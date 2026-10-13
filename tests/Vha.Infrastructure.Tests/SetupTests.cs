using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation.Language;
using System.Text;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Setup;
using Vha.Core.Tests;
using Vha.Core.Vmware;
using Vha.Infrastructure.PowerShell;
using Vha.Infrastructure.Setup;
using Xunit;

namespace Vha.Infrastructure.Tests
{
    public class SetupScriptSyntaxTests
    {
        public static IEnumerable<object[]> AllGeneratedScripts()
        {
            var nasty = Sample.Valid();
            nasty.InstallDirectory = @"C:\it's a ""dir""\vHA`$(calc)";
            nasty.ServiceAccount = @"CORP\svc'user";
            nasty.TaskAccount = @"CORP\task`user";
            nasty.VCenter.AllowUntrustedCertificate = true;
            nasty.Cluster.Name = "CL'1"; nasty.Cluster.WitnessPath = @"\\fs01\wit'ness";
            nasty.Cluster.FailoverThreshold = 3; nasty.Cluster.FailoverPeriodHours = 6;
            nasty.Nodes[0].VmName = "VM'1 & co"; nasty.Nodes[0].DnsName = "vm1.corp.local";
            nasty.AvailabilityGroups[0].Name = "ag'01"; nasty.AvailabilityGroups[0].MountRoot = @"M:\it's\[x]";
            nasty.AvailabilityGroups[0].InitialOwner = "VM'1 & co"; nasty.AvailabilityGroups[0].ListenerName = "L'1";
            nasty.AvailabilityGroups[0].ReservationEnabled = true; nasty.AvailabilityGroups[0].ReservationCpuMHz = 100;
            nasty.AvailabilityGroups[0].SqlOptions = "Encrypt=True;a='b'";
            foreach (var cfg in new[] { Sample.Valid(), nasty })
            {
                foreach (var n in cfg.Nodes)
                {
                    foreach (var s in SetupPlanBuilder.BuildNodePlan(cfg, n).Concat(SetupPlanBuilder.BuildClusterPlan(cfg, n)).Where(x => x.Script != null))
                        yield return new object[] { s.Id, s.Script };
                }
                foreach (var s in SetupPlanBuilder.BuildVerifyPlan(cfg)) yield return new object[] { s.Id, s.Script };
                foreach (var a in cfg.AvailabilityGroups) yield return new object[] { "uninstall:" + a.Name, MoreScripts.Uninstall(cfg, a) };
            }
        }

        [Theory]
        [MemberData(nameof(AllGeneratedScripts))]
        public void Every_setup_script_is_valid_powershell(string id, string script)
        {
            Parser.ParseInput(script, out _, out ParseError[] errors);
            Assert.True(errors.Length == 0, id + ": " + string.Join("; ", errors.Select(e => e.Message + " @" + e.Extent.StartLineNumber)) + "\r\n" + script);
        }
    }

    public class SetupScriptBehaviourTests
    {
        private static string Temp()
        {
            var d = Path.Combine(Path.GetTempPath(), "vha-setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d); return d;
        }

        private static AgSettings AgAt(string root, string name = "AG01") => new AgSettings { Name = name, MountRoot = root };

        [Fact]
        public void Disksign_script_writes_the_group_name_on_each_mounted_folder_and_is_idempotent()
        {
            var root = Temp();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Data")); Directory.CreateDirectory(Path.Combine(root, "Log"));
                using var h = new PowerShellHost();
                var script = Scripts.DiskSign(AgAt(root));
                var first = h.Invoke(script).Select(o => o.ToString()).ToList();
                Assert.Contains("written on 2 mount point(s)", first.Last());
                var f = Path.Combine(root, "Data", "jelena", "disksign.ini");
                Assert.Equal("[AG]\r\nName=AG01\r\n", File.ReadAllText(f));
                var second = h.Invoke(script).Select(o => o.ToString()).ToList();
                Assert.Contains("written on 0 mount point(s)", second.Last());
                Assert.Equal("[AG]\r\nName=AG01\r\n", File.ReadAllText(f));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Disksign_script_reports_missing_mount_points_and_keeps_other_content()
        {
            var root = Temp();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Data", "jelena"));
                File.WriteAllText(Path.Combine(root, "Data", "jelena", "disksign.ini"), "[SCSI]\r\nVMware_ID=1:1\r\n");
                using var h = new PowerShellHost();
                var lines = h.Invoke(Scripts.DiskSign(AgAt(root))).Select(o => o.ToString()).ToList();
                Assert.Contains(lines, l => l.Contains("is not present on this node"));
                var text = File.ReadAllText(Path.Combine(root, "Data", "jelena", "disksign.ini"));
                Assert.Contains("Name=AG01", text); Assert.Contains("VMware_ID=1:1", text);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Disksign_script_refuses_to_claim_a_disk_that_belongs_to_another_group()
        {
            var root = Temp();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Data", "jelena"));
                File.WriteAllText(Path.Combine(root, "Data", "jelena", "disksign.ini"), "[AG]\r\nName=OTHER-AG\r\n");
                using var h = new PowerShellHost();
                var ex = Assert.Throws<PowerShellException>(() => h.Invoke(Scripts.DiskSign(AgAt(root))));
                Assert.Contains("belongs to another AG", ex.Message);
                Assert.Contains("Name=OTHER-AG", File.ReadAllText(Path.Combine(root, "Data", "jelena", "disksign.ini")));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    public class SetupRunnerTests
    {
        private sealed class Secrets : ISecretProvider
        {
            public int Asked;
            public string ServiceAccountPassword() { Asked++; return "svc-secret"; }
            public string TaskAccountPassword() { Asked++; return "task-secret"; }
            public string VCenterPassword() { Asked++; return "vc-secret"; }
        }

        private sealed class FakeProtector : IPasswordProtector { public string Protect(string p) => "dpapi:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(p.Reverse().ToArray()))); }

        private static SetupRunner Runner(DeploymentConfig c, ListLog log, Secrets secrets = null, RunnerOptions o = null, Func<string, string, string> rights = null, Func<PowerShellHost, IVCenter> vc = null) =>
            new SetupRunner(c, c.Nodes[0], o ?? new RunnerOptions { PackageDir = "pkg", PrereqDir = "pre" }, log, secrets ?? new Secrets(), new DatabaseSetup("sql", log, new FakeProtector()), null, vc, rights);

        private static SetupStep Step(string id, string script, params string[] vars) { var s = new SetupStep { Id = id, Title = id, Script = script }; s.Variables.AddRange(vars); return s; }

        [Fact]
        public void Steps_run_in_order_and_stop_at_the_first_failure()
        {
            var log = new ListLog();
            var report = Runner(Sample.Valid(), log).Run(new[] { Step("a", "'one'"), Step("b", "throw 'boom'"), Step("c", "'never'") });
            Assert.False(report.Success);
            Assert.Equal(new[] { StepOutcome.Ok, StepOutcome.Failed }, report.Results.Select(r => r.Outcome).ToArray());
            Assert.Contains("boom", report.Results[1].Error);
            Assert.False(log.Has("never"));
        }

        [Fact]
        public void A_reboot_request_stops_the_run_cleanly()
        {
            var report = Runner(Sample.Valid(), new ListLog()).Run(new[] { Step("a", "'" + SetupPlanBuilder.RebootMarker + " restart'"), Step("b", "'x'") });
            Assert.True(report.RebootRequired);
            Assert.False(report.Success);
            Assert.Single(report.Results);
        }

        [Fact]
        public void Dry_run_executes_nothing_and_never_asks_for_passwords()
        {
            var secrets = new Secrets(); var log = new ListLog();
            var report = Runner(Sample.Valid(), log, secrets, new RunnerOptions { DryRun = true, ShowScripts = true }).Run(new[] { Step("a", "throw 'must not run'", "svcPassword") });
            Assert.True(report.Success);
            Assert.Equal(StepOutcome.Skipped, report.Results[0].Outcome);
            Assert.Equal(0, secrets.Asked);
            Assert.True(log.Has("must not run"));   // the script text is shown
        }

        [Fact]
        public void Secrets_reach_the_script_only_as_variables_and_never_appear_in_the_log()
        {
            var secrets = new Secrets(); var log = new ListLog();
            var report = Runner(Sample.Valid(), log, secrets, new RunnerOptions { PackageDir = @"C:\pkg", PrereqDir = @"C:\pre" })
                .Run(new[] { Step("a", "if ($svcPassword -eq 'svc-secret' -and $packageDir -eq 'C:\\pkg' -and $prereqDir -eq 'C:\\pre') { 'variables ok' } else { throw 'wrong variables' }", "svcPassword", "packageDir", "prereqDir") });
            Assert.True(report.Success, report.Results[0].Error);
            Assert.Equal(1, secrets.Asked);
            Assert.DoesNotContain(log.Lines, l => l.Contains("svc-secret"));
        }

        [Fact]
        public void The_rights_step_grants_service_and_batch_rights_to_the_two_accounts()
        {
            var calls = new List<string>();
            var c = Sample.Valid();
            var report = Runner(c, new ListLog(), rights: (acct, right) => { calls.Add(acct + "|" + right); return "ok"; }).Run(new[] { new SetupStep { Id = "rights", Title = "rights" } });
            Assert.True(report.Success);
            Assert.Equal(new[] { c.ServiceAccount + "|SeServiceLogonRight", c.TaskAccount + "|SeBatchLogonRight" }, calls.ToArray());
        }

        [Fact]
        public void An_unknown_built_in_step_fails_instead_of_being_ignored()
        {
            var report = Runner(Sample.Valid(), new ListLog()).Run(new[] { new SetupStep { Id = "mystery", Title = "x" } });
            Assert.False(report.Success);
            Assert.Contains("Unknown built-in step", report.Results[0].Error);
        }

        /// <summary>MountRoot with Data/Log (capitalised, as on Windows) and the UserData/UserLog folders writable by the SQL Server container.</summary>
        private static string WindowsStyleMount()
        {
            var root = Path.Combine(Lab.Shared, "ag01");
            foreach (var sub in new[] { "", "Data", "Log", "Data/UserData", "Log/UserLog" })
            {
                var p = Path.Combine(root, sub);
                Directory.CreateDirectory(p);
                File.SetUnixFileMode(p, (UnixFileMode)0x1FF);
            }
            return root;
        }

        [SqlFact]
        public void The_database_step_prepares_node_A_then_node_B_against_real_sql_servers_and_is_idempotent()
        {
            Lab.Reset(dropJelena: true);
            var d = WindowsStyleMount();
            var c = Sample.Valid();
            c.Nodes = new List<NodeSettings> { new NodeSettings { VmName = "VM1", DnsName = "vm1.local" }, new NodeSettings { VmName = "VM2", DnsName = "vm2.local" } };
            c.AvailabilityGroups = new List<AgSettings> { new AgSettings { Name = "AG01", MountRoot = d, DiskNameFilter = "*AG01*", InitialOwner = "VM1", ListenerName = "L1", ListenerIp = "10.0.0.9" } };
            c.VCenter.User = @"EX\svc";
            var ag = c.AvailabilityGroups[0];
            var journal = new Journal();
            var vc = new FakeVCenter(journal).AddVm("VM1").AddVm("VM2");
            vc.Attach("VM1", "2000", "[DS1] vm1/os.vmdk", "OS1").Attach("VM1", "2016", "[DS1] vm1/AG01-data.vmdk", "6000C29B-0001").Attach("VM1", "2017", "[DS1] vm1/AG01-log.vmdk", "6000C29B-0002");
            vc.Attach("VM2", "2000", "[DS1] vm2/os.vmdk", "OS2");
            var log = new ListLog();
            Func<NodeSettings, AgSettings, string, string> conn = (n, a, db) => n.VmName == "VM1" ? Lab.ConnA(db) : Lab.ConnB(db);
            var setup = new DatabaseSetup(Path.Combine(Lab.RepoRoot, "sql"), log, new FakeProtector(), conn, '/');

            // ---- node A holds the disks
            var rep = setup.Run(c, ag, c.Nodes[0], "pw'1", vc);
            Assert.Contains(rep, l => l.Contains("database Jelena created"));
            Assert.Contains(rep, l => l.Contains("2 disk(s)"));
            Assert.Contains(rep, l => l.Contains("Dummy01 created"));
            var a = Lab.ConnA("Jelena");
            Assert.Equal("VM1", Convert.ToString(Lab.Scalar(a, "SELECT CurrentOwner FROM dbo.JAGroups")));
            Assert.Equal("1", Convert.ToString(Lab.Scalar(a, "SELECT CAST(is_local AS INT) FROM dbo.JNodes WHERE VMName = 'VM1'")));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(a, "SELECT COUNT(*) FROM dbo.JDisks WHERE Attached = 1")));
            Assert.Equal("6000C29B0001", Convert.ToString(Lab.Scalar(a, "SELECT UniqueId FROM dbo.JDisks WHERE DeviceKey = '2016'")));
            Assert.Equal("Dummy01", Convert.ToString(Lab.Scalar(a, "SELECT DBName FROM dbo.JDatabases")));
            Assert.True(File.Exists(d + "/Data/UserData/Dummy01.mdf"));
            var setting = Convert.ToString(Lab.Scalar(a, "SELECT SettingValue FROM dbo.JSettings WHERE SettingName = 'ConnectVCenter'"));
            Assert.StartsWith("Connect-VIServer vc01.corp.local -User 'EX\\svc' -Password dpapi:", setting);
            Assert.DoesNotContain("pw'1", setting);
            var spec = VCenterConnectionSpec.Parse(setting, b64 => new string(Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Reverse().ToArray()));
            Assert.Equal("pw'1", spec.Password); Assert.Equal(@"EX\svc", spec.User);

            // ---- node B does not hold the disks
            var repB = setup.Run(c, ag, c.Nodes[1], "pw'1", vc);
            Assert.Contains(repB, l => l.Contains("does not hold the disks"));
            var b = Lab.ConnB("Jelena");
            Assert.Equal("1", Convert.ToString(Lab.Scalar(b, "SELECT CAST(is_local AS INT) FROM dbo.JNodes WHERE VMName = 'VM2'")));
            Assert.Equal("0", Convert.ToString(Lab.Scalar(b, "SELECT COUNT(*) FROM dbo.JDisks WHERE Attached = 1")));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(b, "SELECT COUNT(*) FROM dbo.JDisks")));
            Assert.Equal("0", Convert.ToString(Lab.Scalar(b, "SELECT COUNT(*) FROM dbo.JDatabases")));
            Assert.Null(Lab.Scalar(Lab.ConnB(), "SELECT 1 FROM sys.databases WHERE name = 'Dummy01'"));

            // ---- idempotent
            var scnBefore = Convert.ToString(Lab.Scalar(a, "SELECT MAX(LastSCN) FROM dbo.SCN"));
            var again = setup.Run(c, ag, c.Nodes[0], "pw'1", vc);
            Assert.Contains(again, l => l.Contains("Dummy01 already exists"));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(a, "SELECT COUNT(*) FROM dbo.JDisks")));
            Assert.Equal("2", Convert.ToString(Lab.Scalar(a, "SELECT COUNT(*) FROM dbo.JNodes")));
            Assert.Equal("1", Convert.ToString(Lab.Scalar(a, "SELECT COUNT(*) FROM dbo.JDatabases")));
            Assert.Equal(scnBefore, Convert.ToString(Lab.Scalar(a, "SELECT MAX(LastSCN) FROM dbo.SCN")));
            Assert.Equal("1", Convert.ToString(Lab.Scalar(a, "SELECT COUNT(*) FROM dbo.JSettings")));
        }

        [SqlFact]
        public void The_database_step_reports_clear_errors_and_registers_only_what_is_safe()
        {
            Lab.Reset(dropJelena: true);
            var d = WindowsStyleMount();
            var c = Sample.Valid();
            c.VCenter.User = "";
            c.Nodes = new List<NodeSettings> { new NodeSettings { VmName = "VM1", DnsName = "vm1.local" }, new NodeSettings { VmName = "VM2", DnsName = "vm2.local" } };
            c.AvailabilityGroups = new List<AgSettings> { new AgSettings { Name = "AG01", MountRoot = d, DiskNameFilter = "*AG01*", InitialOwner = "VM1", ListenerName = "L1", ListenerIp = "10.0.0.9" } };
            var ag = c.AvailabilityGroups[0];
            var vc = new FakeVCenter(new Journal()).AddVm("VM1").AddVm("VM2");
            vc.Attach("VM1", "2016", "[DS1] other/data.vmdk", "6000C29B-0001");
            Func<NodeSettings, AgSettings, string, string> conn = (n, a, db) => n.VmName == "VM1" ? Lab.ConnA(db) : Lab.ConnB(db);
            var setup = new DatabaseSetup(Path.Combine(Lab.RepoRoot, "sql"), new ListLog(), new FakeProtector(), conn, '/');

            Assert.Contains("No disk matches", Assert.Throws<InvalidOperationException>(() => setup.Run(c, ag, c.Nodes[0], null, vc)).Message);   // filter matches nothing

            vc.Disks["VM1"].Clear(); vc.Attach("VM1", "2016", "[DS1] x/AG01-data.vmdk", "");
            Assert.Contains("EnableUUID", Assert.Throws<InvalidOperationException>(() => setup.Run(c, ag, c.Nodes[0], null, vc)).Message);

            vc.Disks["VM1"].Clear(); vc.Attach("VM1", "2016", "[DS1] x/AG01-data.vmdk", "6000C29B-0001");
            c.VCenter.User = @"EX\svc";
            Assert.Contains("no password", Assert.Throws<InvalidOperationException>(() => setup.Run(c, ag, c.Nodes[0], null, vc)).Message);

            // a database outside the mount root is never registered, a path with regex/LIKE characters is handled literally
            c.VCenter.User = "";
            Lab.Exec(Lab.ConnA(), "IF DB_ID('Outside') IS NULL CREATE DATABASE Outside");
            ag.CreateDummyDatabase = true; ag.MountRoot = d + "/Data";    // too deep on purpose: 'Data' below it does not exist
            var rep = setup.Run(c, ag, c.Nodes[0], null, vc);
            Assert.Contains(rep, l => l.Contains("does not hold the disks"));
            Assert.Null(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT 1 FROM dbo.JDatabases"));
        }

        [Fact]
        public void Connect_vcenter_setting_supports_pass_through_and_quotes_in_names()
        {
            var db = new DatabaseSetup("sql", new ListLog(), new FakeProtector());
            var c = Sample.Valid(); c.VCenter.User = "";
            Assert.Equal("Connect-VIServer vc01.corp.local -Protocol https", db.ConnectVCenterSetting(c, null));
            c.VCenter.User = "EX\\o'neil";
            var s = db.ConnectVCenterSetting(c, "p");
            var spec = VCenterConnectionSpec.Parse(s, b64 => new string(Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Reverse().ToArray()));
            Assert.Equal("EX\\o'neil", spec.User); Assert.Equal("p", spec.Password);
            Assert.Throws<InvalidOperationException>(() => db.ConnectVCenterSetting(c, ""));
        }
    }
}
