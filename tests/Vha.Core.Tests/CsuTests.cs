using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Csu;
using Vha.Core.Failover;
using Vha.Core.Model;
using Vha.Core.Vmware;
using Xunit;

namespace Vha.Core.Tests
{
    public class ScnSyncTests
    {
        private static PeerProbe P(string n, bool ok, int scn) => new PeerProbe { Node = new NodeInfo { VmName = n }, Reachable = ok, Scn = scn };

        [Fact]
        public void Highest_reachable_peer_ahead_of_local_wins()
        {
            var d = ScnSync.Decide(5, new[] { P("a", true, 7), P("b", true, 9), P("c", true, 6) });
            Assert.True(d.CopyNeeded);
            Assert.Equal("b", d.Source.Node.VmName);
        }

        [Fact]
        public void Equal_or_lower_peers_mean_no_copy()
        {
            Assert.False(ScnSync.Decide(5, new[] { P("a", true, 5), P("b", true, 2) }).CopyNeeded);
        }

        [Fact]
        public void Unreachable_peers_are_skipped_not_fatal()
        {
            var d = ScnSync.Decide(5, new[] { P("a", false, 99), P("b", true, 6) });
            Assert.Equal("b", d.Source.Node.VmName);
            Assert.Equal(1, d.UnreachablePeers);
            Assert.Equal(1, d.ReachablePeers);
        }

        [Fact]
        public void No_peers_or_all_down_is_a_valid_situation()
        {
            Assert.False(ScnSync.Decide(0, new PeerProbe[0]).CopyNeeded);
            var d = ScnSync.Decide(3, new[] { P("a", false, 0), P("b", false, 0) });
            Assert.False(d.CopyNeeded);
            Assert.Equal(2, d.UnreachablePeers);
        }
    }

    public class TableSyncPlannerTests
    {
        [Fact]
        public void Jagroups_is_updated_without_id_and_local_flag()
        {
            var sql = TableSyncPlanner.BuildWrite("JAGroups", new[] { "ID", "Name", "CurrentOwner", "Is_CSU_Working", "ResCpuMHz" }, out var cols);
            Assert.Equal(new[] { "Name", "CurrentOwner", "ResCpuMHz" }, cols);
            Assert.Equal("UPDATE [dbo].[JAGroups] SET [Name] = @p0, [CurrentOwner] = @p1, [ResCpuMHz] = @p2 WHERE [ID] = @agId", sql);
        }

        [Fact]
        public void Jdisks_insert_skips_the_node_local_attached_column()
        {
            var sql = TableSyncPlanner.BuildWrite("JDisks", new[] { "JAGroupsID", "FileName", "Attached", "DeviceKey" }, out var cols);
            Assert.Equal(new[] { "JAGroupsID", "FileName", "DeviceKey" }, cols);
            Assert.Equal("INSERT [dbo].[JDisks] ([JAGroupsID], [FileName], [DeviceKey]) VALUES (@p0, @p1, @p2)", sql);
        }

        [Fact]
        public void Columns_with_odd_names_are_quoted()
        {
            var sql = TableSyncPlanner.BuildWrite("JDBFiles", new[] { "JAGroupsID", "we]ird" }, out _);
            Assert.Contains("[we]]ird]", sql);
        }

        [Theory]
        [InlineData("JNodes")] [InlineData("sys.objects")] [InlineData("JDisks; DROP TABLE x")]
        public void Only_the_replicated_tables_are_accepted(string table)
        {
            Assert.Throws<ArgumentException>(() => TableSyncPlanner.BuildSelect(table));
            Assert.Throws<ArgumentException>(() => TableSyncPlanner.BuildWrite(table, new[] { "a" }, out _));
        }

        [Fact]
        public void Select_and_delete_are_filtered_by_ag_and_jagroups_is_never_deleted()
        {
            Assert.Equal("SELECT * FROM [dbo].[JAGroups] WHERE [ID] = @agId", TableSyncPlanner.BuildSelect("JAGroups"));
            Assert.Equal("SELECT * FROM [dbo].[JDisks] WHERE [JAGroupsID] = @agId", TableSyncPlanner.BuildSelect("JDisks"));
            Assert.Equal("DELETE FROM [dbo].[JDisks] WHERE [JAGroupsID] = @agId", TableSyncPlanner.BuildDelete("JDisks"));
            Assert.Throws<InvalidOperationException>(() => TableSyncPlanner.BuildDelete("JAGroups"));
        }
    }

    public class GhostDropPolicyTests
    {
        private static FileProbe F(string p, bool e) => new FileProbe { Path = p, Exists = e };
        private static readonly string[] Managed = { "Sales", "Dummy01" };

        [Fact]
        public void Ghost_with_all_files_missing_on_a_node_that_does_not_own_the_disks_is_dropped()
        {
            var d = GhostDropPolicy.Evaluate("Sales", DbState.Offline, Managed, new[] { F("a.mdf", false), F("a.ldf", false) }, false);
            Assert.Equal(DropVerdict.Drop, d.Verdict);
        }

        [Fact]
        public void Lost_log_with_surviving_mdf_is_never_dropped()
        {
            // the scenario that destroyed data with the original code
            var d = GhostDropPolicy.Evaluate("Sales", DbState.RecoveryPending, Managed, new[] { F("a.mdf", true), F("a.ldf", false) }, false);
            Assert.Equal(DropVerdict.Keep, d.Verdict);
            Assert.Contains("a.mdf", d.Reason);
        }

        [Fact]
        public void The_owner_node_never_drops_it_must_recover()
        {
            var d = GhostDropPolicy.Evaluate("Sales", DbState.RecoveryPending, Managed, new[] { F("a.mdf", false), F("a.ldf", false) }, true);
            Assert.Equal(DropVerdict.Keep, d.Verdict);
        }

        [Theory]
        [InlineData("master")] [InlineData("model")] [InlineData("msdb")] [InlineData("tempdb")] [InlineData("Jelena")] [InlineData("JELENA")] [InlineData("distribution")]
        public void System_and_configuration_databases_are_protected(string name)
        {
            var managed = new[] { name };
            Assert.Equal(DropVerdict.Keep, GhostDropPolicy.Evaluate(name, DbState.Offline, managed, new[] { F("x", false) }, false).Verdict);
        }

        [Fact]
        public void Databases_outside_this_availability_group_are_not_touched()
        {
            var d = GhostDropPolicy.Evaluate("LocalToolsDb", DbState.Offline, Managed, new[] { F("d:\\x.mdf", false) }, false);
            Assert.Equal(DropVerdict.Keep, d.Verdict);
        }

        [Theory]
        [InlineData(DbState.Online)] [InlineData(DbState.Suspect)] [InlineData(DbState.Emergency)] [InlineData(DbState.Restoring)]
        public void Only_offline_and_recovery_pending_are_candidates(DbState state) =>
            Assert.Equal(DropVerdict.Keep, GhostDropPolicy.Evaluate("Sales", state, Managed, new[] { F("x", false) }, false).Verdict);

        [Fact]
        public void Name_comparison_ignores_case_and_unknown_file_lists_are_kept()
        {
            Assert.Equal(DropVerdict.Drop, GhostDropPolicy.Evaluate("SALES", DbState.Offline, Managed, new[] { F("x", false) }, false).Verdict);
            Assert.Equal(DropVerdict.Keep, GhostDropPolicy.Evaluate("Sales", DbState.Offline, Managed, new FileProbe[0], false).Verdict);
            Assert.Equal(DropVerdict.Keep, GhostDropPolicy.Evaluate("Sales", DbState.Offline, Managed, null, false).Verdict);
        }
    }

    public class DiskReconcilerTests
    {
        private static ConfiguredDisk C(string k, string f, bool att, double gb = 100) => new ConfiguredDisk { DeviceKey = k, FileName = f, Attached = att, CapacityGB = gb };
        private static DetectedDisk D(string k, string f, double gb = 100) => new DetectedDisk { DeviceKey = k, FileName = f, CapacityGB = gb, UniqueId = "U" + k };

        [Fact]
        public void Identical_state_changes_nothing()
        {
            var r = DiskReconciler.Reconcile(new[] { C("2016", "a", true), C("2017", "b", true) }, new[] { D("2016", "a"), D("2017", "b") });
            Assert.False(r.Changed);
            Assert.True(r.AllAttached);
        }

        [Fact]
        public void New_moved_and_missing_disks_are_classified()
        {
            var r = DiskReconciler.Reconcile(
                new[] { C("2016", "old", true), C("2017", "b", true), C("2018", "c", false) },
                new[] { D("2016", "new"), D("2019", "z") });
            Assert.Equal(DiskOp.Update, r.Operations.Single(o => o.DeviceKey == "2016").Op);
            Assert.Equal(DiskOp.Insert, r.Operations.Single(o => o.DeviceKey == "2019").Op);
            Assert.Equal(DiskOp.MarkDetached, r.Operations.Single(o => o.DeviceKey == "2017").Op);
            Assert.Equal(DiskOp.Unchanged, r.Operations.Single(o => o.DeviceKey == "2018").Op);   // already detached
            Assert.False(r.AllAttached);
            Assert.True(r.Changed);
        }

        [Fact]
        public void A_row_flagged_detached_but_present_is_updated_to_attached()
        {
            var r = DiskReconciler.Reconcile(new[] { C("2016", "a", false) }, new[] { D("2016", "a") });
            Assert.Equal(DiskOp.Update, r.Operations.Single().Op);
            Assert.True(r.AllAttached);
        }

        [Fact]
        public void Capacity_change_is_detected()
        {
            var r = DiskReconciler.Reconcile(new[] { C("2016", "a", true, 100) }, new[] { D("2016", "a", 200) });
            Assert.Equal(DiskOp.Update, r.Operations.Single().Op);
        }

        [Fact]
        public void Nothing_configured_is_never_AllAttached_and_duplicates_are_refused()
        {
            Assert.False(DiskReconciler.Reconcile(new ConfiguredDisk[0], new DetectedDisk[0]).AllAttached);
            Assert.Throws<InvalidOperationException>(() => DiskReconciler.Reconcile(new ConfiguredDisk[0], new[] { D("2016", "a"), D("2016", "b") }));
        }
    }

    public class DbOnlinePlannerTests
    {
        [Fact]
        public void Missing_database_is_attached_only_if_described()
        {
            Assert.Equal(DbAction.Attach, DbOnlinePlanner.Plan(false, null, true).Action);
            Assert.Equal(DbAction.Fail, DbOnlinePlanner.Plan(false, null, false).Action);
        }

        [Fact]
        public void Offline_database_is_set_online_the_old_code_left_it_offline()
        {
            Assert.Equal(DbAction.SetOnline, DbOnlinePlanner.Plan(true, DbState.Offline, true).Action);
            Assert.Equal(DbAction.SetOnline, DbOnlinePlanner.Plan(true, DbState.RecoveryPending, true).Action);
        }

        [Theory]
        [InlineData(DbState.Online, DbAction.None)]
        [InlineData(DbState.Recovering, DbAction.None)]
        [InlineData(DbState.Restoring, DbAction.None)]
        [InlineData(DbState.Suspect, DbAction.Fail)]
        [InlineData(DbState.Emergency, DbAction.Fail)]
        public void Other_states(DbState state, DbAction expected) => Assert.Equal(expected, DbOnlinePlanner.Plan(true, state, true).Action);
    }

    public class CsuCoordinatorTests
    {
        private sealed class Store : ICsuStore
        {
            public List<string> Calls = new List<string>();
            public int Scn = 5; public string Owner = "VM1";
            public List<NodeInfo> Remote = new List<NodeInfo>();
            public List<ConfiguredDisk> Disks = new List<ConfiguredDisk>();
            public List<ManagedDatabase> Dbs = new List<ManagedDatabase> { new ManagedDatabase { Name = "Sales" } };
            public bool FailCopy, FailSetWorkingFalse;
            public int GetAgId(string n) => 1;
            public void SetWorking(int a, bool w) { Calls.Add("working:" + w); if (!w && FailSetWorkingFalse) throw new Exception("db down"); }
            public void EnsureSchema() => Calls.Add("schema");
            public void DescribeDatabases(int a) => Calls.Add("describe");
            public void BeginTransaction() => Calls.Add("begin");
            public void Commit() => Calls.Add("commit");
            public void Rollback() => Calls.Add("rollback");
            public int GetLastScn(int a) => Scn;
            public IReadOnlyList<NodeInfo> GetRemoteNodes(int a) => Remote;
            public void CopyFromPeer(NodeInfo p, int a) { if (FailCopy) throw new Exception("copy failed"); Calls.Add("copy:" + p.VmName); }
            public void WriteScn(int a, int s) { Calls.Add("scn:" + s); Scn = s; }
            public IReadOnlyList<ConfiguredDisk> GetDisks(int a) => Disks;
            public void ApplyDiskOperation(int a, DiskOperation op) => Calls.Add("disk:" + op.Op + ":" + op.DeviceKey);
            public string GetOwner(int a) => Owner;
            public void SetOwner(int a, string o) { Calls.Add("owner:" + o); Owner = o; }
            public IReadOnlyList<ManagedDatabase> GetDatabases(int a) => Dbs;
        }

        private sealed class ProbeStub : IPeerProbe
        {
            public Dictionary<string, PeerProbe> Map = new Dictionary<string, PeerProbe>();
            public HashSet<string> Throw = new HashSet<string>();
            public PeerProbe Probe(NodeInfo n, int a) { if (Throw.Contains(n.VmName)) throw new Exception("connect timeout"); return Map[n.VmName]; }
        }

        private sealed class Inventory : IDiskInventory
        {
            public DiskJoinResult Result = new DiskJoinResult();
            public List<string> Stamped = new List<string>();
            public bool Throw;
            public DiskJoinResult Detect() { if (Throw) throw new Exception("vcenter down"); return Result; }
            public void StampSlot(DetectedDisk d, string slot) => Stamped.Add(d.DeviceKey + "=" + slot);
        }

        private sealed class Dbs : ILocalDatabases
        {
            public List<LocalDatabase> Inactive = new List<LocalDatabase>();
            public List<string> Dropped = new List<string>();
            public IReadOnlyList<LocalDatabase> ListInactive() => Inactive;
            public void Drop(string n) => Dropped.Add(n);
        }

        private static DetectedDisk Det(string key, string ag = "AG01") => new DetectedDisk { DeviceKey = key, AgName = ag, FileName = "f" + key, CapacityGB = 100, UniqueId = "U" + key };

        private (CsuCoordinator c, Store s, ProbeStub p, Inventory i, Dbs d, ListLog l) Build()
        {
            var s = new Store(); var p = new ProbeStub(); var i = new Inventory(); var d = new Dbs(); var l = new ListLog();
            return (new CsuCoordinator("VM2", "AG01", s, p, i, d, l), s, p, i, d, l);
        }

        [Fact]
        public void Peer_ahead_is_replicated_inside_the_transaction()
        {
            var x = Build();
            x.s.Remote.Add(new NodeInfo { VmName = "VM1" });
            x.p.Map["VM1"] = new PeerProbe { Node = x.s.Remote[0], Reachable = true, Scn = 9 };
            var r = x.c.Run();
            Assert.True(r.Synced);
            var calls = x.s.Calls;
            Assert.True(calls.IndexOf("begin") < calls.IndexOf("copy:VM1"));
            Assert.True(calls.IndexOf("copy:VM1") < calls.IndexOf("scn:9"));
            Assert.True(calls.IndexOf("scn:9") < calls.IndexOf("commit"));
            Assert.Equal("working:True", calls.First());
            Assert.Equal("working:False", calls.Last());
        }

        [Fact]
        public void Peer_down_does_not_abort_the_cycle_the_disk_inventory_still_runs()
        {
            var x = Build();
            x.s.Remote.Add(new NodeInfo { VmName = "VM1" });
            x.p.Throw.Add("VM1");
            x.i.Result.Detected.Add(Det("2016"));
            x.s.Disks.Add(new ConfiguredDisk { DeviceKey = "2016", FileName = "f2016", CapacityGB = 100, Attached = false });
            var r = x.c.Run();
            Assert.Equal(1, r.UnreachablePeers);
            Assert.Contains("disk:Update:2016", x.s.Calls);
            Assert.Contains("owner:VM2", x.s.Calls);
            Assert.Contains("commit", x.s.Calls);
            Assert.Contains("scn:6", x.s.Calls);
        }

        [Fact]
        public void Disks_of_other_availability_groups_are_ignored_and_slots_are_stamped_for_this_one()
        {
            var x = Build();
            x.i.Result.Detected.Add(Det("2016"));
            x.i.Result.Detected.Add(Det("2032", "AG02"));
            x.s.Disks.Add(new ConfiguredDisk { DeviceKey = "2016", FileName = "f2016", CapacityGB = 100, Attached = true });
            x.c.Run();
            Assert.Equal(new[] { "2016=1:0" }, x.i.Stamped);
            Assert.DoesNotContain(x.s.Calls, c => c.Contains("2032"));
        }

        [Fact]
        public void Nothing_changed_writes_no_scn()
        {
            var x = Build();
            x.s.Owner = "VM2";
            x.i.Result.Detected.Add(Det("2016"));
            x.s.Disks.Add(new ConfiguredDisk { DeviceKey = "2016", FileName = "f2016", CapacityGB = 100, Attached = true });
            x.c.Run();
            Assert.DoesNotContain(x.s.Calls, c => c.StartsWith("scn") || c.StartsWith("owner") || c.StartsWith("disk"));
            Assert.Contains("commit", x.s.Calls);
        }

        [Fact]
        public void Failure_rolls_back_and_still_clears_the_working_flag()
        {
            var x = Build();
            x.i.Throw = true;
            Assert.Throws<Exception>(() => x.c.Run());
            Assert.Contains("rollback", x.s.Calls);
            Assert.DoesNotContain("commit", x.s.Calls);
            Assert.Equal("working:False", x.s.Calls.Last());
        }

        [Fact]
        public void A_failing_working_flag_reset_does_not_mask_the_real_result()
        {
            var x = Build();
            x.s.FailSetWorkingFalse = true;
            x.c.Run();
            Assert.True(x.l.Has("Could not reset Is_CSU_Working"));
        }

        [Fact]
        public void Failing_copy_rolls_back()
        {
            var x = Build();
            x.s.Remote.Add(new NodeInfo { VmName = "VM1" });
            x.p.Map["VM1"] = new PeerProbe { Node = x.s.Remote[0], Reachable = true, Scn = 9 };
            x.s.FailCopy = true;
            Assert.Throws<Exception>(() => x.c.Run());
            Assert.Contains("rollback", x.s.Calls);
        }

        [Fact]
        public void Ghost_of_a_managed_database_is_dropped_on_the_passive_node_only()
        {
            var x = Build();
            x.s.Owner = "VM1";
            x.d.Inactive.Add(new LocalDatabase { Name = "Sales", State = DbState.Offline, Files = new[] { new FileProbe { Path = "m:\\a.mdf", Exists = false } } });
            x.d.Inactive.Add(new LocalDatabase { Name = "Unrelated", State = DbState.Offline, Files = new[] { new FileProbe { Path = "d:\\u.mdf", Exists = false } } });
            var r = x.c.Run();
            Assert.Equal(new[] { "Sales" }, x.d.Dropped);
            Assert.Equal(new[] { "Sales" }, r.Dropped);
        }

        [Fact]
        public void The_owner_never_drops_even_when_files_are_missing()
        {
            var x = Build();
            x.s.Owner = "VM2";
            x.d.Inactive.Add(new LocalDatabase { Name = "Sales", State = DbState.RecoveryPending, Files = new[] { new FileProbe { Path = "m:\\a.mdf", Exists = false } } });
            x.c.Run();
            Assert.Empty(x.d.Dropped);
            Assert.True(x.l.Has("is RecoveryPending and is kept"));
        }

        [Fact]
        public void A_node_that_just_detected_the_disks_counts_as_owner_even_before_ownership_is_published()
        {
            var x = Build();
            x.s.Owner = "VM1";
            x.i.Result.Detected.Add(Det("2016"));
            x.s.Disks.Add(new ConfiguredDisk { DeviceKey = "2016", FileName = "f2016", CapacityGB = 100, Attached = true });
            x.s.Disks.Add(new ConfiguredDisk { DeviceKey = "2017", FileName = "f2017", CapacityGB = 100, Attached = true });   // second disk not (yet) visible
            x.d.Inactive.Add(new LocalDatabase { Name = "Sales", State = DbState.Offline, Files = new[] { new FileProbe { Path = "m:\\a.mdf", Exists = false } } });
            x.c.Run();
            Assert.Empty(x.d.Dropped);
        }
    }
}
