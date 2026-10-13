using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using Vha.Core.Config;
using Vha.Core.Csu;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Tests;
using Vha.Core.Vmware;
using Vha.Infrastructure.Csu;
using Vha.Infrastructure.Sql;
using Xunit;

namespace Vha.Infrastructure.Tests
{
    public class SqlIntegrationTests
    {
        private const string OddName = "Sa'les]x";

        /// <summary>DB with 3 filegroups and 2 log files on the "shared disks", with data, on the given node.</summary>
        private static void CreateSalesOnA(string name = OddName, int rows = 1000)
        {
            var d = Lab.Dir("ag01");
            Directory.CreateDirectory(Path.Combine(d, "data")); Directory.CreateDirectory(Path.Combine(d, "log"));
            Lab.Exec(Lab.ConnA(), "CREATE DATABASE " + Q(name) + " ON PRIMARY (NAME=s1, FILENAME=N'" + d + "/data/Sales.mdf'), FILEGROUP FG2 (NAME=s2, FILENAME=N'" + d + "/data/Sales_2.ndf'), FILEGROUP FG3 (NAME=s3, FILENAME=N'" + d + "/data/Sales_3.ndf') LOG ON (NAME=sl, FILENAME=N'" + d + "/log/Sales_log.ldf'), (NAME=sl2, FILENAME=N'" + d + "/log/Sales_log2.ldf')");
            Lab.Exec(Lab.ConnA(), "USE " + Q(name) + "; CREATE TABLE t (id INT PRIMARY KEY, v NVARCHAR(50)) ON FG2; INSERT t SELECT TOP (" + rows + ") ROW_NUMBER() OVER (ORDER BY (SELECT 1)), N'row' FROM sys.all_objects a CROSS JOIN sys.all_objects b");
        }

        private static string Q(string n) => "[" + n.Replace("]", "]]") + "]";

        private static void RegisterOnA()
        {
            Lab.RunScript(Lab.ConnA("Jelena"), "03_register_databases.sql", new Dictionary<string, string> { { "AgName", "AG01" }, { "MountRoot", Lab.Shared + "/ag01/" } });
        }

        private static NodeInfo NodeA => new NodeInfo { VmName = "VM1", DnsName = "vm1.local", ConnectionString = Lab.ConnA("Jelena") };
        private static NodeInfo NodeB => new NodeInfo { VmName = "VM2", DnsName = "vm2.local", ConnectionString = Lab.ConnB("Jelena") };

        // ------------------------------------------------------------------ SQL scripts on a real engine

        [SqlFact]
        public void Schema_script_is_idempotent_and_registration_scripts_work()
        {
            Lab.Reset();
            Lab.RunScript(Lab.ConnA("Jelena"), "01_schema.sql");   // second run must change nothing
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JAGroups")));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JNodes")));
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT CAST(is_local AS INT) FROM dbo.JNodes WHERE VMName='VM1'")));
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT CAST(is_local AS INT) FROM dbo.JNodes WHERE VMName='VM2'")));
            // re-running the registration must not change the owner, duplicate nodes or the SCN
            Lab.Exec(Lab.ConnA("Jelena"), "UPDATE dbo.JAGroups SET CurrentOwner = 'VM2'");
            Lab.RunScript(Lab.ConnA("Jelena"), "02_register_availability_group.sql", new Dictionary<string, string> { { "AgName", "AG01" }, { "VmName", "VM1" }, { "NodeList", "VM1|vm1.local|localhost,14331;VM2|vm2.local|localhost,14332" }, { "Owner", "VM1" }, { "CpuMHz", "100" }, { "MemMB", "200" }, { "CsOptions", "Encrypt=True" } });
            Assert.Equal("VM2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT CurrentOwner FROM dbo.JAGroups")));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JNodes")));
            Assert.Equal("100", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT ResCpuMHz FROM dbo.JAGroups")));
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.SCN")));
        }

        [SqlFact]
        public void Registration_script_rejects_bad_input()
        {
            Lab.Reset(registerAg: false);
            var vars = new Dictionary<string, string> { { "AgName", "AG01" }, { "VmName", "VMX" }, { "NodeList", "VM1|vm1.local|a;VM2|vm2.local|b" }, { "Owner", "VM1" }, { "CpuMHz", "0" }, { "MemMB", "0" }, { "CsOptions", "" } };
            Assert.Throws<SqlException>(() => Lab.RunScript(Lab.ConnA("Jelena"), "02_register_availability_group.sql", vars));   // VmName not in NodeList
            vars["VmName"] = "VM1"; vars["NodeList"] = "VM1|vm1.local|a";
            Assert.Throws<SqlException>(() => Lab.RunScript(Lab.ConnA("Jelena"), "02_register_availability_group.sql", vars));   // fewer than 2 nodes
            vars["NodeList"] = "VM1|vm1.local";
            Assert.Throws<SqlException>(() => Lab.RunScript(Lab.ConnA("Jelena"), "02_register_availability_group.sql", vars));   // malformed item
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JNodes")));            // transaction rolled back
        }

        [SqlFact]
        public void Database_registration_script_registers_only_databases_fully_inside_the_mount_root()
        {
            Lab.Reset();
            CreateSalesOnA();
            Lab.Exec(Lab.ConnA(), "CREATE DATABASE Outside");
            RegisterOnA();
            var names = Lab.Rows(Lab.ConnA("Jelena"), "SELECT DBName FROM dbo.JDatabases").Select(r => r[0]).ToList();
            Assert.Equal(new[] { OddName }, names);   // one row, not one per file
            Assert.Equal("2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));
            RegisterOnA();                              // idempotent
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JDatabases")));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));
        }

        [SqlFact]
        public void Health_check_reports_problems_and_is_silent_on_a_healthy_configuration()
        {
            Lab.Reset();
            var problems = Lab.Rows(Lab.ConnA("Jelena"), HealthCheckQuery()).Select(r => r[1]).ToList();
            Assert.Contains(problems, p => p.Contains("no disks"));
            Assert.Contains(problems, p => p.Contains("no databases"));

            CreateSalesOnA();
            RegisterOnA();
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JDisks (JAGroupsID, DeviceKey, FileName, CapacityGB, UniqueId, Attached) VALUES (1, '2016', N'[ds] a.vmdk', 10, 'AAA', 1), (1, '2017', N'[ds] b.vmdk', 10, 'BBB', 1)");
            new SqlCsuStore(Lab.ConnA("Jelena")).DescribeDatabases(1);
            Lab.Exec(Lab.ConnA("Jelena"), "UPDATE dbo.JAGroups SET CurrentOwner = 'VM1'");
            problems = Lab.Rows(Lab.ConnA("Jelena"), HealthCheckQuery()).Select(r => r[1]).ToList();
            Assert.Empty(problems);

            Lab.Exec(Lab.ConnA("Jelena"), "UPDATE dbo.JDisks SET DeviceKey = '2023' WHERE DeviceKey = '2017'; UPDATE dbo.JDisks SET UniqueId = '' WHERE DeviceKey = '2016'");
            problems = Lab.Rows(Lab.ConnA("Jelena"), HealthCheckQuery()).Select(r => r[1]).ToList();
            Assert.Contains(problems, p => p.Contains("not a SCSI key"));
            Assert.Contains(problems, p => p.Contains("no UniqueId"));
        }

        /// <summary>Script text without USE / GO so that it can be run as one batch from the test connection.</summary>
        private static string ScriptBody(string file)
        {
            var t = Lab.Sql(file).Replace("USE [Jelena];", "").Replace("SET QUOTED_IDENTIFIER ON;", "");
            return System.Text.RegularExpressions.Regex.Replace(t, @"^\s*GO\s*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        private static string HealthCheckQuery()
        {
            var t = Lab.Sql("05_health_check.sql");
            var i = t.IndexOf("SELECT a.Name AS AG", StringComparison.Ordinal);
            return t.Substring(i).Replace("\nGO", "");
        }

        [SqlFact]
        public void Attach_generator_script_produces_a_statement_that_really_attaches_every_file()
        {
            Lab.Reset();
            CreateSalesOnA();
            RegisterOnA();
            new SqlCsuStore(Lab.ConnA("Jelena")).DescribeDatabases(1);
            // run the real script (QUOTED_IDENTIFIER etc. included) and take the AttachScript column
            var rows = Lab.Rows(Lab.ConnA("Jelena"), "SET QUOTED_IDENTIFIER ON;\r\n" + ScriptBody("04_attach_script_generator.sql"));
            Assert.Single(rows);
            var script = rows[0][2];
            Assert.Equal(5, script.Split(new[] { "FILENAME" }, StringSplitOptions.None).Length - 1);
            Lab.Exec(Lab.ConnA(), "ALTER DATABASE " + Q(OddName) + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sp_detach_db @dbname = N'Sa''les]x'");
            Lab.Exec(Lab.ConnA(), script);
            Assert.Equal("1000", Convert.ToString(Lab.Scalar(Lab.ConnA(), "SELECT COUNT(*) FROM " + Q(OddName) + ".dbo.t")));
        }

        // ------------------------------------------------------------------ SqlConfigStore

        [SqlFact]
        public void Config_store_reads_and_writes_the_jelena_tables()
        {
            Lab.Reset();
            Lab.Exec(Lab.ConnA("Jelena"), "UPDATE dbo.JAGroups SET ResCpuMHz = 1500, ResMemMB = 4096");
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JDisks (JAGroupsID, DeviceKey, FileName, CapacityGB, UniqueId, Attached) VALUES (1, '2016', N'[ds] it''s.vmdk', 100.5, ' AAA ', 0)");
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JSettings VALUES ('ConnectVCenter', N'Connect-VIServer vc')");
            using (var s = new SqlConfigStore(Lab.ConnA("Jelena"), ""))
            {
                var ag = s.GetAg("AG01");
                Assert.Equal(1500, ag.ResCpuMHz); Assert.Equal(4096, ag.ResMemMB); Assert.Equal("VM1", ag.CurrentOwner);
                Assert.Null(s.GetAg("nope"));
                Assert.Null(s.GetAg("AG01' OR '1'='1"));
                var nodes = s.GetNodes(ag.Id);
                Assert.Equal(2, nodes.Count); Assert.True(nodes.Single(n => n.VmName == "VM1").IsLocal);
                var d = s.GetDisks(ag.Id).Single();
                Assert.Equal("2016", d.DeviceKey); Assert.Equal("AAA", d.UniqueId); Assert.Equal("[ds] it's.vmdk", d.FileName); Assert.False(d.Attached);
                Assert.Equal("Connect-VIServer vc", s.GetSetting("ConnectVCenter"));
                Assert.Null(s.GetSetting("missing"));
                Assert.Equal(1, s.GetLastScn(ag.Id));

                s.UpdateDisk(ag.Id, d, "[ds2] new's.vmdk", true);
                var d2 = s.GetDisks(ag.Id).Single();
                Assert.True(d2.Attached); Assert.Equal("[ds2] new's.vmdk", d2.FileName);
                Assert.Throws<InvalidOperationException>(() => s.UpdateDisk(ag.Id, new ConfiguredDisk { DeviceKey = "9999" }, "x", true));

                s.SetOwner(ag.Id, "it's me");
                Assert.Equal("it's me", s.GetAg("AG01").CurrentOwner);
                Assert.Throws<InvalidOperationException>(() => s.SetOwner(12345, "x"));

                s.WriteScn(ag.Id, 2);
                Assert.Equal(2, s.GetLastScn(ag.Id));
                Assert.Throws<InvalidOperationException>(() => s.WriteScn(ag.Id, 2));   // duplicate SCN is a conflict, not silently ignored
            }
        }

        // ------------------------------------------------------------------ node release + attach

        [SqlFact]
        public void Node_connector_maps_connection_failures_correctly()
        {
            var log = new ListLog();
            var c = new SqlNodeConnector(log);
            using (var ok = c.Connect(NodeA)) Assert.Equal(ConnectStatus.Connected, ok.Status);

            // nothing listens on the port -> the node does not answer
            Assert.Equal(ConnectStatus.NotAvailable, c.Connect(new NodeInfo { VmName = "X", ConnectionString = Lab.Conn(14999, "master") + ";Connection Timeout=3" }).Status);
            // unresolvable host -> does not answer
            Assert.Equal(ConnectStatus.NotAvailable, c.Connect(new NodeInfo { VmName = "X", ConnectionString = "Server=no-such-host.invalid;User Id=sa;Password=x;TrustServerCertificate=True;Connection Timeout=3" }).Status);

            // reached but refused: configuration errors abort the failover instead of looking like a dead node
            Assert.Throws<SqlException>(() => c.Connect(new NodeInfo { VmName = "X", ConnectionString = Lab.Conn(14331, "master", "sa", "wrong-password") }));
            Assert.Throws<SqlException>(() => c.Connect(new NodeInfo { VmName = "X", ConnectionString = Lab.Conn(14331, "NoSuchDatabase") }));
            // untrusted certificate (self-signed server, Encrypt=True, no TrustServerCertificate) is a configuration problem too
            Assert.Throws<SqlException>(() => c.Connect(new NodeInfo { VmName = "X", ConnectionString = "Server=localhost,14331;User Id=sa;Password=" + Lab.Password + ";Encrypt=True;Connection Timeout=5" }));
        }

        [SqlFact]
        public void Release_detaches_databases_and_leaves_every_file_on_disk_and_skips_missing_ones()
        {
            Lab.Reset();
            CreateSalesOnA();
            var files = Lab.Rows(Lab.ConnA(), "SELECT physical_name FROM sys.master_files WHERE database_id = DB_ID(N'Sa''les]x')").Select(r => r[0]).ToList();
            Assert.Equal(5, files.Count);
            using (var conn = new SqlNodeConnector(NullLog.Instance).Connect(NodeA))
            {
                conn.Node.ReleaseDatabases(new[] { new ManagedDatabase { Name = OddName }, new ManagedDatabase { Name = "NotThere" } });
            }
            Assert.Null(Lab.Scalar(Lab.ConnA(), "SELECT 1 FROM sys.databases WHERE name = N'Sa''les]x'"));
            Assert.All(files, f => Assert.True(File.Exists(f), f));
        }

        [SqlFact]
        public void Release_works_while_clients_are_connected()
        {
            Lab.Reset();
            CreateSalesOnA();
            using (var client = new SqlConnection(Lab.ConnA(OddName)))
            {
                client.Open();
                using (var tx = client.BeginTransaction())
                {
                    using (var cmd = new SqlCommand("INSERT t VALUES (5000, N'open transaction')", client, tx)) cmd.ExecuteNonQuery();
                    using (var conn = new SqlNodeConnector(NullLog.Instance).Connect(NodeA))
                        conn.Node.ReleaseDatabases(new[] { new ManagedDatabase { Name = OddName } });
                }
            }
            Assert.Null(Lab.Scalar(Lab.ConnA(), "SELECT 1 FROM sys.databases WHERE name = N'Sa''les]x'"));
        }

        [SqlFact]
        public void Csu_copy_then_release_then_attach_keeps_data_and_file_layout()
        {
            Lab.Reset();
            CreateSalesOnA();
            RegisterOnA();
            var storeA = new SqlCsuStore(Lab.ConnA("Jelena"));
            storeA.DescribeDatabases(1);

            // B is behind: replicate A -> B inside a transaction, exactly as the CSU does
            using (var storeB = new SqlCsuStore(Lab.ConnB("Jelena")))
            {
                storeB.BeginTransaction();
                storeB.CopyFromPeer(NodeA, 1);
                storeB.WriteScn(1, 2);
                storeB.Commit();
            }
            Assert.Equal(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JDBFiles").ToString(), Lab.Scalar(Lab.ConnB("Jelena"), "SELECT COUNT(*) FROM dbo.JDBFiles").ToString());
            Assert.Equal(OddName, Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT DBName FROM dbo.JDatabases")));
            // node-local state must not travel: the other node keeps its own is_local, and JAGroups keeps its Is_CSU_Working
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT CAST(is_local AS INT) FROM dbo.JNodes WHERE VMName = 'VM2'")));

            using (var conn = new SqlNodeConnector(NullLog.Instance).Connect(NodeA)) conn.Node.ReleaseDatabases(new[] { new ManagedDatabase { Name = OddName } });
            using (var cfg = new SqlConfigStore(Lab.ConnB("Jelena"), ""))
                new LocalSql(Lab.ConnB("Jelena"), "", 1, cfg, NullLog.Instance).BringOnline(cfg.GetDatabases(1));

            Assert.Equal("1000", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT COUNT(*) FROM " + Q(OddName) + ".dbo.t")));
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT state FROM sys.databases WHERE name = N'Sa''les]x'")));
            Assert.Equal("5", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT COUNT(*) FROM sys.master_files WHERE database_id = DB_ID(N'Sa''les]x')")));
            Assert.Equal("3", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT COUNT(*) FROM " + Q(OddName) + ".sys.filegroups")));

            // and back again: B -> A
            using (var conn = new SqlNodeConnector(NullLog.Instance).Connect(NodeB)) conn.Node.ReleaseDatabases(new[] { new ManagedDatabase { Name = OddName } });
            using (var cfg = new SqlConfigStore(Lab.ConnA("Jelena"), ""))
                new LocalSql(Lab.ConnA("Jelena"), "", 1, cfg, NullLog.Instance).BringOnline(cfg.GetDatabases(1));
            Assert.Equal("1000", Convert.ToString(Lab.Scalar(Lab.ConnA(), "SELECT COUNT(*) FROM " + Q(OddName) + ".dbo.t")));
        }

        [SqlFact]
        public void Copy_from_peer_is_atomic_a_failure_leaves_the_target_untouched()
        {
            Lab.Reset();
            CreateSalesOnA(); RegisterOnA();
            new SqlCsuStore(Lab.ConnA("Jelena")).DescribeDatabases(1);
            Lab.Exec(Lab.ConnB("Jelena"), "INSERT dbo.JDatabases VALUES (1, N'keepme', N'x')");
            using (var storeB = new SqlCsuStore(Lab.ConnB("Jelena")))
            {
                storeB.BeginTransaction();
                storeB.CopyFromPeer(NodeA, 1);
                storeB.Rollback();
            }
            Assert.Equal("keepme", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT DBName FROM dbo.JDatabases")));
            // peer unreachable -> exception, nothing changed
            using (var storeB = new SqlCsuStore(Lab.ConnB("Jelena")))
            {
                storeB.BeginTransaction();
                Assert.ThrowsAny<Exception>(() => storeB.CopyFromPeer(new NodeInfo { VmName = "X", ConnectionString = Lab.Conn(14999, "Jelena") + ";Connection Timeout=3" }, 1));
                storeB.Rollback();
            }
            Assert.Equal("keepme", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT DBName FROM dbo.JDatabases")));
        }

        [SqlFact]
        public void Offline_database_is_brought_online_and_one_broken_database_does_not_block_the_others()
        {
            Lab.Reset();
            var d = Lab.Dir("ag01"); Directory.CreateDirectory(d + "/data"); Directory.CreateDirectory(d + "/log");
            foreach (var n in new[] { "Good", "Offline1", "Broken" })
                Lab.Exec(Lab.ConnB(), "CREATE DATABASE " + n + " ON (NAME=" + n + "d, FILENAME=N'" + d + "/data/" + n + ".mdf') LOG ON (NAME=" + n + "l, FILENAME=N'" + d + "/log/" + n + ".ldf')");
            Lab.Exec(Lab.ConnB(), "ALTER DATABASE Offline1 SET OFFLINE WITH ROLLBACK IMMEDIATE; ALTER DATABASE Broken SET OFFLINE WITH ROLLBACK IMMEDIATE");
            File.Delete(d + "/log/Broken.ldf"); File.Delete(d + "/data/Broken.mdf");
            foreach (var n in new[] { "Good", "Offline1", "Broken" }) Lab.Exec(Lab.ConnB("Jelena"), "INSERT dbo.JDatabases VALUES (1, N'" + n + "', N'x'); INSERT dbo.JDBFiles VALUES (1, 1, N'" + n + "', 1, N'" + n + "d', N'" + d + "/data/" + n + ".mdf'), (1, 2, N'" + n + "', 0, N'" + n + "l', N'" + d + "/log/" + n + ".ldf')");
            using (var cfg = new SqlConfigStore(Lab.ConnB("Jelena"), ""))
            {
                var ex = Assert.Throws<FailoverException>(() => new LocalSql(Lab.ConnB("Jelena"), "", 1, cfg, NullLog.Instance).BringOnline(cfg.GetDatabases(1)));
                Assert.Contains("Broken", ex.Message);
                Assert.DoesNotContain("Good:", ex.Message);
                Assert.DoesNotContain("Offline1", ex.Message);
            }
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT state FROM sys.databases WHERE name = 'Offline1'")));
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT state FROM sys.databases WHERE name = 'Good'")));
        }

        [SqlFact]
        public void Database_without_a_file_description_is_reported_not_attached_blindly()
        {
            Lab.Reset();
            Lab.Exec(Lab.ConnB("Jelena"), "INSERT dbo.JDatabases VALUES (1, N'Undescribed', N'x')");
            using (var cfg = new SqlConfigStore(Lab.ConnB("Jelena"), ""))
            {
                var ex = Assert.Throws<FailoverException>(() => new LocalSql(Lab.ConnB("Jelena"), "", 1, cfg, NullLog.Instance).BringOnline(cfg.GetDatabases(1)));
                Assert.Contains("no files described", ex.Message);
            }
        }

        // ------------------------------------------------------------------ CSU against real SQL

        private sealed class FakeInventory : IDiskInventory
        {
            public DiskJoinResult Result = new DiskJoinResult();
            public DiskJoinResult Detect() => Result;
            public void StampSlot(DetectedDisk d, string slot) { }
        }

        private static CsuCoordinator Csu(string vm, string conn, FakeInventory inv, ILog log) =>
            new CsuCoordinator(vm, "AG01", new SqlCsuStore(conn), new SqlPeerProbe(), inv, new SqlLocalDatabases(conn), log);

        [SqlFact]
        public void Csu_run_on_the_stale_node_replicates_from_the_node_that_is_ahead()
        {
            Lab.Reset();
            CreateSalesOnA(); RegisterOnA();
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JDisks VALUES (1, '2016', N'[ds] a.vmdk', 10, 'AAA', 1)");
            var log = new ListLog();
            var r = Csu("VM2", Lab.ConnB("Jelena"), new FakeInventory(), log).Run();
            Assert.True(r.Synced);
            Assert.Equal(OddName, Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT DBName FROM dbo.JDatabases")));
            Assert.Equal("[ds] a.vmdk", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT FileName FROM dbo.JDisks")));
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT CAST(Attached AS INT) FROM dbo.JDisks")));   // node local state is not copied
            Assert.Equal("N", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT Is_CSU_Working FROM dbo.JAGroups")));
            Assert.Equal(Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")), Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));
            Assert.Equal("VM1", Convert.ToString(Lab.Scalar(Lab.ConnB("Jelena"), "SELECT CurrentOwner FROM dbo.JAGroups")));
        }

        [SqlFact]
        public void Csu_with_the_peer_down_still_reconciles_the_local_disks()
        {
            Lab.Reset();
            Lab.Exec(Lab.ConnA("Jelena"), "UPDATE dbo.JNodes SET ConnectionString = '" + Lab.Conn(14999, "Jelena") + ";Connection Timeout=3' WHERE VMName = 'VM2'");
            var inv = new FakeInventory();
            inv.Result.Detected.Add(new DetectedDisk { DeviceKey = "2016", AgName = "AG01", FileName = "[ds] a.vmdk", CapacityGB = 10, UniqueId = "AAA" });
            var r = Csu("VM1", Lab.ConnA("Jelena"), inv, new ListLog()).Run();
            Assert.Equal(1, r.UnreachablePeers);
            Assert.True(r.DisksChanged);
            Assert.Equal("1", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT CAST(Attached AS INT) FROM dbo.JDisks WHERE DeviceKey = '2016'")));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));
            // second run: nothing to do, no SCN churn
            r = Csu("VM1", Lab.ConnA("Jelena"), inv, new ListLog()).Run();
            Assert.False(r.DisksChanged);
            Assert.Equal("2", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT MAX(LastSCN) FROM dbo.SCN")));
        }

        [SqlFact]
        public void Csu_drops_only_true_ghosts_never_a_database_with_surviving_files()
        {
            Lab.Reset();
            var d = Lab.Dir("ag01"); Directory.CreateDirectory(d + "/data"); Directory.CreateDirectory(d + "/log");
            foreach (var n in new[] { "GhostAllGone", "LostLogOnly", "Unmanaged" })
                Lab.Exec(Lab.ConnB(), "CREATE DATABASE " + n + " ON (NAME=" + n + "d, FILENAME=N'" + d + "/data/" + n + ".mdf') LOG ON (NAME=" + n + "l, FILENAME=N'" + d + "/log/" + n + ".ldf')");
            Lab.Exec(Lab.ConnB(), string.Join(";", new[] { "GhostAllGone", "LostLogOnly", "Unmanaged" }.Select(n => "ALTER DATABASE " + n + " SET OFFLINE WITH ROLLBACK IMMEDIATE")));
            File.Delete(d + "/data/GhostAllGone.mdf"); File.Delete(d + "/log/GhostAllGone.ldf");
            File.Delete(d + "/log/LostLogOnly.ldf");                                   // the mdf survives
            File.Delete(d + "/data/Unmanaged.mdf"); File.Delete(d + "/log/Unmanaged.ldf");   // gone, but not ours
            foreach (var n in new[] { "GhostAllGone", "LostLogOnly" }) Lab.Exec(Lab.ConnB("Jelena"), "INSERT dbo.JDatabases VALUES (1, N'" + n + "', N'x')");

            var log = new ListLog();
            var r = Csu("VM2", Lab.ConnB("Jelena"), new FakeInventory(), log).Run();   // owner is VM1: B does not own the disks
            Assert.Equal(new[] { "GhostAllGone" }, r.Dropped);
            var left = Lab.Rows(Lab.ConnB(), "SELECT name FROM sys.databases WHERE database_id > 4 AND name <> 'Jelena' ORDER BY name").Select(x => x[0]).ToList();
            Assert.Equal(new[] { "LostLogOnly", "Unmanaged" }, left);
            Assert.True(File.Exists(d + "/data/LostLogOnly.mdf"));
            Assert.True(log.Has("never drop a database with surviving files"));
        }

        [SqlFact]
        public void Csu_on_the_owner_node_never_drops()
        {
            Lab.Reset();
            var d = Lab.Dir("ag01"); Directory.CreateDirectory(d + "/data"); Directory.CreateDirectory(d + "/log");
            Lab.Exec(Lab.ConnB(), "CREATE DATABASE Gone ON (NAME=g, FILENAME=N'" + d + "/data/Gone.mdf') LOG ON (NAME=gl, FILENAME=N'" + d + "/log/Gone.ldf'); ALTER DATABASE Gone SET OFFLINE WITH ROLLBACK IMMEDIATE");
            File.Delete(d + "/data/Gone.mdf"); File.Delete(d + "/log/Gone.ldf");
            Lab.Exec(Lab.ConnB("Jelena"), "INSERT dbo.JDatabases VALUES (1, N'Gone', N'x'); UPDATE dbo.JAGroups SET CurrentOwner = 'VM2'");
            var r = Csu("VM2", Lab.ConnB("Jelena"), new FakeInventory(), new ListLog()).Run();
            Assert.Empty(r.Dropped);
            Assert.NotNull(Lab.Scalar(Lab.ConnB(), "SELECT 1 FROM sys.databases WHERE name = 'Gone'"));
        }

        [SqlFact]
        public void Csu_describe_skips_offline_or_missing_databases_and_refreshes_changed_layouts()
        {
            Lab.Reset();
            CreateSalesOnA(); RegisterOnA();
            Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JDatabases VALUES (1, N'NotCreatedYet', N'x')");
            var store = new SqlCsuStore(Lab.ConnA("Jelena"));
            store.DescribeDatabases(1);
            Assert.Equal("5", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JDBFiles")));
            Lab.Exec(Lab.ConnA(), "ALTER DATABASE " + Q(OddName) + " ADD FILE (NAME=s4, FILENAME=N'" + Lab.Dir("ag01") + "/data/Sales_4.ndf') TO FILEGROUP FG3");
            store.DescribeDatabases(1);
            Assert.Equal("6", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JDBFiles")));
            Lab.Exec(Lab.ConnA(), "ALTER DATABASE " + Q(OddName) + " SET OFFLINE WITH ROLLBACK IMMEDIATE");
            Lab.Exec(Lab.ConnA("Jelena"), "DELETE dbo.JDBFiles");
            store.DescribeDatabases(1);   // offline: not touched
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JDBFiles")));
        }

        [SqlFact]
        public void Csu_store_basics_ensure_schema_workflags_disk_operations()
        {
            Lab.Reset();
            Lab.Exec(Lab.ConnA("Jelena"), "DROP TABLE dbo.JXLog; DROP TABLE dbo.JDBFiles; DROP TABLE dbo.JDBFileGroups");
            using (var s = new SqlCsuStore(Lab.ConnA("Jelena")))
            {
                s.EnsureSchema(); s.EnsureSchema();
                Assert.Equal("3", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM sys.tables WHERE name IN ('JXLog','JDBFiles','JDBFileGroups')")));
                Assert.Equal(1, s.GetAgId("AG01"));
                Assert.Throws<InvalidOperationException>(() => s.GetAgId("nope"));
                s.SetWorking(1, true);
                Assert.Equal("Y", Convert.ToString(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT Is_CSU_Working FROM dbo.JAGroups")));
                s.SetWorking(1, false);

                s.BeginTransaction();
                s.ApplyDiskOperation(1, new DiskOperation { Op = DiskOp.Insert, DeviceKey = "2016", FileName = "[d] it's.vmdk", CapacityGB = 10, UniqueId = "AAA" });
                s.ApplyDiskOperation(1, new DiskOperation { Op = DiskOp.Update, DeviceKey = "2016", FileName = "[d] moved.vmdk", CapacityGB = 20 });
                s.Commit();
                var disk = s.GetDisks(1).Single();
                Assert.Equal("[d] moved.vmdk", disk.FileName); Assert.True(disk.Attached); Assert.Equal(20, disk.CapacityGB);

                s.BeginTransaction();
                s.ApplyDiskOperation(1, new DiskOperation { Op = DiskOp.MarkDetached, DeviceKey = "2016" });
                s.Rollback();
                Assert.True(s.GetDisks(1).Single().Attached);   // rolled back
                Assert.Throws<InvalidOperationException>(() => s.ApplyDiskOperation(1, new DiskOperation { Op = DiskOp.MarkDetached, DeviceKey = "9999" }));

                Assert.Single(s.GetRemoteNodes(1));
                Assert.Equal("VM2", s.GetRemoteNodes(1).Single().VmName);
                s.SetOwner(1, "VM9"); Assert.Equal("VM9", s.GetOwner(1));
            }
        }

        [SqlFact]
        public void Local_connection_string_accepts_a_full_connection_string_or_an_instance_name()
        {
            Assert.StartsWith("Server=host\\inst;Database=Jelena;Trusted_Connection=Yes", SqlHelper.LocalConnectionString("host\\inst", ""));
            Assert.Contains("Encrypt=True", SqlHelper.LocalConnectionString("host\\inst", "Encrypt=True"));
            var full = SqlHelper.LocalConnectionString("Server=h,1433;User Id=u;Password=p;", "TrustServerCertificate=True");
            Assert.Contains("Database=Jelena", full); Assert.Contains("Connection Timeout=15", full); Assert.EndsWith("TrustServerCertificate=True", full);
            Assert.Throws<ArgumentException>(() => SqlHelper.LocalConnectionString(" ", ""));
            Assert.Equal("Server=h;Initial Catalog=Other", SqlHelper.LocalConnectionString("Server=h;Initial Catalog=Other;Connection Timeout=3;Application Name=x", "").Replace(";Connection Timeout=3;Application Name=x", ""));
        }
    }
}
