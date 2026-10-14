using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation.Language;
using Vha.Core.Csu;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Tests;
using Vha.Core.Vmware;
using Vha.Infrastructure.Csu;
using Vha.Infrastructure.PowerShell;
using Xunit;

namespace Vha.Infrastructure.Tests
{
    /// <summary>
    /// The PowerShell glue (PowerShellHost, PowerCliVCenter, GuestDisks, WindowsDiskInventory) running on a REAL PowerShell engine.
    /// The VMware PowerCLI and Windows Storage cmdlets are replaced by PowerShell functions that behave like the originals for
    /// the parameters the product uses (functions take precedence over cmdlets of the same name).
    /// </summary>
    public class PowerShellStubTests
    {
        private const string VmwareStubs = @"
$global:Connected = $null
$global:Vms = @()
$global:Datastores = @{}
$global:ImportFails = $false
function Import-Module { param($Name, $ErrorAction) if ($global:ImportFails) { throw ""The specified module '$Name' was not loaded because no valid module file was found"" } }
function Connect-VIServer { param([string]$Server, [string]$User, [string]$Password, [string]$Protocol, [switch]$Force, $ErrorAction)
    if ($Protocol -ne 'https') { throw 'only https' }
    $global:Connected = [pscustomobject]@{ Server = $Server; User = $User; Password = $Password; Force = [bool]$Force } }
function Disconnect-VIServer { param($Server, $Confirm, [switch]$Force, $ErrorAction) $global:Connected = $null }
function Get-VM { param([string]$Name, $ErrorAction)
    $r = @($global:Vms | Where-Object { $_.Name -eq $Name })
    if ($r.Count -eq 0) { throw ""VM with name '$Name' was not found using the specified filter(s)."" }
    $r }
function Get-VMResourceConfiguration { param([Parameter(ValueFromPipeline)]$VM) process { [pscustomobject]@{ CpuReservationMhz = $VM.Cpu; MemReservationMB = $VM.Mem } } }
function Get-Snapshot { param($VM, $ErrorAction) if ($VM.Snaps -gt 0) { 1..$VM.Snaps | ForEach-Object { [pscustomobject]@{ Name = ""snap$_"" } } } }
function Get-HardDisk { param([Parameter(ValueFromPipeline)]$VM, [string]$DatastorePath)
    process {
        if ($DatastorePath) { $global:Datastores[$VM.DsName] | Where-Object { $_.Filename -eq $DatastorePath } }
        else { $VM.Disks }
    } }
function Get-Datastore { param([string]$Name, $ErrorAction)
    if (-not $global:Datastores.ContainsKey($Name)) { throw ""Datastore '$Name' not found"" }
    [pscustomobject]@{ DsName = $Name } }
function New-Object { param([string]$TypeName, $ArgumentList)
    if ($TypeName -like 'VMware.Vim.*[[]]') { return ,([object[]]::new([int]$ArgumentList)) }
    if ($TypeName -like 'VMware.Vim.*') { return @{} }
    Microsoft.PowerShell.Utility\New-Object @PSBoundParameters }
function Get-View { param($Id) $o = [pscustomobject]@{ Id = $Id }
    $o | Add-Member -MemberType ScriptMethod -Name ReconfigVM -Value { param($spec) $global:LastSpec = $spec; $global:LastViewId = $this.Id }
    $o }
function Add-StubVm { param($Name, $Id, $Power = 'PoweredOn', $Snaps = 0, $Cpu = 0, $Mem = 0, $Disks = @())
    $global:Vms += [pscustomobject]@{ Name = $Name; Id = $Id; PowerState = $Power; Snaps = $Snaps; Cpu = $Cpu; Mem = $Mem; Disks = @($Disks) } }
function New-StubDisk { param($Id, $File, $Gb, $Uuid) [pscustomobject]@{ Id = $Id; Filename = $File; CapacityGB = $Gb; ExtensionData = [pscustomobject]@{ Backing = [pscustomobject]@{ Uuid = $Uuid } } } }
";

        private static PowerShellHost NewHost()
        {
            var h = new PowerShellHost(TimeSpan.FromSeconds(60));
            h.Invoke(VmwareStubs);
            return h;
        }

        // ---------------------------------------------------------------- PowerShellHost

        [Fact]
        public void Host_returns_objects_and_exposes_typed_property_helpers()
        {
            using var h = new PowerShellHost();
            var o = h.Invoke("[pscustomobject]@{ A = 'x'; N = 42; D = 1.5; Nul = $null }").Single();
            Assert.Equal("x", PowerShellHost.Str(o, "A")); Assert.Equal(42, PowerShellHost.Int(o, "N")); Assert.Equal(1.5, PowerShellHost.Dbl(o, "D"));
            Assert.Equal("", PowerShellHost.Str(o, "Nul")); Assert.Equal("", PowerShellHost.Str(o, "Missing")); Assert.Equal(0, PowerShellHost.Int(o, "Missing"));
        }

        [Fact]
        public void Host_turns_every_kind_of_powershell_error_into_an_exception()
        {
            using var h = new PowerShellHost();
            var t = Assert.Throws<PowerShellException>(() => h.Invoke("throw 'terminating boom'"));
            Assert.Contains("terminating boom", t.Message);
            var n = Assert.Throws<PowerShellException>(() => h.Invoke("Write-Error 'non terminating boom'; 'still returned'"));
            Assert.Contains("non terminating boom", n.Message);
            var c = Assert.Throws<PowerShellException>(() => h.Invoke("Get-Item /definitely/not/there"));
            Assert.Contains("not", c.Message, StringComparison.OrdinalIgnoreCase);
            // and the host is still usable afterwards
            Assert.Equal("ok", PowerShellHost.Str(h.Invoke("[pscustomobject]@{ A = 'ok' }").Single(), "A"));
        }

        [Fact]
        public void Host_enforces_a_timeout_and_stays_usable()
        {
            using var h = new PowerShellHost();
            var ex = Assert.Throws<PowerShellException>(() => h.Invoke("Start-Sleep -Seconds 30", timeout: TimeSpan.FromSeconds(1)));
            Assert.Contains("timed out", ex.Message);
            Assert.Equal("ok", PowerShellHost.Str(h.Invoke("[pscustomobject]@{ A = 'ok' }").Single(), "A"));
        }

        [Fact]
        public void Host_passes_values_as_variables_without_interpreting_them_and_clears_them_afterwards()
        {
            using var h = new PowerShellHost();
            var nasty = "x'; throw 'injected'; $(Get-Date) `n \"quoted\"";
            var o = h.Invoke("[pscustomobject]@{ V = $secret }", new Dictionary<string, object> { { "secret", nasty } }).Single();
            Assert.Equal(nasty, PowerShellHost.Str(o, "V"));
            Assert.Equal("", PowerShellHost.Str(h.Invoke("[pscustomobject]@{ V = [string]$secret }").Single(), "V"));   // not left behind
        }

        // ---------------------------------------------------------------- PowerCliVCenter

        [Fact]
        public void Connect_passes_the_exact_server_user_and_password_even_with_shell_metacharacters()
        {
            using var h = NewHost();
            var vc = new PowerCliVCenter(h, new ListLog());
            vc.Connect("Connect-VIServer vc01.example.local -User 'EX\\svc vha' -Password 'p@ss;w$rd!`\"x#%&' -Protocol https -Force");
            var c = h.Invoke("$global:Connected").Single();
            Assert.Equal("vc01.example.local", PowerShellHost.Str(c, "Server"));
            Assert.Equal("EX\\svc vha", PowerShellHost.Str(c, "User"));
            Assert.Equal("p@ss;w$rd!`\"x#%&", PowerShellHost.Str(c, "Password"));
            Assert.Equal("True", PowerShellHost.Str(c, "Force"));
            vc.Dispose();
            Assert.Empty(h.Invoke("if ($global:Connected) { 'still connected' }"));
        }

        [Fact]
        public void Connect_forces_https_and_warns_when_http_was_configured()
        {
            using var h = NewHost();
            var log = new ListLog();
            new PowerCliVCenter(h, log).Connect("Connect-VIServer 10.0.0.1 -User a -Password b -Protocol http");
            Assert.True(log.Has("-Protocol http ignored"));
            Assert.NotEmpty(h.Invoke("$global:Connected"));
        }

        [Fact]
        public void Connect_rejects_injection_attempts_before_anything_runs()
        {
            using var h = NewHost();
            Assert.Throws<FormatException>(() => new PowerCliVCenter(h, new ListLog()).Connect("Connect-VIServer vc ; Remove-Item -Recurse /"));
            Assert.Empty(h.Invoke("if ($global:Connected) { 'connected' }"));
        }

        [Fact]
        public void Missing_powercli_module_gives_an_actionable_message()
        {
            using var h = NewHost();
            h.Invoke("$global:ImportFails = $true");
            var ex = Assert.Throws<PowerShellException>(() => new PowerCliVCenter(h, new ListLog()).Connect("Connect-VIServer vc"));
            Assert.Contains("Install-Module VMware.PowerCLI", ex.Message);
        }

        [Fact]
        public void Vm_disks_and_datastore_queries_are_parsed()
        {
            using var h = NewHost();
            h.Invoke(@"
                $d1 = New-StubDisk 'VirtualMachine-vm-772/2016' '[DS1] vm/data.vmdk' 100.5 '6000C29B-0001'
                $d2 = New-StubDisk 'VirtualMachine-vm-772/2017' '[DS1] vm/log.vmdk' 50 $null
                Add-StubVm -Name 'SQL01' -Id 'VirtualMachine-vm-772' -Power 'PoweredOn' -Snaps 2 -Cpu 4000 -Mem 8192 -Disks @($d1, $d2)
                Add-StubVm -Name 'it''s a VM' -Id 'VirtualMachine-vm-9'
                $global:Datastores['DS1'] = @([pscustomobject]@{ Filename = '[DS1] vm/free.vmdk'; DsName = 'DS1' })
                $global:Datastores['my store'] = @()");
            var vc = new PowerCliVCenter(h, new ListLog());

            var vm = vc.GetVm("SQL01");
            Assert.Equal("VirtualMachine-vm-772", vm.Id); Assert.Equal("PoweredOn", vm.PowerState); Assert.Equal(2, vm.SnapshotCount);
            Assert.Equal(4000, vm.CpuReservationMhz); Assert.Equal(8192, vm.MemReservationMB); Assert.False(vm.IsPoweredOff);
            Assert.Equal(0, vc.GetVm("it's a VM").SnapshotCount);
            Assert.ThrowsAny<Exception>(() => vc.GetVm("nope"));
            Assert.ThrowsAny<Exception>(() => vc.GetVm("x' ; throw 'pwned' ; '"));

            var disks = vc.GetDisks("SQL01");
            Assert.Equal(2, disks.Count);
            Assert.Equal("2016", disks[0].DeviceKey); Assert.Equal(100.5, disks[0].CapacityGB); Assert.Equal("6000C29B-0001", disks[0].Uuid);
            Assert.Equal("", disks[1].Uuid);
            Assert.Empty(vc.GetDisks("it's a VM"));

            Assert.True(vc.DatastoreFileExists("[DS1] vm/free.vmdk"));
            Assert.False(vc.DatastoreFileExists("[DS1] vm/other.vmdk"));
            Assert.ThrowsAny<Exception>(() => vc.DatastoreFileExists("[NoSuchStore] x.vmdk"));
        }

        [Fact]
        public void Reconfigure_script_builds_the_expected_vsphere_spec_for_add_and_remove()
        {
            using var h = NewHost();
            var vc = new PowerCliVCenter(h, new ListLog());
            var vm = new VmInfo { Id = "VirtualMachine-vm-9", Name = "SQL02" };
            var disks = new[]
            {
                new ConfiguredDisk { DeviceKey = "2016", FileName = "[DS1] o'neil/data.vmdk" },
                new ConfiguredDisk { DeviceKey = "2031", FileName = "[DS1] vm/log.vmdk" },
                new ConfiguredDisk { DeviceKey = "2062", FileName = "[DS1] vm/x.vmdk" }
            };
            vc.Reconfigure(vm, new DiskChange { Action = DiskAction.Add, Disks = disks, CpuReservationMhz = 5000, MemReservationMB = 4096 });
            string Dump(string path) => PowerShellHost.Str(h.Invoke("[pscustomobject]@{ V = [string](" + path + ") }").Single(), "V");
            Assert.Equal("VirtualMachine-vm-9", h.Invoke("$global:LastViewId").Single().ToString());
            Assert.Equal("3", Dump("$global:LastSpec.deviceChange.Count"));
            Assert.Equal("0", Dump("@($global:LastSpec.deviceChange | Where-Object { $_ -eq $null }).Count"));
            Assert.Equal("add", Dump("$global:LastSpec.deviceChange[0].operation"));
            Assert.Equal("2016", Dump("$global:LastSpec.deviceChange[0].device.key"));
            Assert.Equal("1001", Dump("$global:LastSpec.deviceChange[1].device.controllerKey"));
            Assert.Equal("15", Dump("$global:LastSpec.deviceChange[1].device.unitNumber"));
            Assert.Equal("1003", Dump("$global:LastSpec.deviceChange[2].device.controllerKey"));
            Assert.Equal("14", Dump("$global:LastSpec.deviceChange[2].device.unitNumber"));
            Assert.Equal("[DS1] o'neil/data.vmdk", Dump("$global:LastSpec.deviceChange[0].device.backing.fileName"));
            Assert.Equal("persistent", Dump("$global:LastSpec.deviceChange[0].device.backing.diskMode"));
            Assert.Equal("5000", Dump("$global:LastSpec.cpuAllocation.reservation"));
            Assert.Equal("4096", Dump("$global:LastSpec.memoryAllocation.reservation"));
            Assert.Equal("", Dump("$global:LastSpec.deviceChange[0].fileOperation"));   // never deletes VMDK files

            vc.Reconfigure(vm, new DiskChange { Action = DiskAction.Remove, Disks = disks.Take(1).ToList() });
            Assert.Equal("remove", Dump("$global:LastSpec.deviceChange[0].operation"));
            Assert.Equal("", Dump("$global:LastSpec.cpuAllocation.reservation"));   // reservations untouched when not requested
        }

        // ---------------------------------------------------------------- guest disks

        private const string StorageStubs = @"
$global:Disks = @()
$global:RemoteCalls = @()
function Get-Disk { param($Number, $ErrorAction) if ($PSBoundParameters.ContainsKey('Number')) { $global:Disks | Where-Object { $_.Number -eq $Number } } else { $global:Disks } }
function Set-Disk { param($InputObject, $IsOffline, $IsReadOnly, $ErrorAction)
    if ($PSBoundParameters.ContainsKey('IsOffline')) { $InputObject.IsOffline = [bool]$IsOffline }
    if ($PSBoundParameters.ContainsKey('IsReadOnly')) { $InputObject.IsReadOnly = [bool]$IsReadOnly } }
function Get-Partition { param($DiskNumber, $ErrorAction) $global:Partitions | Where-Object { $_.DiskNumber -eq $DiskNumber } }
function Invoke-Command { param($ComputerName, $ScriptBlock, $ErrorAction) $global:RemoteCalls += $ComputerName; & $ScriptBlock }
function Add-StubWinDisk { param($Number, $Serial, $UniqueId = $null, $SizeGB = 100, [bool]$Offline = $false, [bool]$ReadOnly = $false)
    $global:Disks += [pscustomobject]@{ Number = $Number; SerialNumber = $Serial; UniqueId = $(if ($UniqueId) { $UniqueId } else { $Serial.Trim() }); Size = [int64]$SizeGB * 1GB; IsOffline = $Offline; IsReadOnly = $ReadOnly } }
";

        [Fact]
        public void Guest_disks_are_set_offline_on_the_remote_node_and_online_locally_matching_by_serial_or_unique_id()
        {
            using var h = NewHost();
            h.Invoke(StorageStubs);
            h.Invoke(@"Add-StubWinDisk 1 '6000C29B0001  ' ; Add-StubWinDisk 2 '6000C29B0002' -UniqueId 'OTHERID' -Offline $true -ReadOnly $true; Add-StubWinDisk 3 'UNRELATED'");
            var g = new GuestDisks(h, new ListLog());
            var node = new NodeInfo { VmName = "VM1", DnsName = "vm1.example.local" };
            g.SetOffline(node, new[] { new ConfiguredDisk { UniqueId = "6000C29B0001" }, new ConfiguredDisk { UniqueId = "OTHERID" } });
            Assert.Equal("True", h.Invoke("[string]($global:Disks | Where-Object Number -eq 1).IsOffline").Single().ToString());
            Assert.Equal("False", h.Invoke("[string]($global:Disks | Where-Object Number -eq 3).IsOffline").Single().ToString());
            Assert.Equal("vm1.example.local", h.Invoke("$global:RemoteCalls[0]").Single().ToString());

            g.SetOnlineLocal(new[] { new ConfiguredDisk { UniqueId = "6000C29B0002" }, new ConfiguredDisk { UniqueId = "6000C29B0001" } });
            Assert.Equal("False", h.Invoke("[string]($global:Disks | Where-Object Number -eq 2).IsOffline").Single().ToString());
            Assert.Equal("False", h.Invoke("[string]($global:Disks | Where-Object Number -eq 2).IsReadOnly").Single().ToString());
            Assert.Equal("False", h.Invoke("[string]($global:Disks | Where-Object Number -eq 1).IsOffline").Single().ToString());
        }

        [Fact]
        public void Guest_disk_not_found_is_an_error_not_a_silent_skip()
        {
            using var h = NewHost();
            h.Invoke(StorageStubs);
            h.Invoke("Add-StubWinDisk 1 'AAA'");
            var g = new GuestDisks(h, new ListLog());
            Assert.Contains("not found", Assert.Throws<PowerShellException>(() => g.SetOnlineLocal(new[] { new ConfiguredDisk { UniqueId = "MISSING" } })).Message);
            Assert.Contains("not found", Assert.Throws<PowerShellException>(() => g.SetOffline(new NodeInfo { DnsName = "n" }, new[] { new ConfiguredDisk { UniqueId = "MISSING" } })).Message);
            g.SetOnlineLocal(new ConfiguredDisk[0]);   // nothing to do is fine
        }

        [Fact]
        public void Probe_reports_exactly_the_disks_windows_does_not_see()
        {
            using var h = NewHost();
            h.Invoke(StorageStubs);
            h.Invoke(@"Add-StubWinDisk 1 '6000C29B0001  ' ; Add-StubWinDisk 2 'X2' -UniqueId 'OTHERID'");
            var g = new GuestDisks(h, new ListLog());
            var all = new[] { new ConfiguredDisk { UniqueId = "6000C29B0001" }, new ConfiguredDisk { UniqueId = "OTHERID" }, new ConfiguredDisk { UniqueId = "GONE" }, new ConfiguredDisk { UniqueId = "GONE2" } };
            Assert.Equal(new[] { "GONE", "GONE2" }, g.MissingLocally(all).Select(d => d.UniqueId).ToArray());
            Assert.Empty(g.MissingLocally(all.Take(2).ToList()));
            Assert.Empty(g.MissingLocally(new ConfiguredDisk[0]));
            // read only: the probe never changes a disk
            Assert.Equal("False", h.Invoke("[string]($global:Disks | Where-Object Number -eq 1).IsOffline").Single().ToString());
            // quotes cannot break out of the script
            Assert.Equal(1, g.MissingLocally(new[] { new ConfiguredDisk { UniqueId = "A'; throw 'injected'; '" } }).Count);
        }

        [Fact]
        public void Unique_id_with_quotes_cannot_break_out_of_the_script()
        {
            using var h = NewHost();
            h.Invoke(StorageStubs);
            h.Invoke("Add-StubWinDisk 1 'AAA'");
            var g = new GuestDisks(h, new ListLog());
            var ex = Assert.Throws<PowerShellException>(() => g.SetOnlineLocal(new[] { new ConfiguredDisk { UniqueId = "A'; throw 'injected'; '" } }));
            // had the text been executed the error would be 'injected'; instead it is the normal "disk not found" error that merely echoes the id
            Assert.StartsWith("Disk A'; throw 'injected'; ' not found", ex.Message);
        }

        // ---------------------------------------------------------------- disk inventory (CSU)

        [Fact]
        public void Windows_disk_inventory_joins_by_uuid_reads_disksign_and_stamps_the_slot()
        {
            var root = Path.Combine(Path.GetTempPath(), "vha-inv-" + Guid.NewGuid().ToString("N"));
            var dataMount = Path.Combine(root, "AG01", "Data") + Path.DirectorySeparatorChar;
            var logMount = Path.Combine(root, "AG01", "Log") + Path.DirectorySeparatorChar;
            var otherMount = Path.Combine(root, "AG02", "Data") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Path.Combine(dataMount, "jelena")); Directory.CreateDirectory(Path.Combine(logMount, "jelena")); Directory.CreateDirectory(otherMount);
            File.WriteAllText(Path.Combine(dataMount, "jelena", "disksign.ini"), "[AG]\r\nName=AG01\r\n[SCSI]\r\nVMware_ID=9:9\r\n");
            File.WriteAllText(Path.Combine(logMount, "jelena", "disksign.ini"), "[AG]\r\nName=\r\n");            // broken: no name
            try
            {
                using var h = NewHost();
                h.Invoke(StorageStubs);
                h.Invoke($@"
                    $global:Partitions = @(
                        [pscustomobject]@{{ DiskNumber = 0; AccessPaths = @('C:\', '\\?\Volume{{1111}}\') }},
                        [pscustomobject]@{{ DiskNumber = 3; AccessPaths = @('\\?\Volume{{2222}}\', '{dataMount}') }},
                        [pscustomobject]@{{ DiskNumber = 4; AccessPaths = @('{logMount}') }},
                        [pscustomobject]@{{ DiskNumber = 5; AccessPaths = @('{otherMount}') }},
                        [pscustomobject]@{{ DiskNumber = 6; AccessPaths = @('\\?\Volume{{3333}}\') }})
                    Add-StubWinDisk 0 'OSDISK'
                    Add-StubWinDisk 3 '6000c29b0001'
                    Add-StubWinDisk 4 '6000C29B0002 '
                    Add-StubWinDisk 5 '6000C29B0003'
                    Add-StubWinDisk 6 'FOREIGN'
                    $d0 = New-StubDisk 'VirtualMachine-vm-1/2000' '[d] os.vmdk' 60 'OSDISK'
                    $d1 = New-StubDisk 'VirtualMachine-vm-1/2016' '[d] data.vmdk' 100.4 '6000C29B-0001'
                    $d2 = New-StubDisk 'VirtualMachine-vm-1/2017' '[d] log.vmdk' 50 '6000C29B-0002'
                    $d3 = New-StubDisk 'VirtualMachine-vm-1/2032' '[d] other.vmdk' 10 '6000C29B-0003'
                    Add-StubVm -Name 'VM1' -Id 'VirtualMachine-vm-1' -Disks @($d0,$d1,$d2,$d3)");
                var vc = new PowerCliVCenter(h, new ListLog());
                var inv = new WindowsDiskInventory(h, vc, "VM1", new ListLog());

                var join = inv.Detect();
                Assert.Single(join.Unmatched);                       // the FOREIGN disk
                var data = join.Detected.Single(d => d.DeviceKey == "2016");
                Assert.Equal("AG01", data.AgName); Assert.Equal(dataMount, data.MountPath); Assert.Equal("6000c29b0001", data.UniqueId); Assert.Equal(100, data.CapacityGB);
                Assert.Equal("##ERROR##", join.Detected.Single(d => d.DeviceKey == "2017").AgName);
                Assert.Null(join.Detected.Single(d => d.DeviceKey == "2032").AgName);   // no disksign.ini
                Assert.Null(join.Detected.Single(d => d.DeviceKey == "2000").AgName);   // C: has no jelena dir

                inv.StampSlot(data, ScsiSlot.FromDeviceKey(data.DeviceKey).ToString());
                Assert.Contains("VMware_ID=1:0", File.ReadAllText(Path.Combine(dataMount, "jelena", "disksign.ini")));
                inv.StampSlot(join.Detected.Single(d => d.DeviceKey == "2032"), "2:0");   // no file: silently nothing
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Disk_inventory_fails_loudly_when_two_vcenter_disks_match_one_windows_disk()
        {
            using var h = NewHost();
            h.Invoke(StorageStubs);
            h.Invoke(@"
                $global:Partitions = @()
                Add-StubWinDisk 1 'AAAA'
                $a = New-StubDisk 'VirtualMachine-vm-1/2016' '[d] a.vmdk' 1 'aaaa'
                $b = New-StubDisk 'VirtualMachine-vm-1/2017' '[d] b.vmdk' 1 'AAAA'
                Add-StubVm -Name 'VM1' -Id 'VirtualMachine-vm-1' -Disks @($a,$b)");
            var inv = new WindowsDiskInventory(h, new PowerCliVCenter(h, new ListLog()), "VM1", new ListLog());
            Assert.Throws<InvalidOperationException>(() => inv.Detect());
        }

        // ---------------------------------------------------------------- syntax of every generated script

        public static IEnumerable<object[]> GeneratedScripts()
        {
            var nasty = new[] { "plain", "o'neil", "a\u2019b", "x'; Remove-Item /; '", "with space", "[DS] f/it's.vmdk", "$(calc)", "`n", "\"q\"", "{brace}" };
            foreach (var n in nasty)
            {
                yield return new object[] { PowerCliScripts.GetVm(n) };
                yield return new object[] { PowerCliScripts.GetDisks(n) };
                yield return new object[] { PowerCliScripts.DatastoreFileExists("[ds] " + n) };
                yield return new object[] { PowerCliScripts.SetDiskOffline(new[] { new ConfiguredDisk { UniqueId = n } }, n) };
                yield return new object[] { PowerCliScripts.MissingLocalDisks(new[] { new ConfiguredDisk { UniqueId = n }, new ConfiguredDisk { UniqueId = "b" } }) };
                yield return new object[] { PowerCliScripts.SetDiskOnlineLocal(new[] { new ConfiguredDisk { UniqueId = n }, new ConfiguredDisk { UniqueId = "b" } }) };
                foreach (var a in new[] { DiskAction.Add, DiskAction.Remove })
                {
                    var change = new DiskChange
                    {
                        Action = a,
                        Disks = new[] { new ConfiguredDisk { DeviceKey = "2016", FileName = "[d] " + n }, new ConfiguredDisk { DeviceKey = "2047", FileName = "[d] y" } },
                        CpuReservationMhz = 1, MemReservationMB = 2
                    };
                    yield return new object[] { PowerCliScripts.Reconfigure("VirtualMachine-vm-" + n.Length, change) };
                }
            }
            yield return new object[] { PowerCliScripts.WindowsDisksScript };
            yield return new object[] { VCenterConnectionSpec.Parse("Connect-VIServer vc -User a -Password b -Force").Script };
        }

        [Theory]
        [MemberData(nameof(GeneratedScripts))]
        public void Every_generated_script_is_syntactically_valid_powershell(string script)
        {
            Parser.ParseInput(script, out _, out ParseError[] errors);
            Assert.True(errors.Length == 0, string.Join("; ", errors.Select(e => e.Message)) + "\r\n" + script);
        }
    }
}
