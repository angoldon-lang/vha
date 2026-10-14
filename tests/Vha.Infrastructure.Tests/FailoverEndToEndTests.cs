using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// The whole failover decision logic (FailoverCoordinator) with the REAL SQL side (config store, node connector, local attach)
    /// on two real SQL Server 2025 instances. Only vCenter, the guest disk cmdlets and the scheduled task are simulated.
    /// </summary>
    public class FailoverEndToEndTests
    {
        private const string Data = "[DS1] SQLVM/data.vmdk";
        private const string Logv = "[DS1] SQLVM/log.vmdk";
        private const string Db = "Sales";

        private sealed class FakeInventory : IDiskInventory
        {
            public DiskJoinResult Result = new DiskJoinResult();
            public DiskJoinResult Detect() => Result;
            public void StampSlot(DetectedDisk d, string slot) { }
        }

        private sealed class Env
        {
            public Journal J = new Journal();
            public FakeVCenter VC; public FakeCsuTask Csu; public FakeGuest Guest; public FakeClock Clock = new FakeClock(); public ListLog Log = new ListLog();
            public Env()
            {
                VC = new FakeVCenter(J); Csu = new FakeCsuTask(J); Guest = new FakeGuest(J);
                VC.AddVm("VM1").AddVm("VM2");
                VC.Attach("VM1", "2016", Data, "AAA").Attach("VM1", "2017", Logv, "BBB");
            }

            public FailoverResult FailoverTo(string vm, string conn)
            {
                var ini = IniDocument.Parse($"[VIM]\nVirtualMachineName={vm}\n[AG]\nJelena-AG01=AG01\nReservation=N\nTaskName=CSU\nInstance=x\n");
                var settings = ServiceSettings.FromIni(ini, "Jelena-AG01");
                using (var cfg = new SqlConfigStore(conn, ""))
                {
                    var c = new FailoverCoordinator(settings, cfg, VC, new SqlNodeConnector(Log), Guest, new LocalSql(conn, "", 1, cfg, Log), Csu, Clock, Log);
                    return c.Run();
                }
            }
        }

        private static Env Prepare(int rows = 1000)
        {
            Lab.Reset();
            var d = Lab.Dir("ag01");
            Lab.Exec(Lab.ConnA(), $"CREATE DATABASE {Db} ON PRIMARY (NAME=s1, FILENAME=N'{d}/data/Sales.mdf'), FILEGROUP FG2 (NAME=s2, FILENAME=N'{d}/data/Sales_2.ndf') LOG ON (NAME=sl, FILENAME=N'{d}/log/Sales_log.ldf')");
            Lab.Exec(Lab.ConnA(), $"USE {Db}; CREATE TABLE t (id INT PRIMARY KEY, v CHAR(100) NOT NULL) ON FG2; INSERT t SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b");
            Lab.Exec(Lab.ConnA(), $"CREATE DATABASE Dummy01 ON PRIMARY (NAME=d1, FILENAME=N'{d}/data/Dummy01.mdf') LOG ON (NAME=d1l, FILENAME=N'{d}/log/Dummy01_log.ldf')");
            Lab.RunScript(Lab.ConnA("Jelena"), "03_register_databases.sql", new Dictionary<string, string> { { "AgName", "AG01" }, { "MountRoot", Lab.Shared + "/ag01/" } });
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JDisks VALUES (1, '2016', N'" + Data + "', 100, 'AAA', 1), (1, '2017', N'" + Logv + "', 50, 'BBB', 1)");
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.SCN VALUES (1, 10)");
            // the CSU describes the databases on the active node and replicates the configuration to the passive one
            new SqlCsuStore(Lab.ConnA("Jelena")).DescribeDatabases(1);
            var r = new CsuCoordinator("VM2", "AG01", new SqlCsuStore(Lab.ConnB("Jelena")), new SqlPeerProbe(), new FakeInventory(), new SqlLocalDatabases(Lab.ConnB("Jelena")), new ListLog()).Run();
            Assert.True(r.Synced);
            return new Env();
        }

        private static int Rows(string conn, string db) => Convert.ToInt32(Lab.Scalar(conn, $"SELECT COUNT(*) FROM [{db}].dbo.t"));
        private static bool Exists(string conn, string db) => Lab.Scalar(conn, "SELECT 1 FROM sys.databases WHERE name = @n", ("@n", db)) != null;

        [SqlFact]
        public void Planned_failover_A_to_B_and_back_with_real_databases()
        {
            var env = Prepare();
            var r = env.FailoverTo("VM2", Lab.ConnB("Jelena"));

            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.False(Exists(Lab.ConnA(), Db)); Assert.False(Exists(Lab.ConnA(), "Dummy01"));
            Assert.Equal(1000, Rows(Lab.ConnB(), Db));
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT state FROM sys.databases WHERE name = 'Sales'")));
            Assert.Equal("VM2", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT CurrentOwner FROM dbo.JAGroups")));
            Assert.Equal("11", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));
            Assert.Equal(2, env.VC.Disks["VM2"].Count); Assert.Empty(env.VC.Disks["VM1"]);
            Assert.Equal("csu-enable", env.J.Events.Last());

            // the CSU on the old node replicates the new situation, then we fail back
            var csu = new CsuCoordinator("VM1", "AG01", new SqlCsuStore(Lab.ConnA("Jelena")), new SqlPeerProbe(), new FakeInventory(), new SqlLocalDatabases(Lab.ConnA("Jelena")), env.Log).Run();
            Assert.True(csu.Synced);
            Assert.Equal("VM2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT CurrentOwner FROM dbo.JAGroups")));

            r = env.FailoverTo("VM1", Lab.ConnA("Jelena"));
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.False(Exists(Lab.ConnB(), Db));
            Assert.Equal(1000, Rows(Lab.ConnA(), Db));
            Assert.Equal("VM1", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT CurrentOwner FROM dbo.JAGroups")));
        }

        [SqlFact]
        public void Node_restart_while_vcenter_is_down_brings_the_databases_online_from_local_evidence()
        {
            var env = Prepare();
            env.FailoverTo("VM2", Lab.ConnB("Jelena"));
            // VM2 reboots (databases offline, service restarts) and vCenter is gone
            Lab.Exec(Lab.ConnB(), "ALTER DATABASE Sales SET OFFLINE WITH ROLLBACK IMMEDIATE");
            env.VC.Down = true;
            var r = env.FailoverTo("VM2", Lab.ConnB("Jelena"));
            Assert.Equal(FailoverOutcome.StartedWithoutVCenter, r.Outcome);
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT state FROM sys.databases WHERE name = 'Sales'")));
            Assert.Equal(1000, Rows(Lab.ConnB(), Db));
            Assert.Equal("csu-enable", env.J.Events.Last());
        }

        [SqlFact]
        public void Failover_to_the_other_node_while_vcenter_is_down_is_refused_and_nothing_changes()
        {
            var env = Prepare();
            env.VC.Down = true;
            env.Guest.LocalMissing.Add("AAA"); env.Guest.LocalMissing.Add("BBB");   // VM2 does not see the disks: they are on VM1
            var ex = Assert.Throws<FailoverException>(() => env.FailoverTo("VM2", Lab.ConnB("Jelena")));
            Assert.Contains("without vCenter", ex.Message);
            Assert.Equal(1000, Rows(Lab.ConnA(), Db));
            Assert.False(Exists(Lab.ConnB(), Db));
            Assert.Equal("VM1", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT CurrentOwner FROM dbo.JAGroups")));
            Assert.Equal("csu-enable", env.J.Events.Last());
        }

        [SqlFact]
        public void Rerunning_the_service_on_the_active_node_is_harmless()
        {
            var env = Prepare();
            env.FailoverTo("VM2", Lab.ConnB("Jelena"));
            var r = env.FailoverTo("VM2", Lab.ConnB("Jelena"));
            Assert.Equal(FailoverOutcome.AlreadyOwner, r.Outcome);
            Assert.Equal(1000, Rows(Lab.ConnB(), Db));
            Assert.Equal("11", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));   // no SCN churn
        }

        [SqlFact]
        public void Failover_with_the_previous_owner_sql_stopped_and_the_vm_powered_off_attaches_cleanly_on_the_new_node()
        {
            var env = Prepare();
            // the old node's SQL instance is detached by hand to emulate the files being moved with the disks
            using (var c = new SqlNodeConnector(env.Log).Connect(new NodeInfo { VmName = "VM1", ConnectionString = Lab.ConnA("Jelena") }))
                c.Node.ReleaseDatabases(new[] { new ManagedDatabase { Name = Db }, new ManagedDatabase { Name = "Dummy01" } });
            env.VC.Vms["VM1"].PowerState = "PoweredOff";
            Lab.Exec(Lab.ConnB("Jelena"), "UPDATE dbo.JNodes SET ConnectionString = '" + Lab.Conn(14999, "Jelena") + ";Connection Timeout=3' WHERE VMName = 'VM1'");   // unreachable
            var r = env.FailoverTo("VM2", Lab.ConnB("Jelena"));
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.Equal(1000, Rows(Lab.ConnB(), Db));
        }

        [SqlFact]
        public void Wrong_credentials_for_the_previous_owner_abort_the_failover_before_any_disk_is_touched()
        {
            var env = Prepare();
            Lab.Exec(Lab.ConnB("Jelena"), "UPDATE dbo.JNodes SET ConnectionString = '" + Lab.Conn(14331, "Jelena", "sa", "bad-password") + "' WHERE VMName = 'VM1'");
            Assert.ThrowsAny<Exception>(() => env.FailoverTo("VM2", Lab.ConnB("Jelena")));
            Assert.Equal(2, env.VC.Disks["VM1"].Count);                // still attached to the old owner
            Assert.DoesNotContain(env.J.Events, e => e.StartsWith("remove") || e.StartsWith("add"));
            Assert.Equal(1000, Rows(Lab.ConnA(), Db));                 // old owner still serves the database
            Assert.Equal("csu-enable", env.J.Events.Last());
        }

        /// <summary>
        /// The node dies in the middle of work: its SQL Server process is killed (SIGKILL) with an open transaction and dirty pages.
        /// The new node must be able to attach the database from its files (crash recovery) and see exactly the committed rows.
        /// </summary>
        [DockerSqlFact]
        public void Killed_node_with_uncommitted_work_database_recovers_on_attach_with_committed_data_only()
        {
            var env = Prepare(rows: 1000);
            // A database that has been running for a while has been checkpointed. (SQL Server itself cannot redo the very first,
            // never checkpointed, operations on a brand new secondary data file after a SIGKILL: reproduced with plain sqlcmd, see docs/ANALISI.md.)
            Lab.Exec(Lab.ConnA(Db), "CHECKPOINT"); Lab.Exec(Lab.ConnA("Dummy01"), "CHECKPOINT");
            using (var client = new System.Data.SqlClient.SqlConnection(Lab.ConnA(Db)))
            {
                client.Open();
                using (var cmd = new System.Data.SqlClient.SqlCommand("BEGIN TRAN; INSERT t SELECT TOP (5000) 100000 + ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 'uncommitted' FROM sys.all_objects a CROSS JOIN sys.all_objects b; UPDATE t SET v = 'dirty' WHERE id <= 500;", client)) cmd.ExecuteNonQuery();
                Docker("kill sqlA");            // SIGKILL: no checkpoint, no clean shutdown, transaction still open
            }
            try
            {
                env.VC.Vms["VM1"].PowerState = "PoweredOff";
                Lab.Exec(Lab.ConnB("Jelena"), "UPDATE dbo.JNodes SET ConnectionString = '" + Lab.Conn(14331, "Jelena") + ";Connection Timeout=3' WHERE VMName = 'VM1'");   // dead node
                var r = env.FailoverTo("VM2", Lab.ConnB("Jelena"));

                Assert.Equal(FailoverOutcome.Completed, r.Outcome);
                Assert.Equal(1000, Rows(Lab.ConnB(), Db));
                Assert.Equal(0, Convert.ToInt32(Lab.Scalar(Lab.ConnB(), $"SELECT COUNT(*) FROM [{Db}].dbo.t WHERE v = 'dirty' OR v = 'uncommitted'")));
                Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT state FROM sys.databases WHERE name = 'Sales'")));
                Assert.Equal(1, Convert.ToInt32(Lab.Scalar(Lab.ConnB(), "SELECT COUNT(*) FROM sys.databases WHERE name = 'Dummy01' AND state = 0")));
            }
            finally
            {
                // hand the files back and revive node A so that the next tests have their lab
                try { using (var c = new SqlNodeConnector(env.Log).Connect(new NodeInfo { VmName = "VM2", ConnectionString = Lab.ConnB("Jelena") })) c.Node.ReleaseDatabases(new[] { new ManagedDatabase { Name = Db }, new ManagedDatabase { Name = "Dummy01" } }); } catch (Exception) { }
                Docker("start sqlA");
                for (int i = 0; i < 60; i++) { try { Lab.Exec(Lab.ConnA(), "SELECT 1"); break; } catch (Exception) { Thread.Sleep(2000); } }
            }
        }

        internal static void Docker(string args)
        {
            using (var p = Process.Start(new ProcessStartInfo("docker", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
            { p.WaitForExit(120000); if (p.ExitCode != 0) throw new InvalidOperationException("docker " + args + ": " + p.StandardError.ReadToEnd()); }
        }
    }

    public sealed class DockerSqlFactAttribute : SqlFactAttribute
    {
        public DockerSqlFactAttribute()
        {
            if (Skip != null) return;
            try { FailoverEndToEndTests.Docker("inspect sqlA"); } catch (Exception) { Skip = "docker (containers sqlA/sqlB) not available"; }
        }
    }
}
