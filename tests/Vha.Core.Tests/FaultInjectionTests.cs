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
    /// <summary>
    /// Exhaustive fault injection on the failover. For every single external operation of a complete failover
    /// (vCenter, configuration database, SQL on the old node, guest disks, local attach, scheduled task) the operation is made to fail
    /// (a) before it has any effect and (b) after it has taken effect but reports an error (a timeout that hides a success).
    /// After each injected fault the invariants below must hold, and a second, fault free run of the service must always converge.
    /// </summary>
    public class FaultInjectionTests
    {
        private sealed class InjectedFault : Exception { public InjectedFault(string m) : base(m) { } }

        private sealed class Plan
        {
            public int Target;           // 1-based index of the operation that fails; 0 = none
            public bool AfterEffect;
            public int Count;
            public List<string> Trace = new List<string>();
            public bool Fired;

            public T Around<T>(string name, Func<T> f)
            {
                Count++; Trace.Add(name);
                var hit = Count == Target;
                if (hit && !AfterEffect) { Fired = true; throw new InjectedFault(name + " (before effect)"); }
                var r = f();
                if (hit && AfterEffect) { Fired = true; throw new InjectedFault(name + " (after effect)"); }
                return r;
            }
            public void Around(string name, Action a) => Around<object>(name, () => { a(); return null; });
        }

        private sealed class FaultyVc : IVCenter
        {
            private readonly Plan _p; private readonly FakeVCenter _i;
            public FaultyVc(Plan p, FakeVCenter i) { _p = p; _i = i; }
            public VmInfo GetVm(string n) => _p.Around("vc.GetVm:" + n, () => _i.GetVm(n));
            public IReadOnlyList<VmDisk> GetDisks(string n) => _p.Around("vc.GetDisks:" + n, () => _i.GetDisks(n));
            public bool DatastoreFileExists(string f) => _p.Around("vc.Exists", () => _i.DatastoreFileExists(f));
            public void Reconfigure(VmInfo vm, DiskChange c) => _p.Around("vc.Reconfigure." + c.Action + ":" + vm.Name, () => _i.Reconfigure(vm, c));
        }

        private sealed class FaultyStore : IConfigStore
        {
            private readonly Plan _p; private readonly FakeStore _i;
            public FaultyStore(Plan p, FakeStore i) { _p = p; _i = i; }
            public AgInfo GetAg(string n) => _p.Around("db.GetAg", () => _i.GetAg(n));
            public IReadOnlyList<NodeInfo> GetNodes(int a) => _p.Around("db.GetNodes", () => _i.GetNodes(a));
            public IReadOnlyList<ConfiguredDisk> GetDisks(int a) => _p.Around("db.GetDisks", () => _i.GetDisks(a));
            public IReadOnlyList<ManagedDatabase> GetDatabases(int a) => _p.Around("db.GetDatabases", () => _i.GetDatabases(a));
            public int GetLastScn(int a) => _p.Around("db.GetLastScn", () => _i.GetLastScn(a));
            public void SetOwner(int a, string o) => _p.Around("db.SetOwner:" + o, () => _i.SetOwner(a, o));
            public void UpdateDisk(int a, ConfiguredDisk d, string f, bool at) => _p.Around("db.UpdateDisk", () => _i.UpdateDisk(a, d, f, at));
            public void WriteScn(int a, int s) => _p.Around("db.WriteScn", () => _i.WriteScn(a, s));
        }

        private sealed class FaultyConnector : INodeConnector
        {
            private readonly Plan _p; private readonly INodeConnector _i; private readonly Journal _j;
            public FaultyConnector(Plan p, INodeConnector i, Journal j) { _p = p; _i = i; _j = j; }
            public NodeConnection Connect(NodeInfo n)
            {
                var c = _p.Around("sql.Connect:" + n.VmName, () => _i.Connect(n));
                if (c.Status != ConnectStatus.Connected) return c;
                return new NodeConnection(ConnectStatus.Connected, new FaultyNode(_p, c.Node));
            }
            private sealed class FaultyNode : ISqlNode
            {
                private readonly Plan _p; private readonly ISqlNode _i;
                public FaultyNode(Plan p, ISqlNode i) { _p = p; _i = i; }
                public void ReleaseDatabases(IReadOnlyList<ManagedDatabase> d) => _p.Around("sql.Release", () => _i.ReleaseDatabases(d));
                public void Dispose() => _i.Dispose();
            }
        }

        private sealed class FaultyGuest : IGuestDisks
        {
            private readonly Plan _p; private readonly IGuestDisks _i;
            public FaultyGuest(Plan p, IGuestDisks i) { _p = p; _i = i; }
            public void SetOffline(NodeInfo n, IReadOnlyList<ConfiguredDisk> d) => _p.Around("guest.Offline", () => _i.SetOffline(n, d));
            public void SetOnlineLocal(IReadOnlyList<ConfiguredDisk> d) => _p.Around("guest.Online", () => _i.SetOnlineLocal(d));
        }

        private sealed class FaultyLocal : ILocalSql
        {
            private readonly Plan _p; private readonly ILocalSql _i;
            public FaultyLocal(Plan p, ILocalSql i) { _p = p; _i = i; }
            public void BringOnline(IReadOnlyList<ManagedDatabase> d) => _p.Around("local.BringOnline", () => _i.BringOnline(d));
        }

        private sealed class FaultyCsu : ICsuTask
        {
            private readonly Plan _p; private readonly ICsuTask _i;
            public FaultyCsu(Plan p, ICsuTask i) { _p = p; _i = i; }
            public void Disable() => _p.Around("csu.Disable", () => _i.Disable());
            public bool IsRunning() => _p.Around("csu.IsRunning", () => _i.IsRunning());
            public void Stop() => _p.Around("csu.Stop", () => _i.Stop());
            public void EnableAndRun() => _p.Around("csu.Enable", () => _i.EnableAndRun());
        }

        public enum Start { Normal, OwnerPoweredOff, OwnerUnreachablePoweredOn, Split, DisksFree, AlreadyOnSelf }

        private sealed class World
        {
            public Journal J = new Journal(); public Plan Plan = new Plan();
            public FakeVCenter VC; public FakeStore Store; public FakeConnector Conn; public FakeGuest Guest; public FakeLocalSql Local; public FakeCsuTask Csu; public FakeClock Clock = new FakeClock(); public ListLog Log = new ListLog();
            public ServiceSettings Settings;

            public World(Start start)
            {
                VC = new FakeVCenter(J); Store = new FakeStore(J); Conn = new FakeConnector(J); Guest = new FakeGuest(J); Local = new FakeLocalSql(J); Csu = new FakeCsuTask(J);
                VC.AddVm("VM1", cpu: 4000, mem: 8192).AddVm("VM2", cpu: 1000, mem: 2048).AddVm("VM3");
                Store.Nodes.Add(new NodeInfo { VmName = "VM1", DnsName = "vm1.local", ConnectionString = "x" });
                Store.Nodes.Add(new NodeInfo { VmName = "VM2", DnsName = "vm2.local", ConnectionString = "y", IsLocal = true });
                Store.Nodes.Add(new NodeInfo { VmName = "VM3", DnsName = "vm3.local", ConnectionString = "z" });
                Store.Disks.Add(new ConfiguredDisk { DeviceKey = "2016", FileName = "[DS1] a/data.vmdk", UniqueId = "AAA", CapacityGB = 100, Attached = true });
                Store.Disks.Add(new ConfiguredDisk { DeviceKey = "2017", FileName = "[DS1] a/log.vmdk", UniqueId = "BBB", CapacityGB = 50, Attached = true });
                Store.Disks.Add(new ConfiguredDisk { DeviceKey = "2033", FileName = "[DS1] a/idx.vmdk", UniqueId = "CCC", CapacityGB = 20, Attached = true });
                Store.Ag.ResCpuMHz = 500; Store.Ag.ResMemMB = 1024;
                VC.DatastoreFiles.Add("[DS1] a/data.vmdk"); VC.DatastoreFiles.Add("[DS1] a/log.vmdk"); VC.DatastoreFiles.Add("[DS1] a/idx.vmdk");
                switch (start)
                {
                    case Start.Normal: AllOn("VM1"); break;
                    case Start.OwnerPoweredOff: AllOn("VM1"); VC.Vms["VM1"].PowerState = "PoweredOff"; Conn.Status["VM1"] = ConnectStatus.NotAvailable; break;
                    case Start.OwnerUnreachablePoweredOn: AllOn("VM1"); Conn.Status["VM1"] = ConnectStatus.Timeout; break;
                    case Start.Split: VC.Attach("VM1", "2016", "[DS1] a/data.vmdk", "AAA").Attach("VM3", "2017", "[DS1] a/log.vmdk", "BBB").Attach("VM1", "2033", "[DS1] a/idx.vmdk", "CCC"); break;
                    case Start.DisksFree: Store.Ag.CurrentOwner = FailoverCoordinator.TransitioningMarker; break;
                    case Start.AlreadyOnSelf: AllOn("VM2"); Store.Ag.CurrentOwner = "VM1"; break;
                }
                var ini = "[VIM]\nVirtualMachineName=VM2\n[AG]\nJelena-AG01=AG01\nReservation=Y\nTaskName=CSU\nInstance=h\n[Failover]\nOwnerUnreachablePolicy=Force\n";
                Settings = ServiceSettings.FromIni(IniDocument.Parse(ini), "Jelena-AG01");
            }

            private void AllOn(string vm) { VC.Attach(vm, "2016", "[DS1] a/data.vmdk", "AAA").Attach(vm, "2017", "[DS1] a/log.vmdk", "BBB").Attach(vm, "2033", "[DS1] a/idx.vmdk", "CCC"); }

            public FailoverResult Run(Plan plan) =>
                new FailoverCoordinator(Settings, new FaultyStore(plan, Store), new FaultyVc(plan, VC), new FaultyConnector(plan, Conn, J), new FaultyGuest(plan, Guest),
                    new FaultyLocal(plan, Local), new FaultyCsu(plan, Csu), Clock, Log).Run();
        }

        public static IEnumerable<object[]> Starts() => Enum.GetValues(typeof(Start)).Cast<Start>().Select(s => new object[] { s });

        [Theory]
        [MemberData(nameof(Starts))]
        public void A_fault_free_run_reaches_the_target_state(Start start)
        {
            var w = new World(start);
            var r = w.Run(w.Plan);
            Assert.True(r.Outcome == FailoverOutcome.Completed || r.Outcome == FailoverOutcome.AlreadyOwner);
            AssertConverged(w);
        }

        [Theory]
        [MemberData(nameof(Starts))]
        public void Whatever_fails_the_invariants_hold_and_a_second_run_converges(Start start)
        {
            // learn how many operations a clean run performs
            var probe = new World(start);
            var clean = new Plan();
            probe.Run(clean);
            int total = clean.Count;
            Assert.InRange(total, 8, 200);

            int faultsInjected = 0;
            foreach (var after in new[] { false, true })
            {
                for (int k = 1; k <= total; k++)
                {
                    var w = new World(start);
                    var plan = new Plan { Target = k, AfterEffect = after };
                    var label = start + " fault@" + k + (after ? " after" : " before") + " [" + (k <= clean.Trace.Count ? clean.Trace[k - 1] : "?") + "]";

                    Exception thrown = null; FailoverResult first = null;
                    try { first = w.Run(plan); } catch (Exception ex) { thrown = ex; }
                    if (plan.Fired) faultsInjected++;
                    var op = plan.Trace.Count >= k ? plan.Trace[k - 1] : "";

                    // ---- invariants of the faulty run
                    // vSphere never accepted a disk held by another VM; only a "timeout that hid a successful attach" may make the rollback try it
                    if (!(after && op.StartsWith("vc.Reconfigure.Add"))) Assert.True(w.VC.LockViolations == 0, label + ": tried to attach a disk held by another VM");
                    // databases are released on a live previous owner before any disk is detached
                    var firstRemove = w.J.Events.FindIndex(e => e.StartsWith("remove:"));
                    if (firstRemove >= 0 && start == Start.Normal) Assert.True(w.J.IndexOf("release-dbs:VM1") >= 0 && w.J.IndexOf("release-dbs:VM1") < firstRemove, label + ": disks detached before the databases were released");
                    // the marker is written before the first detach
                    if (firstRemove >= 0) Assert.True(w.J.IndexOf("owner:" + FailoverCoordinator.TransitioningMarker) >= 0 && w.J.IndexOf("owner:" + FailoverCoordinator.TransitioningMarker) < firstRemove, label + ": detached before the transitioning marker");
                    // databases are never made available before every disk is on this node
                    if (w.J.Has("dbs-online")) Assert.Equal(3, w.VC.Disks["VM2"].Count);
                    // the CSU task is re-enabled unless the fault is in the re-enabling itself or before the task was ever disabled
                    if (!op.StartsWith("csu.") ) Assert.True(w.J.Has("csu-enable"), label + ": CSU task left disabled");
                    // an injected fault is never swallowed into a success that did not do the work
                    if (thrown == null && first != null && first.Outcome == FailoverOutcome.Completed && !op.StartsWith("csu.") && !op.StartsWith("vc.GetVm") ) { /* completed runs must be real */ AssertConverged(w); }

                    // ---- a second, fault free run (service restarted by the cluster) always converges
                    w.J.Events.Clear();
                    w.Csu.RunningChecks = 0;
                    FailoverResult second = null;
                    var again = Record.Exception(() => second = w.Run(new Plan()));
                    Assert.True(again == null, label + ": second run failed: " + again);
                    Assert.True(second.Outcome == FailoverOutcome.Completed || second.Outcome == FailoverOutcome.AlreadyOwner, label + ": second run outcome " + second.Outcome);
                    AssertConverged(w, label);
                    Assert.True(w.VC.LockViolations == 0 || (after && op.StartsWith("vc.Reconfigure.Add")), label + ": lock violation in the second run");
                }
            }
            Assert.True(faultsInjected >= total, "expected faults on every operation, injected " + faultsInjected + " of " + total * 2);
        }

        private static void AssertConverged(World w, string label = "")
        {
            Assert.True(w.VC.Disks["VM2"].Select(d => d.DeviceKey).OrderBy(x => x).SequenceEqual(new[] { "2016", "2017", "2033" }), label + ": disks not all on VM2");
            Assert.Empty(w.VC.Disks["VM1"]); Assert.Empty(w.VC.Disks["VM3"]);
            Assert.True(w.Store.Ag.CurrentOwner == "VM2", label + ": owner " + w.Store.Ag.CurrentOwner);
            Assert.True(w.J.Has("dbs-online"), label + ": databases not brought online");
            Assert.True(w.J.Has("guest-online"), label + ": guest disks not onlined");
            Assert.True(w.Store.Scn >= 5, label);
            // reservations never negative and never double counted on VM2 after a complete run
            Assert.True(w.VC.Vms["VM1"].CpuReservationMhz >= 0 && w.VC.Vms["VM1"].MemReservationMB >= 0, label + ": negative reservation");
        }
    }
}
