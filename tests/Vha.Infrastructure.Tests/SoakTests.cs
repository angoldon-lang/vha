using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using Vha.Core.Config;
using Vha.Core.Csu;
using Vha.Core.Failover;
using Vha.Core.Model;
using Vha.Core.Tests;
using Vha.Core.Vmware;
using Vha.Infrastructure.Csu;
using Vha.Infrastructure.Sql;
using Xunit;

namespace Vha.Infrastructure.Tests
{
    /// <summary>
    /// Endurance: many consecutive failovers between two real SQL Server instances while a client keeps inserting rows.
    /// Every insert acknowledged by SQL Server must be in the database at the end (no committed data lost, no duplicates),
    /// and the configuration must stay coherent after every move.
    /// </summary>
    public class SoakTests
    {
        private sealed class Inventory : IDiskInventory
        {
            public DiskJoinResult Detect() => new DiskJoinResult();
            public void StampSlot(DetectedDisk d, string slot) { }
        }

        [SqlFact]
        public void Twenty_consecutive_failovers_under_continuous_writes_lose_no_acknowledged_row()
        {
            Lab.Reset();
            var d = Lab.Dir("ag01");
            Lab.Exec(Lab.ConnA(), $"CREATE DATABASE Sales ON PRIMARY (NAME=s1, FILENAME=N'{d}/data/Sales.mdf'), FILEGROUP FG2 (NAME=s2, FILENAME=N'{d}/data/Sales_2.ndf') LOG ON (NAME=sl, FILENAME=N'{d}/log/Sales_log.ldf')");
            Lab.Exec(Lab.ConnA(), "USE Sales; CREATE TABLE t (id INT PRIMARY KEY, v CHAR(200) NOT NULL) ON FG2; CHECKPOINT");
            Lab.Exec(Lab.ConnA(), $"CREATE DATABASE Dummy01 ON PRIMARY (NAME=d1, FILENAME=N'{d}/data/Dummy01.mdf') LOG ON (NAME=d1l, FILENAME=N'{d}/log/Dummy01_log.ldf')");
            Lab.RunScript(Lab.ConnA("Jelena"), "03_register_databases.sql", new Dictionary<string, string> { { "AgName", "AG01" }, { "MountRoot", Lab.Shared + "/ag01/" } });
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JDisks VALUES (1, '2016', N'[DS1] a/data.vmdk', 100, 'AAA', 1), (1, '2017', N'[DS1] a/log.vmdk', 50, 'BBB', 1)");
            new SqlCsuStore(Lab.ConnA("Jelena")).DescribeDatabases(1);

            var journal = new Journal();
            var vc = new FakeVCenter(journal).AddVm("VM1").AddVm("VM2");
            vc.Attach("VM1", "2016", "[DS1] a/data.vmdk", "AAA").Attach("VM1", "2017", "[DS1] a/log.vmdk", "BBB");
            var log = new ListLog();

            string currentOwner = "VM1";            // where the client believes the database lives
            var acked = new ConcurrentBag<int>();
            var stop = false; int nextId = 1; long writerErrors = 0; var errorSamples = new ConcurrentQueue<string>();
            var writer = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var conn = currentOwner == "VM1" ? Lab.ConnA("Sales") : Lab.ConnB("Sales");
                    try
                    {
                        using (var c = new SqlConnection(conn + ";Max Pool Size=5")) { c.Open(); using (var cmd = new SqlCommand("INSERT t VALUES (@i, 'x')", c)) { cmd.Parameters.AddWithValue("@i", nextId); cmd.ExecuteNonQuery(); } }
                        acked.Add(nextId); nextId++;
                    }
                    catch (SqlException ex) { Interlocked.Increment(ref writerErrors); SqlConnection.ClearAllPools(); if (errorSamples.Count < 6) errorSamples.Enqueue("[" + currentOwner + "] " + ex.Number + ": " + ex.Message); Thread.Sleep(30); }
                    catch (InvalidOperationException) { Interlocked.Increment(ref writerErrors); Thread.Sleep(30); }
                }
            }) { IsBackground = true };
            writer.Start();
            Thread.Sleep(500);

            const int cycles = 20;
            for (int i = 1; i <= cycles; i++)
            {
                var target = i % 2 == 1 ? "VM2" : "VM1";
                var targetConn = target == "VM2" ? Lab.ConnB("Jelena") : Lab.ConnA("Jelena");
                var otherConn = target == "VM2" ? Lab.ConnA("Jelena") : Lab.ConnB("Jelena");
                // the CSU of the target node replicates the configuration of the node that is about to give the databases away
                new CsuCoordinator(target, "AG01", new SqlCsuStore(targetConn), new SqlPeerProbe(), new Inventory(), new SqlLocalDatabases(targetConn), log).Run();
                Thread.Sleep(150);                   // the client keeps writing to the old owner until the very last moment
                var ini = IniDocument.Parse($"[VIM]\nVirtualMachineName={target}\n[AG]\nJelena-AG01=AG01\nReservation=N\nTaskName=CSU\nInstance=x\n[Failover]\nReleaseTimeoutSeconds=60\n");
                var settings = ServiceSettings.FromIni(ini, "Jelena-AG01");
                FailoverResult result;
                using (var cfg = new SqlConfigStore(targetConn, ""))
                {
                    result = new FailoverCoordinator(settings, cfg, vc, new SqlNodeConnector(log, 60), new FakeGuest(journal), new LocalSql(targetConn, "", 1, cfg, log), new FakeCsuTask(journal), new FakeClock(), log).Run();
                }
                Assert.True(result.Outcome == FailoverOutcome.Completed, "cycle " + i + ": " + result.Outcome);
                currentOwner = target;
                // coherence after every move
                Assert.Equal(target, Convert.ToString(Lab.Scalar(targetConn, "SELECT CurrentOwner FROM dbo.JAGroups")));
                Assert.Equal("2", Convert.ToString(Lab.Scalar(targetConn, "SELECT COUNT(*) FROM dbo.JDisks WHERE Attached = 1")));
                Assert.Equal(2, vc.Disks[target].Count); Assert.Empty(vc.Disks[target == "VM1" ? "VM2" : "VM1"]);
                Assert.Null(Lab.Scalar(target == "VM2" ? Lab.ConnA() : Lab.ConnB(), "SELECT 1 FROM sys.databases WHERE name IN ('Sales', 'Dummy01')"));
                Assert.Equal("2", Convert.ToString(Lab.Scalar(target == "VM2" ? Lab.ConnB() : Lab.ConnA(), "SELECT COUNT(*) FROM sys.databases WHERE name IN ('Sales', 'Dummy01') AND state = 0")));
                Thread.Sleep(150);
            }
            Volatile.Write(ref stop, true);
            writer.Join(10000);

            var finalConn = currentOwner == "VM1" ? Lab.ConnA("Sales") : Lab.ConnB("Sales");
            var present = Lab.Rows(finalConn, "SELECT id FROM t").Select(r => int.Parse(r[0])).ToList();
            var ackedList = acked.ToList();
            var lost = ackedList.Except(present).ToList();
            Assert.True(lost.Count == 0, lost.Count + " acknowledged rows lost, e.g. " + string.Join(",", lost.Take(10)));
            Assert.Equal(present.Count, present.Distinct().Count());
            // liveness only (speed depends on the machine and the SQL Server version): the correctness checks above are what matters
            Assert.True(ackedList.Count > 200, "the writer acknowledged only " + ackedList.Count + " rows; errors: " + writerErrors + " e.g. " + string.Join(" | ", errorSamples));
            Console.WriteLine("failovers: " + cycles + ", acknowledged rows: " + ackedList.Count + ", rows in the database: " + present.Count + ", writer errors during moves: " + writerErrors);
            // the database is healthy after all that
            Assert.Equal("0", Convert.ToString(Lab.Scalar(finalConn, "SELECT COUNT(*) FROM sys.dm_db_mirroring_auto_page_repair") ?? "0"));
            Lab.Exec(finalConn, "DBCC CHECKDB WITH NO_INFOMSGS");
        }
    }
}
