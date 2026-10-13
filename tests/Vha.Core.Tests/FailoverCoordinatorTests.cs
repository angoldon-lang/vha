using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Config;
using Vha.Core.Failover;
using Vha.Core.Model;
using Vha.Core.Vmware;
using Xunit;

namespace Vha.Core.Tests
{
    public class FailoverCoordinatorTests
    {
        private const string Data = "[DS1] SQLVM/data.vmdk";
        private const string LogVmdk = "[DS1] SQLVM/log.vmdk";

        private sealed class Fx
        {
            public Journal J = new Journal();
            public FakeVCenter VC; public FakeStore Store; public FakeConnector Conn; public FakeGuest Guest; public FakeLocalSql Local; public FakeCsuTask Csu; public FakeClock Clock; public ListLog Log;
            public ServiceSettings Settings;
            public Fx(string ini = "")
            {
                VC = new FakeVCenter(J); Store = new FakeStore(J); Conn = new FakeConnector(J); Guest = new FakeGuest(J); Local = new FakeLocalSql(J); Csu = new FakeCsuTask(J); Clock = new FakeClock(); Log = new ListLog();
                VC.AddVm("VM1", cpu: 4000, mem: 8192).AddVm("VM2", cpu: 1000, mem: 2048);
                Store.Nodes.Add(new NodeInfo { VmName = "VM1", DnsName = "vm1.local", ConnectionString = "x" });
                Store.Nodes.Add(new NodeInfo { VmName = "VM2", DnsName = "vm2.local", ConnectionString = "y", IsLocal = true });
                Store.Disks.Add(new ConfiguredDisk { DeviceKey = "2016", FileName = Data, UniqueId = "AAA", CapacityGB = 100 });
                Store.Disks.Add(new ConfiguredDisk { DeviceKey = "2017", FileName = LogVmdk, UniqueId = "BBB", CapacityGB = 50 });
                VC.Attach("VM1", "2016", Data, "AAA").Attach("VM1", "2017", LogVmdk, "BBB");
                var text = "[VIM]\nVirtualMachineName=VM2\n[AG]\nJelena-AG01=AG01\nReservation=N\nTaskName=CSU\nInstance=host\\i\n" + ini;
                Settings = ServiceSettings.FromIni(IniDocument.Parse(text), "Jelena-AG01");
            }
            public FailoverCoordinator Build() => new FailoverCoordinator(Settings, Store, VC, Conn, Guest, Local, Csu, Clock, Log);
        }

        [Fact]
        public void Normal_failover_moves_disks_in_the_safe_order()
        {
            var f = new Fx();
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            var e = f.J.Events;
            Assert.True(f.J.IndexOf("csu-disable") < f.J.IndexOf("release-dbs:VM1"));
            Assert.True(f.J.IndexOf("release-dbs:VM1") < f.J.IndexOf("guest-offline:VM1"));
            Assert.True(f.J.IndexOf("guest-offline:VM1") < f.J.IndexOf("owner:" + FailoverCoordinator.TransitioningMarker));
            Assert.True(f.J.IndexOf("owner:" + FailoverCoordinator.TransitioningMarker) < f.J.IndexOf("remove:VM1:2016,2017"));
            Assert.True(f.J.IndexOf("remove:VM1:2016,2017") < f.J.IndexOf("add:VM2:2016,2017"));
            Assert.True(f.J.IndexOf("add:VM2:2016,2017") < f.J.IndexOf("owner:VM2"));
            Assert.True(f.J.IndexOf("owner:VM2") < f.J.IndexOf("scn:6"));
            Assert.True(f.J.IndexOf("scn:6") < f.J.IndexOf("guest-online"));
            Assert.True(f.J.IndexOf("guest-online") < f.J.IndexOf("dbs-online"));
            Assert.Equal("csu-enable", e.Last());
            Assert.Equal("VM2", f.Store.Ag.CurrentOwner);
            Assert.Equal(2, f.VC.Disks["VM2"].Count);
            Assert.Empty(f.VC.Disks["VM1"]);
        }

        [Fact]
        public void Restart_on_the_node_that_already_owns_the_disks_only_brings_databases_online()
        {
            var f = new Fx();
            f.VC.Disks["VM1"].Clear();
            f.VC.Attach("VM2", "2016", Data, "AAA").Attach("VM2", "2017", LogVmdk, "BBB");
            f.Store.Ag.CurrentOwner = "VM2";
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.AlreadyOwner, r.Outcome);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("remove") || e.StartsWith("add") || e.StartsWith("scn"));
            Assert.True(f.J.Has("dbs-online"));
        }

        [Fact]
        public void Restart_with_stale_owner_corrects_owner_and_writes_scn()
        {
            var f = new Fx();
            f.VC.Disks["VM1"].Clear();
            f.VC.Attach("VM2", "2016", Data, "AAA").Attach("VM2", "2017", LogVmdk, "BBB");
            f.Store.Ag.CurrentOwner = FailoverCoordinator.TransitioningMarker;
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.AlreadyOwner, r.Outcome);
            Assert.True(f.J.Has("owner:VM2"));
            Assert.True(f.J.Has("scn:6"));
        }

        [Fact]
        public void Crash_recovery_disks_attached_nowhere_are_attached_from_the_datastore()
        {
            var f = new Fx();
            f.VC.Disks["VM1"].Clear();
            f.VC.DatastoreFiles.Add(Data); f.VC.DatastoreFiles.Add(LogVmdk);
            f.Store.Ag.CurrentOwner = "VM1"; // stale: the old service got stuck here forever
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("remove"));
            Assert.Equal(2, f.VC.Disks["VM2"].Count);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("release-dbs"));
        }

        [Fact]
        public void Disk_missing_everywhere_aborts_before_changing_anything()
        {
            var f = new Fx();
            f.VC.Disks["VM1"].Clear();
            var ex = Assert.Throws<FailoverException>(() => f.Build().Run());
            Assert.Contains("not found in the datastore", ex.Message);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("owner") || e.StartsWith("add") || e.StartsWith("remove"));
        }

        [Fact]
        public void Mixed_state_disks_split_between_two_holders_are_consolidated()
        {
            var f = new Fx();
            f.VC.AddVm("VM3");
            f.Store.Nodes.Add(new NodeInfo { VmName = "VM3", DnsName = "vm3.local", ConnectionString = "z" });
            f.VC.Disks["VM1"].RemoveAll(d => d.DeviceKey == "2017");
            f.VC.Attach("VM3", "2017", LogVmdk, "BBB");
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.True(f.J.Has("remove:VM1:2016"));
            Assert.True(f.J.Has("remove:VM3:2017"));
            Assert.Equal(2, f.VC.Disks["VM2"].Count);
        }

        [Fact]
        public void Disk_attached_to_two_nodes_is_refused()
        {
            var f = new Fx();
            f.VC.Attach("VM2", "2016", Data, "AAA");
            var ex = Assert.Throws<FailoverException>(() => f.Build().Run());
            Assert.Contains("more than one node", ex.Message);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("remove") || e.StartsWith("add"));
        }

        [Fact]
        public void Transitioning_marker_failure_aborts_before_any_detach()
        {
            var f = new Fx();
            f.Store.FailSetOwnerTransitioning = true;
            var ex = Assert.Throws<FailoverException>(() => f.Build().Run());
            Assert.Contains("aborted before touching any disk", ex.Message);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("remove") || e.StartsWith("add"));
            Assert.Equal(2, f.VC.Disks["VM1"].Count);
            Assert.True(f.J.Has("csu-enable"));
        }

        [Fact]
        public void Failed_attach_rolls_the_disks_back_to_the_previous_holder()
        {
            var f = new Fx();
            f.VC.FailReconfigure = (vm, c) => vm == "VM2" && c.Action == DiskAction.Add;
            var ex = Assert.Throws<FailoverException>(() => f.Build().Run());
            Assert.Contains("returned to their previous holder", ex.Message);
            Assert.Equal(2, f.VC.Disks["VM1"].Count);
            Assert.Empty(f.VC.Disks["VM2"]);
            Assert.True(f.J.Has("csu-enable"));
        }

        [Fact]
        public void Failed_attach_with_failed_rollback_is_reported_and_does_not_hide_the_original_error()
        {
            var f = new Fx();
            f.VC.FailReconfigure = (vm, c) => c.Action == DiskAction.Add;
            var ex = Assert.Throws<FailoverException>(() => f.Build().Run());
            Assert.Contains("vCenter refused Add on VM2", ex.Message);
            Assert.True(f.Log.Has("ROLLBACK FAILED"));
        }

        [Fact]
        public void Attach_that_vcenter_reports_ok_but_did_not_happen_is_detected()
        {
            var f = new Fx();
            f.VC.SilentlyIgnoreAdd = true;
            Assert.Throws<FailoverException>(() => f.Build().Run());
        }

        [Fact]
        public void Scn_write_failure_is_no_longer_swallowed()
        {
            var f = new Fx();
            f.Store.FailWriteScn = true;
            var ex = Assert.Throws<InvalidOperationException>(() => f.Build().Run());
            Assert.Contains("scn insert failed", ex.Message);
            Assert.DoesNotContain("guest-online", f.J.Events);
        }

        [Fact]
        public void Owner_not_reachable_with_Force_policy_detaches_anyway_and_warns()
        {
            var f = new Fx();
            f.Conn.Status["VM1"] = ConnectStatus.NotAvailable;
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.DoesNotContain("release-dbs:VM1", f.J.Events);
            Assert.True(f.Log.Has("OwnerUnreachablePolicy=Force"));
        }

        [Fact]
        public void Owner_not_reachable_with_Abort_policy_changes_nothing()
        {
            var f = new Fx("[Failover]\nOwnerUnreachablePolicy=Abort\n");
            f.Conn.Status["VM1"] = ConnectStatus.Timeout;
            var ex = Assert.Throws<FailoverException>(() => f.Build().Run());
            Assert.Contains("failover aborted", ex.Message);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("owner") || e.StartsWith("remove") || e.StartsWith("add"));
        }

        [Fact]
        public void Powered_off_owner_is_a_certain_takeover_even_with_Abort_policy()
        {
            var f = new Fx("[Failover]\nOwnerUnreachablePolicy=Abort\n");
            f.VC.Vms["VM1"].PowerState = "PoweredOff";
            f.Conn.Status["VM1"] = ConnectStatus.NotAvailable;
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
        }

        [Fact]
        public void Failure_to_release_databases_on_a_reachable_owner_aborts_before_detaching()
        {
            var f = new Fx();
            f.Conn.ReleaseThrows = true;
            Assert.Throws<InvalidOperationException>(() => f.Build().Run());
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("remove") || e.StartsWith("owner"));
        }

        [Fact]
        public void Holder_with_snapshots_triggers_failback_without_touching_anything()
        {
            var f = new Fx();
            f.VC.Vms["VM1"].SnapshotCount = 2;
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.FailbackRequired, r.Outcome);
            Assert.Equal("vm1.local", r.FailbackNode.DnsName);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("owner") || e.StartsWith("remove") || e.StartsWith("add") || e == "dbs-online");
        }

        [Fact]
        public void Running_csu_task_is_waited_for()
        {
            var f = new Fx();
            f.Csu.RunningChecks = 3;
            f.Build().Run();
            Assert.DoesNotContain("csu-stop", f.J.Events);
            Assert.True(f.Log.Has("waiting 2 seconds"));
        }

        [Fact]
        public void Stuck_csu_task_is_stopped_after_the_timeout_and_the_failover_goes_on()
        {
            var f = new Fx();
            f.Csu.NeverStops = true;
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.True(f.J.Has("csu-stop"));
            Assert.True(f.Clock.Now >= new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc));
        }

        [Fact]
        public void Csu_task_is_re_enabled_even_when_the_failover_fails_and_a_scheduler_error_does_not_mask_it()
        {
            var f = new Fx();
            f.Csu.EnableThrows = true;
            f.VC.FailReconfigure = (vm, c) => c.Action == DiskAction.Remove;
            var ex = Assert.Throws<InvalidOperationException>(() => f.Build().Run());
            Assert.Contains("vCenter refused Remove", ex.Message);
            Assert.True(f.Log.Has("Could not re-enable the CSU task"));
        }

        [Fact]
        public void Reservations_are_moved_with_the_disks_when_enabled_and_never_go_negative()
        {
            var f = new Fx();
            f.Store.Ag.ResCpuMHz = 5000; // more than VM1 has
            var text = "[VIM]\nVirtualMachineName=VM2\n[AG]\nJelena-AG01=AG01\nReservation=Y\nTaskName=CSU\nInstance=h\n";
            f.Settings = ServiceSettings.FromIni(IniDocument.Parse(text), "Jelena-AG01");
            f.Build().Run();
            Assert.Equal(0, f.VC.Vms["VM1"].CpuReservationMhz);
            Assert.Equal(6000, f.VC.Vms["VM2"].CpuReservationMhz);
            Assert.Equal(8192 - 2048, f.VC.Vms["VM1"].MemReservationMB);
            Assert.Equal(2048 + 2048, f.VC.Vms["VM2"].MemReservationMB);
        }

        [Fact]
        public void Reservations_untouched_when_disabled()
        {
            var f = new Fx();
            f.Build().Run();
            Assert.Null(f.VC.Changes[0].CpuReservationMhz);
            Assert.Equal(4000, f.VC.Vms["VM1"].CpuReservationMhz);
        }

        [Fact]
        public void Moved_vmdk_file_is_followed_when_the_uuid_confirms_the_disk()
        {
            var f = new Fx();
            f.VC.Disks["VM1"].Single(d => d.DeviceKey == "2016").FileName = "[DS2] SQLVM/data.vmdk";
            f.Build().Run();
            Assert.Equal("[DS2] SQLVM/data.vmdk", f.Store.Disks.Single(d => d.DeviceKey == "2016").FileName);
            Assert.Equal("[DS2] SQLVM/data.vmdk", f.VC.Changes.First(c => c.Action == DiskAction.Add).Disks.Single(d => d.DeviceKey == "2016").FileName);
        }

        [Fact]
        public void A_different_disk_using_the_same_key_is_not_mistaken_for_the_availability_group_disk()
        {
            var f = new Fx();
            f.VC.Disks["VM1"].Clear();
            f.VC.Attach("VM1", "2016", "[DS1] other/foreign.vmdk", "FOREIGN");
            f.VC.DatastoreFiles.Add(Data); f.VC.DatastoreFiles.Add(LogVmdk);
            var r = f.Build().Run();
            Assert.Equal(FailoverOutcome.Completed, r.Outcome);
            Assert.DoesNotContain(f.J.Events, e => e.StartsWith("remove"));
            Assert.Single(f.VC.Disks["VM1"]);
        }

        [Fact]
        public void Missing_configuration_is_reported_clearly()
        {
            var f = new Fx();
            f.Store.Disks.Clear();
            Assert.Contains("no hard disks", Assert.Throws<FailoverException>(() => f.Build().Run()).Message);
            var g = new Fx(); g.Store.Dbs.Clear();
            Assert.Contains("JDatabases", Assert.Throws<FailoverException>(() => g.Build().Run()).Message);
            var h = new Fx(); h.Store.Nodes.RemoveAll(n => n.VmName == "VM2");
            Assert.Contains("not listed in dbo.JNodes", Assert.Throws<FailoverException>(() => h.Build().Run()).Message);
        }
    }
}
