using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vha.Core.Config;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Sql;
using Vha.Core.Util;
using Vha.Core.Vmware;
using Xunit;

namespace Vha.Core.Tests
{
    public class SqlTextTests
    {
        [Theory]
        [InlineData("Sales", "[Sales]")]
        [InlineData("a]b", "[a]]b]")]
        [InlineData("x'y", "[x'y]")]
        public void QuoteName_escapes_the_closing_bracket(string input, string expected) => Assert.Equal(expected, SqlText.QuoteName(input));

        [Theory]
        [InlineData("abc", "N'abc'")]
        [InlineData("it's", "N'it''s'")]
        [InlineData("'; DROP DATABASE x; --", "N'''; DROP DATABASE x; --'")]
        public void Literal_doubles_quotes(string input, string expected) => Assert.Equal(expected, SqlText.Literal(input));

        [Fact]
        public void Invalid_identifiers_are_rejected()
        {
            Assert.Throws<ArgumentException>(() => SqlText.QuoteName(""));
            Assert.Throws<ArgumentException>(() => SqlText.QuoteName(new string('a', 129)));
            Assert.Throws<ArgumentException>(() => SqlText.QuoteName("a\0b"));
            Assert.Throws<ArgumentException>(() => SqlText.Literal("a\0"));
        }
    }

    public class AttachScriptBuilderTests
    {
        private static DatabaseFile F(int id, string path) => new DatabaseFile { FileId = id, PhysicalName = path };

        [Fact]
        public void Attach_lists_every_file_in_file_id_order()
        {
            var sql = AttachScriptBuilder.BuildAttach("Sales", new[] { F(3, @"M:\Data\s_idx.ndf"), F(2, @"M:\Log\s.ldf"), F(1, @"M:\Data\s.mdf"), F(4, @"M:\Data\s_lob.ndf") });
            Assert.StartsWith("CREATE DATABASE [Sales] ON ", sql);
            Assert.EndsWith("FOR ATTACH", sql);
            var order = new[] { "s.mdf", "s.ldf", "s_idx.ndf", "s_lob.ndf" }.Select(x => sql.IndexOf(x)).ToList();
            Assert.Equal(order.OrderBy(x => x), order);
            Assert.Equal(4, sql.Split(new[] { "FILENAME" }, StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void Attach_escapes_names_and_paths()
        {
            var sql = AttachScriptBuilder.BuildAttach("we]ird", new[] { F(1, @"M:\o'neil\a.mdf"), F(2, @"M:\o'neil\a.ldf") });
            Assert.Contains("[we]]ird]", sql);
            Assert.Contains(@"N'M:\o''neil\a.mdf'", sql);
        }

        [Fact]
        public void Attach_refuses_incomplete_descriptions()
        {
            Assert.Throws<ArgumentException>(() => AttachScriptBuilder.BuildAttach("x", new DatabaseFile[0]));
            Assert.Throws<ArgumentException>(() => AttachScriptBuilder.BuildAttach("x", new[] { F(2, "a") }));
            Assert.Throws<ArgumentException>(() => AttachScriptBuilder.BuildAttach("x", new[] { F(1, "a"), F(1, "b") }));
            Assert.Throws<ArgumentException>(() => AttachScriptBuilder.BuildAttach("x", new[] { F(1, "a"), F(2, " ") }));
        }

        [Fact]
        public void Detach_and_offline_batches_are_quoted_and_guarded()
        {
            var d = AttachScriptBuilder.BuildDetach("a'b]c");
            Assert.Contains("N'a''b]c'", d);
            Assert.Contains("[a'b]]c]", d);
            Assert.Contains("IF EXISTS", d);
            Assert.Contains("sp_detach_db", d);
            Assert.Contains("SET OFFLINE WITH ROLLBACK IMMEDIATE", AttachScriptBuilder.BuildOffline("x"));
            Assert.Equal("ALTER DATABASE [x] SET ONLINE", AttachScriptBuilder.BuildSetOnline("x"));
        }
    }

    public class ScsiSlotTests
    {
        [Theory]
        [InlineData(2000, 0, 0)]
        [InlineData(2001, 0, 1)]
        [InlineData(2015, 0, 15)]
        [InlineData(2016, 1, 0)]
        [InlineData(2031, 1, 15)]   // the old code mapped this to controller 0 / unit -1
        [InlineData(2032, 2, 0)]
        [InlineData(2046, 2, 14)]   // the old code returned unit 0
        [InlineData(2047, 2, 15)]
        [InlineData(2063, 3, 15)]   // the old code stopped at 2060
        [InlineData(2062, 3, 14)]
        public void DeviceKey_round_trips(int key, int controller, int unit)
        {
            Assert.True(ScsiSlot.TryFromDeviceKey(key, out var s));
            Assert.Equal(controller, s.Controller);
            Assert.Equal(unit, s.Unit);
            Assert.Equal(key, s.DeviceKey);
            Assert.Equal(1000 + controller, s.ControllerKey);
        }

        [Theory]
        [InlineData(1999)] [InlineData(2007)] [InlineData(2023)] [InlineData(2039)] [InlineData(2055)] [InlineData(2064)] [InlineData(31000)] [InlineData(16000)]
        public void Non_scsi_or_reserved_keys_are_rejected(int key) => Assert.False(ScsiSlot.TryFromDeviceKey(key, out _));

        [Fact]
        public void FromDeviceKey_explains_unsupported_controllers()
        {
            var ex = Assert.Throws<NotSupportedException>(() => ScsiSlot.FromDeviceKey("31000"));
            Assert.Contains("NVMe", ex.Message);
            Assert.Throws<NotSupportedException>(() => ScsiSlot.FromDeviceKey("abc"));
            Assert.Equal("1:15", ScsiSlot.FromDeviceKey(" 2031 ").ToString());
        }

        [Fact]
        public void Invalid_slots_cannot_be_built()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ScsiSlot(4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ScsiSlot(0, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ScsiSlot(0, 16));
        }
    }

    public class QuotingTests
    {
        [Theory]
        [InlineData("plain", "'plain'")]
        [InlineData("o'neil", "'o''neil'")]
        [InlineData("a\u2019b", "'a\u2019\u2019b'")]
        [InlineData("\u2018x\u201B", "'\u2018\u2018x\u201B\u201B'")]
        public void PsQuote_doubles_every_kind_of_single_quote(string v, string expected) => Assert.Equal(expected, PsQuote.Single(v));

        [Fact]
        public void PsQuote_neutralises_injection() => Assert.Equal("'x''; Remove-Item C:\\ -Recurse; ''y'", PsQuote.Single("x'; Remove-Item C:\\ -Recurse; 'y"));

        [Theory]
        [InlineData("abc", "abc")]
        [InlineData("a b", "\"a b\"")]
        [InlineData("", "\"\"")]
        [InlineData("a\"b", "\"a\\\"b\"")]
        [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
        public void ProcessArgs_follows_windows_rules(string v, string expected) => Assert.Equal(expected, ProcessArgs.Quote(v));
    }

    public class DiskMatcherTests
    {
        private static VmDisk V(string id, string file, string uuid = null) => new VmDisk { Id = id, FileName = file, Uuid = uuid };
        private static ConfiguredDisk C(string key, string file, string uid = null) => new ConfiguredDisk { DeviceKey = key, FileName = file, UniqueId = uid };

        [Fact]
        public void Key_is_compared_as_a_whole_token_not_as_a_substring_of_the_id()
        {
            // old code: Id LIKE '%2050%' matched the moref of VM 2050
            var m = DiskMatcher.Find(C("2050", "[d] a.vmdk"), new[] { V("VirtualMachine-vm-2050/2034", "[d] b.vmdk") });
            Assert.Equal(MatchKind.None, m.Kind);
            m = DiskMatcher.Find(C("2050", "[d] a.vmdk"), new[] { V("VirtualMachine-vm-772/2050", "[d] a.vmdk") });
            Assert.Equal(MatchKind.Exact, m.Kind);
        }

        [Fact]
        public void Partial_keys_do_not_match() =>
            Assert.Equal(MatchKind.None, DiskMatcher.Find(C("205", "[d] a"), new[] { V("VirtualMachine-vm-1/2050", "[d] a") }).Kind);

        [Fact]
        public void File_name_comparison_ignores_case() =>
            Assert.Equal(MatchKind.Exact, DiskMatcher.Find(C("2016", "[DS] a/Data.vmdk"), new[] { V("VirtualMachine-vm-1/2016", "[ds] a/data.VMDK") }).Kind);

        [Fact]
        public void Moved_file_is_accepted_only_when_the_uuid_agrees()
        {
            var vm = new[] { V("VirtualMachine-vm-1/2016", "[new] a.vmdk", "6000C29B-6F1D") };
            Assert.Equal(MatchKind.MovedFile, DiskMatcher.Find(C("2016", "[old] a.vmdk", "6000c29b6f1d"), vm).Kind);
            Assert.Equal(MatchKind.None, DiskMatcher.Find(C("2016", "[old] a.vmdk", "FFFF"), vm).Kind);
        }

        [Fact]
        public void Different_file_without_any_uuid_is_ambiguous_and_refused() =>
            Assert.Throws<InvalidOperationException>(() => DiskMatcher.Find(C("2016", "[old] a.vmdk"), new[] { V("VirtualMachine-vm-1/2016", "[new] a.vmdk") }));

        [Fact]
        public void Duplicated_key_on_one_vm_is_refused() =>
            Assert.Throws<InvalidOperationException>(() => DiskMatcher.Find(C("2016", "a"), new[] { V("x/2016", "a"), V("y/2016", "a") }));
    }

    public class DiskIdentityTests
    {
        [Fact]
        public void Normalize_removes_dashes_and_case() => Assert.Equal("6000C29B6F1D", DiskIdentity.Normalize(" 6000c29b-6f1d "));

        [Fact]
        public void Empty_serials_never_match() { Assert.False(DiskIdentity.SameDisk("", "")); Assert.False(DiskIdentity.SameDisk(null, "A")); }

        [Fact]
        public void Join_matches_by_uuid_and_reports_unmatched_disks()
        {
            var win = new[]
            {
                new WindowsDisk { Number = 0, SerialNumber = "OSDISK", MountPath = "C:\\" },
                new WindowsDisk { Number = 3, SerialNumber = "6000c29b6f1d0001", MountPath = @"M:\ag01\Data\" },
                new WindowsDisk { Number = 4, SerialNumber = "6000C29B6F1D0002", MountPath = @"M:\ag01\Log\" },
                new WindowsDisk { Number = 5, SerialNumber = "NOTVMWARE", MountPath = null }
            };
            var vm = new[]
            {
                new VmDisk { Id = "VirtualMachine-vm-1/2000", FileName = "[d] os.vmdk", Uuid = "OSDISK", CapacityGB = 60 },
                new VmDisk { Id = "VirtualMachine-vm-1/2016", FileName = "[d] data.vmdk", Uuid = "6000C29B-6F1D-0001", CapacityGB = 100.2 },
                new VmDisk { Id = "VirtualMachine-vm-1/2017", FileName = "[d] log.vmdk", Uuid = "6000C29B-6F1D-0002", CapacityGB = 50 }
            };
            var r = DiskIdentity.Join(win, vm);
            Assert.Equal(3, r.Detected.Count);
            Assert.Single(r.Unmatched);
            var data = r.Detected.Single(d => d.Number == 3);
            Assert.Equal("2016", data.DeviceKey);
            Assert.Equal("[d] data.vmdk", data.FileName);
            Assert.Equal(100, data.CapacityGB);
        }

        [Fact]
        public void Join_refuses_an_ambiguous_uuid() =>
            Assert.Throws<InvalidOperationException>(() => DiskIdentity.Join(
                new[] { new WindowsDisk { Number = 1, SerialNumber = "A" } },
                new[] { new VmDisk { Id = "x/2016", Uuid = "A" }, new VmDisk { Id = "x/2017", Uuid = "a" } }));
    }

    public class PowerCliScriptsTests
    {
        private static ConfiguredDisk D(string key, string file) => new ConfiguredDisk { DeviceKey = key, FileName = file };

        [Fact]
        public void Reconfigure_has_no_null_holes_and_uses_correct_controller_and_unit()
        {
            var s = PowerCliScripts.Reconfigure("VirtualMachine-vm-9", new DiskChange { Action = DiskAction.Remove, Disks = new[] { D("2031", "[d] a.vmdk"), D("2062", "[d] b.vmdk") } });
            Assert.Contains("VirtualDeviceConfigSpec[] (2)", s);
            Assert.Contains("deviceChange[0].device.controllerKey = 1001", s);
            Assert.Contains("deviceChange[0].device.unitNumber = 15", s);
            Assert.Contains("deviceChange[1].device.controllerKey = 1003", s);
            Assert.Contains("deviceChange[1].device.unitNumber = 14", s);
            Assert.Contains("operation = 'remove'", s);
            Assert.DoesNotContain("fileOperation", s);   // VMDKs must never be deleted
            Assert.EndsWith("$_this.ReconfigVM($spec)", s);
        }

        [Fact]
        public void Reconfigure_add_quotes_file_names()
        {
            var s = PowerCliScripts.Reconfigure("VirtualMachine-vm-9", new DiskChange { Action = DiskAction.Add, Disks = new[] { D("2016", "[d s] o'neil/a.vmdk") } });
            Assert.Contains("fileName = '[d s] o''neil/a.vmdk'", s);
            Assert.Contains("operation = 'add'", s);
        }

        [Fact]
        public void Reconfigure_reservations_are_emitted_only_when_requested_and_clamped()
        {
            var none = PowerCliScripts.Reconfigure("v", new DiskChange { Action = DiskAction.Add, Disks = new[] { D("2016", "[d] a") } });
            Assert.DoesNotContain("reservation", none);
            var some = PowerCliScripts.Reconfigure("v", new DiskChange { Action = DiskAction.Add, Disks = new[] { D("2016", "[d] a") }, CpuReservationMhz = -5, MemReservationMB = 2048 });
            Assert.Contains("cpuAllocation.reservation = 0", some);
            Assert.Contains("memoryAllocation.reservation = 2048", some);
        }

        [Fact]
        public void Reconfigure_rejects_unsupported_keys_and_empty_lists()
        {
            Assert.Throws<NotSupportedException>(() => PowerCliScripts.Reconfigure("v", new DiskChange { Action = DiskAction.Add, Disks = new[] { D("31000", "[d] a") } }));
            Assert.Throws<ArgumentException>(() => PowerCliScripts.Reconfigure("v", new DiskChange { Action = DiskAction.Add, Disks = new ConfiguredDisk[0] }));
        }

        [Fact]
        public void Vm_names_cannot_inject_commands()
        {
            var s = PowerCliScripts.GetVm("x'; Stop-VM -VM * -Confirm:$false; '");
            Assert.Contains("-Name 'x''; Stop-VM -VM * -Confirm:$false; '''", s);
            var g = PowerCliScripts.GetDisks("a b");
            Assert.Contains("-Name 'a b'", g);
        }

        [Theory]
        [InlineData("[DS1] vm/a.vmdk", "DS1")]
        [InlineData("[my store] a", "my store")]
        public void DatastoreName_is_extracted(string path, string expected) => Assert.Equal(expected, PowerCliScripts.DatastoreName(path));

        [Theory] [InlineData("vm/a.vmdk")] [InlineData("[]x")] [InlineData("")]
        public void DatastoreName_rejects_bad_paths(string path) => Assert.Throws<ArgumentException>(() => PowerCliScripts.DatastoreName(path));

        [Fact]
        public void Disk_online_offline_scripts_quote_ids()
        {
            var off = PowerCliScripts.SetDiskOffline(new[] { new ConfiguredDisk { UniqueId = "A'B" } }, "srv'1");
            Assert.Contains("-ComputerName 'srv''1'", off);
            Assert.Contains("'A''B'", off);
            Assert.Contains("-IsOffline $false", PowerCliScripts.SetDiskOnlineLocal(new[] { new ConfiguredDisk { UniqueId = "A" } }));
        }
    }

    public class VCenterConnectionSpecTests
    {
        [Fact]
        public void Parses_the_forms_used_in_the_documentation()
        {
            var s = VCenterConnectionSpec.Parse("Connect-VIServer vc01.example.local -User EXAMPLE\\svcVha -Password 'p@ss;w$rd!' -Protocol https");
            Assert.Equal("vc01.example.local", s.Server);
            Assert.Equal("EXAMPLE\\svcVha", s.User);
            Assert.Equal("p@ss;w$rd!", s.Password);
            Assert.Empty(s.Warnings);
            Assert.Contains("-Server $vcServer", s.Script);
            Assert.Contains("-User $vcUser -Password $vcPassword", s.Script);
            Assert.Equal("p@ss;w$rd!", s.Variables["vcPassword"]);
        }

        [Fact]
        public void Server_only_and_trusted_domain_form()
        {
            var s = VCenterConnectionSpec.Parse("Connect-VIServer vc01.example.local -Protocol https");
            Assert.Null(s.User);
            Assert.DoesNotContain("$vcUser", s.Script);
            Assert.Single(s.Variables);
        }

        [Fact]
        public void Plain_http_is_never_used()
        {
            var s = VCenterConnectionSpec.Parse("Connect-VIServer 10.0.0.1 -User a -Password b -Protocol http");
            Assert.Single(s.Warnings);
            Assert.Contains("-Protocol https", s.Script);
            Assert.DoesNotContain("-Protocol http ", s.Script + " ");
        }

        [Theory]
        [InlineData("Connect-VIServer vc ; Remove-VM x")]
        [InlineData("Connect-VIServer vc | Out-File c:\\x")]
        [InlineData("Connect-VIServer vc & calc")]
        [InlineData("Connect-VIServer $(calc)")]
        [InlineData("Connect-VIServer vc -User a -Password `calc")]
        [InlineData("Connect-VIServer vc -SomethingElse 1")]
        [InlineData("Get-VM *")]
        [InlineData("Connect-VIServer vc -User a")]
        [InlineData("Connect-VIServer")]
        [InlineData("Connect-VIServer vc -Password 'unterminated")]
        [InlineData("Connect-VIServer vc extra")]
        [InlineData("")]
        public void Anything_that_is_not_a_plain_connection_is_rejected(string text) => Assert.Throws<FormatException>(() => VCenterConnectionSpec.Parse(text));

        [Fact]
        public void Double_quotes_cannot_expand_variables() =>
            Assert.Throws<FormatException>(() => VCenterConnectionSpec.Parse("Connect-VIServer vc -User a -Password \"$env:USERNAME\""));

        [Fact]
        public void Dpapi_passwords_are_decrypted_by_the_caller()
        {
            var s = VCenterConnectionSpec.Parse("Connect-VIServer vc -User a -Password dpapi:QUJD", x => "decrypted-" + x);
            Assert.Equal("decrypted-QUJD", s.Password);
            Assert.Throws<FormatException>(() => VCenterConnectionSpec.Parse("Connect-VIServer vc -User a -Password dpapi:QUJD"));
        }
    }

    public class IniAndSettingsTests
    {
        private const string Sample = "[General]\r\nPollingFreqMilliseconds=15000\r\n; comment\r\n[VIM]\r\nVirtualMachineName=ENTGSQL01\r\n[AG]\r\nAGName=isql2016a-AG01\r\nJelena-isql2016a-AG01=isql2016a-AG01\r\nInstance=entgsql01\\isql2016a\r\nReservation=N\r\nTaskName=CSU - isql2016a - AG01\r\n";

        [Fact]
        public void Reads_keys_case_insensitively_and_returns_empty_for_missing()
        {
            var ini = IniDocument.Parse(Sample);
            Assert.Equal("ENTGSQL01", ini.Get("vim", "virtualmachinename"));
            Assert.Equal("", ini.Get("VIM", "nope"));
            Assert.Equal("", ini.Get("nope", "x"));
            Assert.Equal("CSU - isql2016a - AG01", ini.Get("AG", "TaskName"));
        }

        [Fact]
        public void Set_preserves_comments_and_order_and_appends_where_needed()
        {
            var ini = IniDocument.Parse("[AG]\r\nName=x\r\n\r\n[SCSI]\r\nVMware_ID=1:1\r\n; keep\r\n");
            ini.Set("SCSI", "VMware_ID", "2:3");
            ini.Set("AG", "Extra", "1");
            ini.Set("New", "K", "V");
            var t = ini.ToString();
            Assert.Contains("VMware_ID=2:3", t);
            Assert.Contains("; keep", t);
            Assert.True(t.IndexOf("Extra=1") < t.IndexOf("[SCSI]"));
            Assert.Equal("V", IniDocument.Parse(t).Get("New", "K"));
            Assert.Equal("x", IniDocument.Parse(t).Get("AG", "Name"));
        }

        [Fact]
        public void Save_is_atomic_and_round_trips()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vha-ini-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "disksign.ini");
                File.WriteAllText(path, "[AG]\r\nName=a\r\n");
                var ini = IniDocument.Load(path);
                ini.Set("SCSI", "VMware_ID", "1:1");
                ini.Save(path);
                Assert.Equal("1:1", IniDocument.Load(path).Get("SCSI", "VMware_ID"));
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Service_settings_are_read_with_defaults()
        {
            var s = ServiceSettings.FromIni(IniDocument.Parse(Sample), "Jelena-isql2016a-AG01");
            Assert.Equal("ENTGSQL01", s.VirtualMachineName);
            Assert.Equal("isql2016a-AG01", s.AgName);
            Assert.False(s.ReservationEnabled);
            Assert.Equal(OwnerUnreachablePolicy.Force, s.UnreachablePolicy);
            Assert.Equal(TimeSpan.FromSeconds(120), s.CsuWaitTimeout);
            Assert.Equal(TimeSpan.FromMinutes(15), s.FailoverTimeout);
        }

        [Theory]
        [InlineData("VirtualMachineName=ENTGSQL01", "VirtualMachineName=")]
        [InlineData("Instance=entgsql01\\isql2016a", "Instance=")]
        [InlineData("TaskName=CSU - isql2016a - AG01", "TaskName=")]
        [InlineData("Reservation=N", "Reservation=")]
        [InlineData("Reservation=N", "Reservation=maybe")]
        public void Missing_or_invalid_values_fail_at_startup(string find, string replace) =>
            Assert.Throws<InvalidOperationException>(() => ServiceSettings.FromIni(IniDocument.Parse(Sample.Replace(find, replace)), "Jelena-isql2016a-AG01"));

        [Fact]
        public void Unknown_service_name_fails() =>
            Assert.Throws<InvalidOperationException>(() => ServiceSettings.FromIni(IniDocument.Parse(Sample), "Jelena-other"));

        [Fact]
        public void Optional_failover_section_is_validated()
        {
            var ok = ServiceSettings.FromIni(IniDocument.Parse(Sample + "[Failover]\r\nOwnerUnreachablePolicy=abort\r\nCsuWaitSeconds=30\r\nFailoverTimeoutSeconds=600\r\n"), "Jelena-isql2016a-AG01");
            Assert.Equal(OwnerUnreachablePolicy.Abort, ok.UnreachablePolicy);
            Assert.Equal(TimeSpan.FromSeconds(30), ok.CsuWaitTimeout);
            Assert.Throws<InvalidOperationException>(() => ServiceSettings.FromIni(IniDocument.Parse(Sample + "[Failover]\r\nOwnerUnreachablePolicy=Maybe\r\n"), "Jelena-isql2016a-AG01"));
            Assert.Throws<InvalidOperationException>(() => ServiceSettings.FromIni(IniDocument.Parse(Sample + "[Failover]\r\nCsuWaitSeconds=0\r\n"), "Jelena-isql2016a-AG01"));
            Assert.Throws<InvalidOperationException>(() => ServiceSettings.FromIni(IniDocument.Parse(Sample + "[Failover]\r\nFailoverTimeoutSeconds=abc\r\n"), "Jelena-isql2016a-AG01"));
        }
    }

    public class FileLogTests
    {
        [Fact]
        public void Writes_daily_files_and_purges_old_ones()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vha-log-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "20200101.log"), "old");
                File.WriteAllText(Path.Combine(dir, "other.txt"), "keep");
                var now = new DateTime(2026, 3, 5, 10, 0, 0);
                var log = new FileLog(dir, 31, () => now);
                log.Info("hello"); log.Error("boom");
                now = now.AddDays(1);
                log.Warn("next day");
                Assert.True(File.Exists(Path.Combine(dir, "20260305.log")));
                Assert.True(File.Exists(Path.Combine(dir, "20260306.log")));
                Assert.False(File.Exists(Path.Combine(dir, "20200101.log")));
                Assert.True(File.Exists(Path.Combine(dir, "other.txt")));
                Assert.Contains("ERROR\tboom", File.ReadAllText(Path.Combine(dir, "20260305.log")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Never_throws_when_the_log_cannot_be_written()
        {
            var file = Path.GetTempFileName();   // a file where a directory is expected
            try
            {
                var log = new FileLog(Path.Combine(file, "sub"));
                log.Info("x"); log.Error("y");
                Assert.Equal(2, log.WriteFailures);
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void Composite_forwards_to_every_log()
        {
            var a = new ListLog(); var b = new ListLog();
            new CompositeLog(a, b).Warn("w");
            Assert.True(a.Has("w")); Assert.True(b.Has("w"));
        }
    }
}
