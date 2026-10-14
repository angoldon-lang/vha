using System;
using System.Diagnostics;
using System.Linq;
using Vha.Core.Csu;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Tests;
using Vha.Infrastructure.Csu;
using Vha.Infrastructure.Sql;
using Xunit;

namespace Vha.Infrastructure.Tests
{
    /// <summary>
    /// A frozen node is not a stopped node: TCP stays up, nothing answers (VM paused, instance stuck on storage).
    /// docker pause freezes the SQL Server process; every operation must give up in bounded time and leave the state untouched.
    /// </summary>
    public class HangTests
    {
        private static string Short(string cs) => cs + ";Connection Timeout=5";

        private sealed class Frozen : IDisposable
        {
            private readonly string _c;
            public Frozen(string container) { _c = container; FailoverEndToEndTests.Docker("pause " + _c); }
            public void Dispose() { try { FailoverEndToEndTests.Docker("unpause " + _c); } catch (Exception) { } }
        }

        [DockerSqlFact]
        public void Connecting_to_a_frozen_instance_gives_up_at_the_connection_timeout_and_reports_it_as_not_answering()
        {
            Lab.Reset();
            var sw = Stopwatch.StartNew();
            ConnectionResult r;
            using (new Frozen("sqlA"))
            {
                sw.Restart();
                var c = new SqlNodeConnector(NullLog.Instance).Connect(new NodeInfo { VmName = "VM1", ConnectionString = Short(Lab.ConnA("Jelena")) });
                r = new ConnectionResult(c.Status);
            }
            Assert.True(r.Status == ConnectStatus.Timeout || r.Status == ConnectStatus.NotAvailable, r.Status.ToString());
            Assert.InRange(sw.Elapsed.TotalSeconds, 3, 25);
        }

        private sealed class ConnectionResult { public ConnectStatus Status; public ConnectionResult(ConnectStatus s) { Status = s; } }

        [DockerSqlFact]
        public void Release_on_an_instance_that_freezes_after_the_connection_raises_unresponsive_after_the_release_timeout()
        {
            Lab.Reset();
            var d = Lab.Dir("ag01");
            Lab.Exec(Lab.ConnA(), $"CREATE DATABASE Frz ON (NAME=f, FILENAME=N'{d}/data/frz.mdf') LOG ON (NAME=fl, FILENAME=N'{d}/log/frz.ldf')");
            using var conn = new SqlNodeConnector(NullLog.Instance, releaseTimeoutSeconds: 10).Connect(new NodeInfo { VmName = "VM1", ConnectionString = Lab.ConnA("Jelena") });
            Assert.Equal(ConnectStatus.Connected, conn.Status);
            Stopwatch sw;
            using (new Frozen("sqlA"))
            {
                sw = Stopwatch.StartNew();
                var ex = Assert.Throws<NodeUnresponsiveException>(() => conn.Node.ReleaseDatabases(new[] { new ManagedDatabase { Name = "Frz" } }));
                sw.Stop();
                Assert.Contains("timed out", ex.Message);
            }
            Assert.InRange(sw.Elapsed.TotalSeconds, 8, 40);
            // after the thaw nothing was half done
            Assert.NotNull(Lab.Scalar(Lab.ConnA(), "SELECT 1 FROM sys.databases WHERE name = 'Frz'"));
        }

        [DockerSqlFact]
        public void A_peer_that_freezes_between_probe_and_copy_is_skipped_or_rolled_back_in_bounded_time_without_touching_the_local_state()
        {
            Lab.Reset();
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.SCN VALUES (1, 50)");   // A is ahead of B
            Lab.Exec(Lab.ConnB("Jelena"), "UPDATE dbo.JNodes SET ConnectionString = '" + Short(Lab.ConnA("Jelena")) + "' WHERE VMName = 'VM1'");
            var nodeA = new NodeInfo { VmName = "VM1", ConnectionString = Short(Lab.ConnA("Jelena")) };

            // 1. the probe itself on a frozen peer
            Stopwatch sw;
            using (new Frozen("sqlA"))
            {
                sw = Stopwatch.StartNew();
                var probeError = Assert.ThrowsAny<Exception>(() => new SqlPeerProbe().Probe(nodeA, 1));
                Console.WriteLine("probe error after " + sw.Elapsed.TotalSeconds + "s: " + probeError.GetType().Name + ": " + probeError.Message);
            }
            Assert.InRange(sw.Elapsed.TotalSeconds, 3, 25);

            // 2. the peer freezes after the probe, before the copy: the transaction is rolled back, the local configuration untouched
            using (var storeB = new SqlCsuStore(Lab.ConnB("Jelena")))
            {
                storeB.BeginTransaction();
                using (new Frozen("sqlA"))
                {
                    sw = Stopwatch.StartNew();
                    var copyError = Assert.ThrowsAny<Exception>(() => storeB.CopyFromPeer(nodeA, 1));
                    Console.WriteLine("copy error after " + sw.Elapsed.TotalSeconds + "s: " + copyError.GetType().Name + ": " + copyError.Message);
                }
                storeB.Rollback();
            }
            Assert.InRange(sw.Elapsed.TotalSeconds, 0, 25);   // SqlClient caches a failed connect for a few seconds, so the second attempt may fail at once
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));

            // 3. a whole CSU run with a frozen peer completes (peer counted as unreachable) in bounded time and leaves the working flag at N
            using (new Frozen("sqlA"))
            {
                sw = Stopwatch.StartNew();
                var r = new CsuCoordinator("VM2", "AG01", new SqlCsuStore(Lab.ConnB("Jelena")), new SqlPeerProbe(), new EmptyInventory(), new SqlLocalDatabases(Lab.ConnB("Jelena")), NullLog.Instance).Run();
                Assert.Equal(1, r.UnreachablePeers);
            }
            Assert.InRange(sw.Elapsed.TotalSeconds, 0, 40);
            Assert.Equal("N", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT Is_CSU_Working FROM dbo.JAGroups")));

            // 4. after the thaw the next run replicates normally (SqlClient remembers a failed connect for a few seconds; the CSU runs once a minute)
            System.Data.SqlClient.SqlConnection.ClearAllPools();   // CsuCmd is a new process every minute, so it never carries the pool's failure memory over
            var again = new CsuCoordinator("VM2", "AG01", new SqlCsuStore(Lab.ConnB("Jelena")), new SqlPeerProbe(), new EmptyInventory(), new SqlLocalDatabases(Lab.ConnB("Jelena")), NullLog.Instance).Run();
            Assert.True(again.Synced);
            Assert.Equal("50", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));
        }

        private sealed class EmptyInventory : IDiskInventory
        {
            public Vha.Core.Vmware.DiskJoinResult Detect() => new Vha.Core.Vmware.DiskJoinResult();
            public void StampSlot(Vha.Core.Vmware.DetectedDisk d, string slot) { }
        }
    }
}

namespace Vha.Infrastructure.Tests
{
    public class StartupRaceTests
    {
        [DockerSqlFact]
        public void The_service_waits_for_a_local_sql_server_that_is_still_starting()
        {
            Lab.Reset();
            FailoverEndToEndTests.Docker("stop -t 0 sqlB");
            try
            {
                var starter = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(5000); FailoverEndToEndTests.Docker("start sqlB"); });
                starter.Start();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var attempts = 0;
                using (var cfg = Vha.Core.Util.Retry.Run(() => new Vha.Infrastructure.Sql.SqlConfigStore(Lab.ConnB("Jelena") + ";Connection Timeout=3", ""), 30, TimeSpan.FromSeconds(3),
                    Vha.Infrastructure.Sql.SqlNodeConnector.IsTransient, (n, ex) => attempts = n))
                {
                    Assert.NotNull(cfg.GetAg("AG01"));
                }
                starter.Join();
                Assert.True(attempts >= 2, "expected at least one retry, got " + attempts);
                Assert.InRange(sw.Elapsed.TotalSeconds, 5, 90);
            }
            finally { FailoverEndToEndTests.Docker("start sqlB"); }
        }

        [SqlFact]
        public void A_wrong_password_is_not_retried()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); int retries = 0;
            Assert.Throws<System.Data.SqlClient.SqlException>(() => Vha.Core.Util.Retry.Run(() => new Vha.Infrastructure.Sql.SqlConfigStore(Lab.Conn(14331, "Jelena", "sa", "wrong"), ""), 10, TimeSpan.FromSeconds(5),
                Vha.Infrastructure.Sql.SqlNodeConnector.IsTransient, (n, ex) => retries++));
            Assert.Equal(0, retries);
            Assert.InRange(sw.Elapsed.TotalSeconds, 0, 10);
        }
    }
}
