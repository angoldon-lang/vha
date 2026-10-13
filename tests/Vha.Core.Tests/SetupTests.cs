using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vha.Core.Config;
using Vha.Core.Model;
using Vha.Core.Setup;
using Vha.Core.Vmware;
using Xunit;

namespace Vha.Core.Tests
{
    public static class Sample
    {
        public static DeploymentConfig Valid()
        {
            return new DeploymentConfig
            {
                InstallDirectory = @"C:\vHA\Bin",
                VCenter = new VCenterSettings { Server = "vc01.corp.local", User = @"CORP\svcvha" },
                ServiceAccount = @"CORP\svcVhaSvc$",
                TaskAccount = @"CORP\svcVhaTask$",
                Cluster = new ClusterSettings { Name = "VHACL01", IpAddress = "10.0.0.50", Witness = WitnessType.FileShare, WitnessPath = @"\\fs01\witness\vhacl01" },
                Nodes = new List<NodeSettings>
                {
                    new NodeSettings { VmName = "SQLVM01", DnsName = "sqlvm01.corp.local" },
                    new NodeSettings { VmName = "SQLVM02", DnsName = "sqlvm02.corp.local" }
                },
                AvailabilityGroups = new List<AgSettings>
                {
                    new AgSettings { Name = "isql2025a-AG01", InstanceName = "ISQL2025A", MountRoot = @"M:\isql2025a-AG01", DiskNameFilter = "*isql2025a-AG01*", InitialOwner = "SQLVM01", ListenerName = "ISQL25A01", ListenerIp = "10.0.0.51" },
                    new AgSettings { Name = "isql2025a-AG02", InstanceName = "ISQL2025A", MountRoot = @"M:\isql2025a-AG02", DiskNameFilter = "*isql2025a-AG02*", InitialOwner = "SQLVM02", ListenerName = "ISQL25A02", ListenerIp = "10.0.0.52" }
                }
            };
        }

        public static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "vha.sln"))) d = d.Parent;
            return d.FullName;
        }
    }

    public class DeploymentConfigTests
    {
        [Fact]
        public void The_sample_configuration_is_valid_and_only_warns_about_nothing()
        {
            var r = DeploymentValidator.Validate(Sample.Valid());
            Assert.True(r.IsValid, string.Join("\n", r.Issues));
            Assert.Contains(r.Warnings, w => w.Path.EndsWith("sqlOptions"));   // default TrustServerCertificate=True is flagged
        }

        [Fact]
        public void Json_round_trips_and_uses_readable_enum_names()
        {
            var c = Sample.Valid();
            var json = DeploymentConfigJson.Serialize(c);
            Assert.Contains("\"witness\": \"FileShare\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"ownerUnreachablePolicy\": \"Force\"", json, StringComparison.OrdinalIgnoreCase);
            var back = DeploymentConfigJson.Parse(json);
            Assert.Equal(DeploymentConfigJson.Serialize(c), DeploymentConfigJson.Serialize(back));
            Assert.True(DeploymentValidator.Validate(back).IsValid);
        }

        [Fact]
        public void Written_files_use_camel_case_names_like_the_documentation_and_the_example()
        {
            var json = DeploymentConfigJson.Serialize(Sample.Valid());
            Assert.Contains("\"installDirectory\"", json); Assert.Contains("\"availabilityGroups\"", json); Assert.Contains("\"diskNameFilter\"", json);
            Assert.DoesNotContain("\"InstallDirectory\"", json);
        }

        [Fact]
        public void The_shipped_example_file_is_valid()
        {
            var path = Path.Combine(Sample.RepoRoot(), "examples", "vha-deployment.example.json");
            var c = DeploymentConfigJson.Load(path);
            var r = DeploymentValidator.Validate(c);
            Assert.True(r.IsValid, string.Join("\n", r.Issues));
            Assert.Equal("isql2025a-AG01", c.AvailabilityGroups[0].Name);
        }

        [Fact]
        public void Json_accepts_comments_trailing_commas_and_any_case_but_rejects_unknown_keys_and_bad_values()
        {
            var ok = DeploymentConfigJson.Parse("{ // comment\n \"SchemaVersion\": 1, \"installdirectory\": \"C:\\\\x\\\\y\", }");
            Assert.Equal(@"C:\x\y", ok.InstallDirectory);
            Assert.Throws<FormatException>(() => DeploymentConfigJson.Parse("{ \"installDirectry\": \"C:\\\\x\" }"));          // typo
            Assert.Throws<FormatException>(() => DeploymentConfigJson.Parse("{ \"cluster\": { \"witness\": \"Cloud\" } }"));   // unknown enum
            Assert.Throws<FormatException>(() => DeploymentConfigJson.Parse("{ \"nodes\": 3 }"));
            Assert.Throws<FormatException>(() => DeploymentConfigJson.Parse(""));
            Assert.Throws<FormatException>(() => DeploymentConfigJson.Parse("{ not json"));
        }

        public static IEnumerable<object[]> Breakages()
        {
            Action<DeploymentConfig> none = c => { };
            yield return Case("schemaVersion", c => c.SchemaVersion = 2);
            yield return Case("installDirectory", c => c.InstallDirectory = "relative\\path");
            yield return Case("installDirectory", c => c.InstallDirectory = @"C:\a\..\b");
            yield return Case("installDirectory", c => c.InstallDirectory = "");
            yield return Case("vCenter.server", c => c.VCenter.Server = "bad host name");
            yield return Case("vCenter.server", c => c.VCenter.Server = "");
            yield return Case("vCenter.user", c => c.VCenter.User = "not valid user");
            yield return Case("serviceAccount", c => c.ServiceAccount = "svc");
            yield return Case("taskAccount", c => c.TaskAccount = "");
            yield return Case("nodes", c => c.Nodes.RemoveAt(1));
            yield return Case("nodes[0].vmName", c => c.Nodes[0].VmName = "vm';drop");
            yield return Case("nodes[1].dnsName", c => c.Nodes[1].DnsName = "bad name.local");
            yield return Case("nodes", c => c.Nodes[1].VmName = "sqlvm01");                    // duplicate, case insensitive
            yield return Case("cluster.name", c => c.Cluster.Name = "THISNAMEISWAYTOOLONG");
            yield return Case("cluster.ipAddress", c => c.Cluster.IpAddress = "10.0.0");
            yield return Case("cluster.ipAddress", c => c.Cluster.IpAddress = "fe80::1");
            yield return Case("cluster.witnessPath", c => c.Cluster.WitnessPath = @"C:\witness");
            yield return Case("cluster.witness", c => c.Cluster.Witness = WitnessType.None);   // 2 nodes without witness
            yield return Case("cluster.failoverThreshold", c => c.Cluster.FailoverThreshold = 2);   // period missing
            yield return Case("availabilityGroups", c => c.AvailabilityGroups.Clear());
            yield return Case("availabilityGroups[0].name", c => c.AvailabilityGroups[0].Name = "has space");
            yield return Case("availabilityGroups[0].name", c => c.AvailabilityGroups[0].Name = "-leading");
            yield return Case("availabilityGroups[0].instanceName", c => c.AvailabilityGroups[0].InstanceName = "9bad");
            yield return Case("availabilityGroups[0].sqlPort", c => c.AvailabilityGroups[0].SqlPort = 70000);
            yield return Case("availabilityGroups[0].mountRoot", c => c.AvailabilityGroups[0].MountRoot = @"M:\");
            yield return Case("availabilityGroups[0].mountRoot", c => c.AvailabilityGroups[0].MountRoot = "M:/slash");
            yield return Case("availabilityGroups", c => c.AvailabilityGroups[1].MountRoot = c.AvailabilityGroups[0].MountRoot);
            yield return Case("availabilityGroups[0].diskNameFilter", c => c.AvailabilityGroups[0].DiskNameFilter = "");
            yield return Case("availabilityGroups[0].diskNameFilter", c => c.AvailabilityGroups[0].DiskNameFilter = "*x'; drop*");
            yield return Case("availabilityGroups[0].initialOwner", c => c.AvailabilityGroups[0].InitialOwner = "NOSUCHVM");
            yield return Case("availabilityGroups[0].listenerName", c => c.AvailabilityGroups[0].ListenerName = "LISTENERNAMETOOLONG");
            yield return Case("availabilityGroups[0].listenerIp", c => c.AvailabilityGroups[0].ListenerIp = "999.1.1.1");
            yield return Case("availabilityGroups", c => c.AvailabilityGroups[1].ListenerIp = c.AvailabilityGroups[0].ListenerIp);
            yield return Case("availabilityGroups", c => c.AvailabilityGroups[1].ListenerIp = c.Cluster.IpAddress);
            yield return Case("availabilityGroups[0].reservationCpuMHz", c => { c.AvailabilityGroups[0].ReservationEnabled = true; c.AvailabilityGroups[0].ReservationCpuMHz = -1; });
            yield return Case("availabilityGroups[0].ownerUnreachablePolicy", c => c.AvailabilityGroups[0].OwnerUnreachablePolicy = (OwnerPolicy)7);
            yield return Case("availabilityGroups[0].csuWaitSeconds", c => c.AvailabilityGroups[0].CsuWaitSeconds = 0);
            yield return Case("availabilityGroups[0].failoverTimeoutSeconds", c => c.AvailabilityGroups[0].FailoverTimeoutSeconds = 10);
            yield return Case("availabilityGroups[0].csuLogLevel", c => c.AvailabilityGroups[0].CsuLogLevel = 4);
            yield return Case("availabilityGroups[0].csuIntervalSeconds", c => c.AvailabilityGroups[0].CsuIntervalSeconds = 30);
            yield return Case("availabilityGroups[0].csuIntervalSeconds", c => c.AvailabilityGroups[0].CsuIntervalSeconds = 90);
            yield return Case("availabilityGroups[0].sqlOptions", c => c.AvailabilityGroups[0].SqlOptions = "Encrypt=True\r\n[Evil]");
            yield return Case("availabilityGroups[0].dummyDatabaseName", c => c.AvailabilityGroups[0].DummyDatabaseName = "bad name");
            yield return Case("availabilityGroups[0].taskName", c => c.AvailabilityGroups[0].TaskName = "bad/name");
            yield return Case("availabilityGroups", c => { c.AvailabilityGroups[0].TaskName = "same"; c.AvailabilityGroups[1].TaskName = "SAME"; });
        }

        private static object[] Case(string path, Action<DeploymentConfig> mutate) => new object[] { path, mutate };

        [Theory]
        [MemberData(nameof(Breakages))]
        public void Every_rule_rejects_what_it_should(string path, Action<DeploymentConfig> mutate)
        {
            var c = Sample.Valid();
            mutate(c);
            var r = DeploymentValidator.Validate(c);
            Assert.False(r.IsValid);
            Assert.Contains(r.Errors, e => e.Path == path);
        }

        [Fact]
        public void Gmsa_is_recognised_and_other_accounts_get_a_warning()
        {
            Assert.True(DeploymentConfig.IsGmsa(@"CORP\svc$")); Assert.False(DeploymentConfig.IsGmsa(@"CORP\svc")); Assert.False(DeploymentConfig.IsGmsa(""));
            var c = Sample.Valid(); c.ServiceAccount = @"CORP\svcnormal";
            var r = DeploymentValidator.Validate(c);
            Assert.True(r.IsValid);
            Assert.Contains(r.Warnings, w => w.Path == "serviceAccount");
        }

        [Fact]
        public void Four_nodes_or_odd_nodes_can_run_without_witness_only_when_odd()
        {
            var c = Sample.Valid(); c.Nodes.Add(new NodeSettings { VmName = "SQLVM03", DnsName = "sqlvm03.corp.local" }); c.Cluster.Witness = WitnessType.None;
            Assert.True(DeploymentValidator.Validate(c).IsValid);
            c.Nodes.Add(new NodeSettings { VmName = "SQLVM04", DnsName = "sqlvm04.corp.local" });
            Assert.False(DeploymentValidator.Validate(c).IsValid);
        }

        [Fact]
        public void Null_pieces_are_reported_not_thrown()
        {
            Assert.False(DeploymentValidator.Validate(null).IsValid);
            var c = Sample.Valid(); c.VCenter = null; c.Cluster = null; c.Nodes[0] = null; c.AvailabilityGroups[0] = null;
            Assert.False(DeploymentValidator.Validate(c).IsValid);
        }

        [Fact]
        public void Wildcard_star_alone_is_a_warning()
        {
            var c = Sample.Valid(); c.AvailabilityGroups[0].DiskNameFilter = "*";
            Assert.Contains(DeploymentValidator.Validate(c).Warnings, w => w.Path.EndsWith("diskNameFilter"));
        }
    }

    public class RendererTests
    {
        [Fact]
        public void Generated_ini_is_read_back_by_the_service_exactly()
        {
            var c = Sample.Valid(); c.AvailabilityGroups[0].ReservationEnabled = true; c.AvailabilityGroups[0].ReservationCpuMHz = 100; c.AvailabilityGroups[0].ReservationMemMB = 200;
            c.AvailabilityGroups[0].OwnerUnreachablePolicy = OwnerPolicy.Abort; c.AvailabilityGroups[0].CsuWaitSeconds = 45; c.AvailabilityGroups[0].FailoverTimeoutSeconds = 600;
            var ag = c.AvailabilityGroups[0]; var node = c.Nodes[1];
            var s = ServiceSettings.FromIni(IniDocument.Parse(IniRenderer.Render(c, ag, node)), ag.ServiceName);
            Assert.Equal("SQLVM02", s.VirtualMachineName);
            Assert.Equal("isql2025a-AG01", s.AgName);
            Assert.Equal(@"sqlvm02.corp.local\ISQL2025A", s.Instance);
            Assert.True(s.ReservationEnabled);
            Assert.Equal(OwnerUnreachablePolicy.Abort, s.UnreachablePolicy);
            Assert.Equal(TimeSpan.FromSeconds(45), s.CsuWaitTimeout);
            Assert.Equal(TimeSpan.FromSeconds(600), s.FailoverTimeout);
            Assert.Equal("CSU - ISQL2025A - isql2025a-AG01", s.TaskName);
            Assert.Equal("Encrypt=True;TrustServerCertificate=True", s.SqlOptions);
        }

        [Theory]
        [InlineData("ISQL", 0, @"h.corp\ISQL")]
        [InlineData("", 0, "h.corp")]
        [InlineData("ISQL", 1433, "h.corp,1433")]
        public void Sql_address_follows_instance_and_port(string instance, int port, string expected)
        {
            var ag = new AgSettings { InstanceName = instance, SqlPort = port };
            Assert.Equal(expected, NodeNaming.SqlAddress(new NodeSettings { DnsName = "h.corp" }, ag));
        }

        [Fact]
        public void Task_xml_is_well_formed_runs_the_csu_with_quoted_arguments_and_staggers_the_start_second()
        {
            var c = Sample.Valid(); c.AvailabilityGroups[1].CsuIntervalSeconds = 120; c.AvailabilityGroups[1].CsuLogLevel = 3;
            c.Nodes[0].VmName = "SQL VM & 01";
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var x0 = XDocument.Parse(TaskXmlRenderer.Render(c, 0, c.Nodes[0]));
            var x1 = XDocument.Parse(TaskXmlRenderer.Render(c, 1, c.Nodes[0]));
            Assert.Equal(@"C:\vHA\Bin\CsuCmd.exe", x0.Descendants(ns + "Command").Single().Value);
            Assert.Equal("\"SQL VM & 01\" isql2025a-AG01 sqlvm01.corp.local\\ISQL2025A 2", x0.Descendants(ns + "Arguments").Single().Value);
            Assert.Equal("\"SQL VM & 01\" isql2025a-AG02 sqlvm01.corp.local\\ISQL2025A 3", x1.Descendants(ns + "Arguments").Single().Value);
            Assert.Equal("PT1M", x0.Descendants(ns + "Interval").Single().Value);
            Assert.Equal("PT2M", x1.Descendants(ns + "Interval").Single().Value);
            Assert.EndsWith(":00", x0.Descendants(ns + "StartBoundary").Single().Value);
            Assert.EndsWith(":15", x1.Descendants(ns + "StartBoundary").Single().Value);
            Assert.Equal(@"CORP\svcVhaTask$", x0.Descendants(ns + "UserId").Single().Value);
            Assert.Equal("Password", x0.Descendants(ns + "LogonType").Single().Value);
            Assert.Equal("IgnoreNew", x0.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
            Assert.Equal("false", x0.Descendants(ns + "StopAtDurationEnd").Single().Value);
            Assert.Equal("1.2", x0.Root.Attribute("version").Value);
        }

        [Fact]
        public void Task_start_seconds_cycle_through_0_15_30_45()
        {
            var c = Sample.Valid();
            c.AvailabilityGroups.Clear();
            for (int i = 0; i < 6; i++) c.AvailabilityGroups.Add(new AgSettings { Name = "AG" + i, InstanceName = "I", InitialOwner = "SQLVM01" });
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var secs = Enumerable.Range(0, 6).Select(i => XDocument.Parse(TaskXmlRenderer.Render(c, i, c.Nodes[0])).Descendants(ns + "StartBoundary").Single().Value.Substring(17)).ToArray();
            Assert.Equal(new[] { "00", "15", "30", "45", "00", "15" }, secs);
        }

        [Fact]
        public void Disksign_text_names_the_group() => Assert.Equal("[AG]\r\nName=isql2025a-AG01\r\n", IniRenderer.DiskSign(Sample.Valid().AvailabilityGroups[0]));
    }

    public class SqlScriptTemplateTests
    {
        [Fact]
        public void Values_are_escaped_for_string_literals_and_numbers_validated()
        {
            var sql = SqlScriptTemplate.Apply("DECLARE @a NVARCHAR(10) = N'$(AgName)', @c INT = $(CpuMHz);", new Dictionary<string, string> { { "AgName", "it's" }, { "CpuMHz", "100" } });
            Assert.Equal("DECLARE @a NVARCHAR(10) = N'it''s', @c INT = 100;", sql);
            Assert.Throws<ArgumentException>(() => SqlScriptTemplate.Apply("$(CpuMHz)", new Dictionary<string, string> { { "CpuMHz", "1; DROP TABLE x" } }));
            Assert.Throws<ArgumentException>(() => SqlScriptTemplate.Apply("$(CpuMHz)", new Dictionary<string, string> { { "CpuMHz", "-1" } }));
            Assert.Throws<ArgumentException>(() => SqlScriptTemplate.Apply("'$(AgName)'", new Dictionary<string, string> { { "AgName", "a\0b" } }));
            Assert.Throws<InvalidOperationException>(() => SqlScriptTemplate.Apply("$(Missing)", new Dictionary<string, string>()));
        }

        [Fact]
        public void Injection_attempts_stay_inside_the_literal()
        {
            var sql = SqlScriptTemplate.Apply("SELECT N'$(AgName)'", new Dictionary<string, string> { { "AgName", "x'; DROP DATABASE Jelena; --" } });
            Assert.Equal("SELECT N'x''; DROP DATABASE Jelena; --'", sql);
        }

        [Fact]
        public void Batches_split_on_go_lines_only()
        {
            var b = SqlScriptTemplate.Batches("SELECT 1\nGO\nSELECT 'GO there'\n  go  \nSELECT 3\nGO\n").ToList();
            Assert.Equal(3, b.Count);
            Assert.Contains("GO there", b[1]);
        }

        [Fact]
        public void Every_variable_of_the_repository_scripts_is_provided_by_the_setup()
        {
            var root = Sample.RepoRoot();
            var vars = new Dictionary<string, string> { { "AgName", "a" }, { "VmName", "v" }, { "NodeList", "n" }, { "Owner", "o" }, { "CpuMHz", "0" }, { "MemMB", "0" }, { "CsOptions", "x" }, { "MountRoot", "m" } };
            foreach (var f in Directory.GetFiles(Path.Combine(root, "sql"), "*.sql"))
            {
                var applied = SqlScriptTemplate.Apply(File.ReadAllText(f), vars);
                Assert.DoesNotContain("$(", applied);
            }
        }
    }

    public class UserRightsInfTests
    {
        private const string Sid = "S-1-5-21-111-222-333-1105";
        private const string Inf = "[Unicode]\r\nUnicode=yes\r\n[Privilege Rights]\r\nSeServiceLogonRight = *S-1-5-80-0,*S-1-5-20\r\nSeBatchLogonRight = *S-1-5-32-544\r\n[Version]\r\nsignature=\"$CHICAGO$\"\r\n";

        [Fact]
        public void Account_is_appended_to_an_existing_right()
        {
            var r = UserRightsInf.AddAccount(Inf, "SeServiceLogonRight", Sid);
            Assert.Contains("SeServiceLogonRight = *S-1-5-80-0,*S-1-5-20,*" + Sid, r);
            Assert.Contains("SeBatchLogonRight = *S-1-5-32-544\r\n", r);   // the others are untouched
            Assert.True(UserRightsInf.Contains(r, "SeServiceLogonRight", Sid));
            Assert.False(UserRightsInf.Contains(r, "SeBatchLogonRight", Sid));
        }

        [Fact]
        public void Adding_twice_changes_nothing_and_a_missing_right_is_created_in_the_section()
        {
            var once = UserRightsInf.AddAccount(Inf, "SeBatchLogonRight", Sid);
            Assert.Equal(once, UserRightsInf.AddAccount(once, "SeBatchLogonRight", Sid));
            var created = UserRightsInf.AddAccount(Inf, "SeDenyBatchLogonRight", Sid);
            Assert.True(created.IndexOf("SeDenyBatchLogonRight = *" + Sid) > created.IndexOf("[Privilege Rights]") && created.IndexOf("SeDenyBatchLogonRight") < created.IndexOf("[Version]"));
            var noSection = UserRightsInf.AddAccount("[Unicode]\r\nUnicode=yes\r\n", "SeBatchLogonRight", Sid);
            Assert.Contains("[Privilege Rights]\r\nSeBatchLogonRight = *" + Sid, noSection);
        }

        [Fact]
        public void A_right_with_an_empty_list_and_prefix_names_are_handled()
        {
            var r = UserRightsInf.AddAccount("[Privilege Rights]\r\nSeBatchLogonRightX = *S-1-1-0\r\nSeBatchLogonRight = \r\n", "SeBatchLogonRight", Sid);
            Assert.Contains("SeBatchLogonRight = *" + Sid, r);
            Assert.Contains("SeBatchLogonRightX = *S-1-1-0", r);
        }

        [Theory]
        [InlineData("SeBatchLogonRight\r\nrm -rf", Sid)] [InlineData("SeBatchLogonRight", "S-1-5-21-1; calc")] [InlineData("", Sid)] [InlineData("SeBatchLogonRight", "")]
        public void Invalid_inputs_are_refused(string right, string sid) => Assert.Throws<ArgumentException>(() => UserRightsInf.AddAccount(Inf, right, sid));
    }

    public class DiskDiscoveryTests
    {
        private static VmDisk D(string vm, string key, string file, string uuid = "6000C29B-0001", double gb = 100) =>
            new VmDisk { Id = "VirtualMachine-" + vm + "/" + key, FileName = file, Uuid = uuid, CapacityGB = gb };

        private static IDictionary<string, IReadOnlyList<VmDisk>> World(params (string vm, VmDisk[] disks)[] vms) =>
            vms.ToDictionary(v => v.vm, v => (IReadOnlyList<VmDisk>)v.disks);

        [Theory]
        [InlineData("*AG01*", "[DS1] SQLVM01/isql2025a-AG01-data.vmdk", true)]
        [InlineData("*ag01*", "[DS1] x/AG01.vmdk", true)]
        [InlineData("[DS1] ?/a.vmdk", "[DS1] x/a.vmdk", true)]
        [InlineData("*AG01*", "[DS1] x/AG02.vmdk", false)]
        [InlineData("a.b", "aXb", false)]
        public void Wildcards_match_like_the_guide_describes(string filter, string file, bool expected) => Assert.Equal(expected, DiskDiscovery.Matches(filter, file));

        [Fact]
        public void Finds_the_disks_of_the_group_on_the_holder_and_records_whether_they_are_attached_to_this_node()
        {
            var w = World(("SQLVM01", new[] { D("1", "2000", "[d] os1.vmdk", "OS1"), D("1", "2016", "[d] AG01-data.vmdk", "6000C29B-0001", 100.4), D("1", "2017", "[d] AG01-log.vmdk", "6000C29B-0002", 50) }),
                          ("SQLVM02", new[] { D("2", "2000", "[d] os2.vmdk", "OS2") }));
            var found = DiskDiscovery.Discover("*AG01*", w, "SQLVM01");
            Assert.Equal(new[] { "2016", "2017" }, found.Select(f => f.Disk.DeviceKey).ToArray());
            Assert.All(found, f => Assert.Equal("SQLVM01", f.HolderVm));
            Assert.All(found, f => Assert.True(f.Disk.Attached));
            Assert.Equal("6000C29B0001", found[0].Disk.UniqueId);
            Assert.Equal(100, found[0].Disk.CapacityGB);
            Assert.All(DiskDiscovery.Discover("*AG01*", w, "SQLVM02"), f => Assert.False(f.Disk.Attached));
        }

        [Fact]
        public void Refuses_unsafe_or_ambiguous_situations()
        {
            Assert.Throws<ArgumentException>(() => DiskDiscovery.Discover(" ", World(), "x"));
            Assert.Contains("No disk matches", Assert.Throws<InvalidOperationException>(() => DiskDiscovery.Discover("*zzz*", World(("A", new[] { D("1", "2016", "[d] a.vmdk") })), "A")).Message);
            Assert.Contains("EnableUUID", Assert.Throws<InvalidOperationException>(() => DiskDiscovery.Discover("*a*", World(("A", new[] { D("1", "2016", "[d] a.vmdk", "") })), "A")).Message);
            Assert.Contains("only SCSI", Assert.Throws<InvalidOperationException>(() => DiskDiscovery.Discover("*a*", World(("A", new[] { D("1", "31000", "[d] a.vmdk") })), "A")).Message);
            Assert.Contains("more than one matching", Assert.Throws<InvalidOperationException>(() => DiskDiscovery.Discover("*a*", World(("A", new[] { D("1", "2016", "[d] a.vmdk") }), ("B", new[] { D("2", "2016", "[d] a2.vmdk") })), "A")).Message);
            Assert.Contains("more than one VM", Assert.Throws<InvalidOperationException>(() => DiskDiscovery.Discover("*a*", World(("A", new[] { D("1", "2016", "[d] a.vmdk") }), ("B", new[] { D("2", "2018", "[d] a.vmdk") })), "A")).Message);
        }
    }

    public class SetupPlanTests
    {
        [Fact]
        public void Node_plan_has_the_prerequisites_first_then_per_group_steps_in_a_safe_order()
        {
            var c = Sample.Valid();
            var ids = SetupPlanBuilder.BuildNodePlan(c, c.Nodes[0]).Select(s => s.Id).ToList();
            Assert.Equal(new[] { "os", "dotnet", "features", "powercli", "admin", "rights", "files",
                "ini:isql2025a-AG01", "service:isql2025a-AG01", "task:isql2025a-AG01", "disksign:isql2025a-AG01", "database:isql2025a-AG01",
                "ini:isql2025a-AG02", "service:isql2025a-AG02", "task:isql2025a-AG02", "disksign:isql2025a-AG02", "database:isql2025a-AG02" }, ids.ToArray());
        }

        [Fact]
        public void Secrets_are_only_requested_for_non_gmsa_accounts_and_never_embedded()
        {
            var c = Sample.Valid();
            var plan = SetupPlanBuilder.BuildNodePlan(c, c.Nodes[0]);
            Assert.DoesNotContain(plan, s => s.Variables.Contains("svcPassword") || s.Variables.Contains("taskPassword"));
            c.ServiceAccount = @"CORP\svcnormal"; c.TaskAccount = @"CORP\tasknormal";
            plan = SetupPlanBuilder.BuildNodePlan(c, c.Nodes[0]);
            Assert.All(plan.Where(s => s.Kind == StepKind.Service), s => { Assert.Contains("svcPassword", s.Variables); Assert.Contains("$svcPassword", s.Script); });
            Assert.All(plan.Where(s => s.Kind == StepKind.Task), s => { Assert.Contains("taskPassword", s.Variables); Assert.Contains("-Password $taskPassword", s.Script); });
            Assert.DoesNotContain(plan, s => s.Script != null && s.Script.Contains("Password = '"));
        }

        [Fact]
        public void Cluster_plan_creates_the_cluster_on_the_first_node_and_each_role_on_its_initial_owner()
        {
            var c = Sample.Valid();
            Assert.Equal(new[] { "cluster", "role:isql2025a-AG01" }, SetupPlanBuilder.BuildClusterPlan(c, c.Nodes[0]).Select(s => s.Id).ToArray());
            Assert.Equal(new[] { "role:isql2025a-AG02" }, SetupPlanBuilder.BuildClusterPlan(c, c.Nodes[1]).Select(s => s.Id).ToArray());
            var role = SetupPlanBuilder.BuildClusterPlan(c, c.Nodes[0]).Last().Script;
            Assert.Contains("-StaticAddress '10.0.0.51'", role);
            Assert.Contains("$g.Name = $ag", role);
            Assert.DoesNotContain("FailoverThreshold", role);
            c.Cluster.FailoverThreshold = 3; c.Cluster.FailoverPeriodHours = 6;
            Assert.Contains("$g.FailoverThreshold = 3", SetupPlanBuilder.BuildClusterPlan(c, c.Nodes[0]).Last().Script);
        }

        [Fact]
        public void Scripts_quote_every_configured_value()
        {
            var c = Sample.Valid();
            c.InstallDirectory = @"C:\o'neil\vHA";
            c.AvailabilityGroups[0].MountRoot = @"M:\it's";
            var all = string.Join("\n", SetupPlanBuilder.BuildNodePlan(c, c.Nodes[0]).Where(s => s.Script != null).Select(s => s.Script));
            Assert.Contains(@"'C:\o''neil\vHA'", all);
            Assert.Contains(@"'M:\it''s'", all);
            Assert.DoesNotContain(@"C:\o'neil", all.Replace(@"C:\o''neil", ""));
        }

        [Fact]
        public void Powercli_step_keeps_certificate_validation_on_unless_explicitly_disabled()
        {
            Assert.Contains("-InvalidCertificateAction Fail", Scripts.PowerCli(false));
            Assert.Contains("-InvalidCertificateAction Ignore", Scripts.PowerCli(true));
        }

        [Fact]
        public void Reboot_marker_is_emitted_by_the_steps_that_can_need_a_restart()
        {
            Assert.Contains(SetupPlanBuilder.RebootMarker, Scripts.DotNet);
            Assert.Contains(SetupPlanBuilder.RebootMarker, Scripts.Features);
        }

        [Fact]
        public void The_files_step_never_overwrites_a_running_service_and_verifies_hashes()
        {
            var s = Scripts.Files(Sample.Valid());
            Assert.Contains("is running (it is owned by the cluster role)", s);
            Assert.Contains("Get-FileHash", s);
        }

        [Fact]
        public void Disksign_step_refuses_to_claim_a_disk_of_another_group()
        {
            var s = Scripts.DiskSign(Sample.Valid().AvailabilityGroups[0]);
            Assert.Contains("belongs to another AG, refusing to claim it", s);
        }

        [Fact]
        public void Description_lists_the_steps()
        {
            var c = Sample.Valid();
            var d = SetupPlanBuilder.Describe(SetupPlanBuilder.BuildNodePlan(c, c.Nodes[0]), false);
            Assert.Contains("01. [Prerequisite]", d); Assert.Contains("(built-in action)", d);
            Assert.Contains("      | ", SetupPlanBuilder.Describe(SetupPlanBuilder.BuildNodePlan(c, c.Nodes[0]), true));
        }
    }

    public class WizardTests
    {
        private sealed class ScriptedIO : IConsoleIO
        {
            private readonly Queue<string> _in; public List<string> Out = new List<string>();
            public ScriptedIO(IEnumerable<string> answers) { _in = new Queue<string>(answers); }
            public void WriteLine(string t) => Out.Add(t);
            public string ReadLine() => _in.Count == 0 ? null : _in.Dequeue();
        }

        private static readonly string[] HappyPath =
        {
            "",                                   // install dir (default)
            "vc01.corp.local", @"CORP\svcvha", "n",
            @"CORP\svcVhaSvc$", @"CORP\svcVhaTask$",
            "SQLVM01", "sqlvm01.corp.local", "SQLVM02", "sqlvm02.corp.local", "n",   // two nodes, no third
            "VHACL01", "10.0.0.50", "", @"\\fs01\witness\vhacl01", "n",
            "isql2025a-AG01", "ISQL2025A", "0", @"M:\isql2025a-AG01", "", "", "ISQL25A01", "10.0.0.51", "n", "", "n",
            "n"                                   // no other AG
        };

        [Fact]
        public void A_complete_session_produces_a_valid_configuration()
        {
            var io = new ScriptedIO(HappyPath);
            var c = new Wizard(io).Run();
            Assert.True(DeploymentValidator.Validate(c).IsValid);
            Assert.Equal(@"C:\vHA\Bin", c.InstallDirectory);
            Assert.Equal("*isql2025a-AG01*", c.AvailabilityGroups[0].DiskNameFilter);
            Assert.Equal("SQLVM01", c.AvailabilityGroups[0].InitialOwner);
            Assert.Equal(WitnessType.FileShare, c.Cluster.Witness);
            Assert.Equal(2, c.Nodes.Count);
            Assert.Contains(io.Out, l => l.Contains("Configurazione valida"));
        }

        [Fact]
        public void Invalid_answers_are_rejected_on_the_spot_and_asked_again()
        {
            var answers = HappyPath.ToList();
            answers.Insert(1, "bad server name");        // vCenter server: invalid, then the valid one follows
            var io = new ScriptedIO(answers);
            var c = new Wizard(io).Run();
            Assert.Equal("vc01.corp.local", c.VCenter.Server);
            Assert.Contains(io.Out, l => l.StartsWith("  ERRORE: "));
        }

        [Fact]
        public void Required_fields_cannot_be_left_empty()
        {
            var answers = HappyPath.ToList();
            answers.Insert(1, "");                       // empty vCenter server
            var io = new ScriptedIO(answers);
            new Wizard(io).Run();
            Assert.Contains(io.Out, l => l.Contains("valore obbligatorio"));
        }

        [Fact]
        public void Running_out_of_input_cancels_cleanly() => Assert.Throws<OperationCanceledException>(() => new Wizard(new ScriptedIO(HappyPath.Take(8))).Run());

        [Fact]
        public void Endless_garbage_does_not_loop_forever() =>
            Assert.Throws<InvalidOperationException>(() => new Wizard(new ScriptedIO(new[] { "" }.Concat(Enumerable.Repeat("bad host name", 100)))).Run());

        [Fact]
        public void An_existing_configuration_is_edited_keeping_its_values_as_defaults()
        {
            var existing = Sample.Valid();
            // accept every default: INVIO everywhere
            var io = new ScriptedIO(Enumerable.Repeat("", 400));
            var c = new Wizard(io).Run(existing);
            Assert.True(DeploymentValidator.Validate(c).IsValid);
            Assert.Equal("vc01.corp.local", c.VCenter.Server);
            Assert.Equal(2, c.AvailabilityGroups.Count);
        }
    }
}

namespace Vha.Core.Tests
{
    public class CliSupportTests
    {
        [Fact]
        public void Options_are_parsed_strictly()
        {
            var o = CliOptions.Parse(new[] { "--config", "a.json", "--yes", "--node", "VM1", "--dry-run" });
            Assert.Equal("a.json", o.Get("config")); Assert.Equal("VM1", o.Get("NODE")); Assert.True(o.Has("yes")); Assert.True(o.Has("dry-run")); Assert.False(o.Has("scripts")); Assert.Null(o.Get("package"));
            Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(new[] { "config", "a" }));
            Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(new[] { "--confg", "a" }));
            Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(new[] { "--config" }));
            Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(new[] { "--config", "--yes" }));
            Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(new[] { "--node", "a", "--node", "b" }));
        }

        [Fact]
        public void The_node_is_detected_from_the_computer_name_or_given_explicitly()
        {
            var c = Sample.Valid();
            Assert.Equal("SQLVM02", NodeResolver.Resolve(c, null, "sqlvm02").VmName);
            Assert.Equal("SQLVM01", NodeResolver.Resolve(c, null, "SQLVM01").VmName);
            Assert.Equal("SQLVM02", NodeResolver.Resolve(c, "sqlvm02", "whatever").VmName);
            Assert.Contains("use --node", Assert.Throws<InvalidOperationException>(() => NodeResolver.Resolve(c, null, "OTHERPC")).Message);
            Assert.Contains("is not in the configuration", Assert.Throws<InvalidOperationException>(() => NodeResolver.Resolve(c, "nope", "x")).Message);
        }
    }
}
