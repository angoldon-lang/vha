using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Csu;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Core.Tests
{
    public sealed class ListLog : ILog
    {
        public List<string> Lines = new List<string>();
        public void Debug(string m) => Lines.Add("DEBUG " + m);
        public void Info(string m) => Lines.Add("INFO " + m);
        public void Warn(string m) => Lines.Add("WARN " + m);
        public void Error(string m) => Lines.Add("ERROR " + m);
        public bool Has(string text) => Lines.Any(l => l.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>Records every side effect in one ordered list, so tests can assert on the order of operations.</summary>
    public sealed class Journal
    {
        public List<string> Events = new List<string>();
        public void Add(string e) => Events.Add(e);
        public int IndexOf(string e) => Events.FindIndex(x => x == e);
        public bool Has(string e) => IndexOf(e) >= 0;
    }

    public sealed class FakeClock : IClock
    {
        public DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => Now;
        public void Sleep(TimeSpan t) => Now += t;
    }

    public sealed class FakeVCenter : IVCenter
    {
        private readonly Journal _j;
        public Dictionary<string, VmInfo> Vms = new Dictionary<string, VmInfo>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<VmDisk>> Disks = new Dictionary<string, List<VmDisk>>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DatastoreFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Func<string, DiskChange, bool> FailReconfigure = (vm, c) => false;
        public List<DiskChange> Changes = new List<DiskChange>();
        public bool SilentlyIgnoreAdd;
        /// <summary>Attempts to attach a disk that another VM still holds (vSphere refuses these with a lock error).</summary>
        public int LockViolations;
        /// <summary>Test hook: an Add attaches only the first disk and then reports an error (a partial result).</summary>
        public bool PartialAdd;

        public FakeVCenter(Journal j) { _j = j; }

        public FakeVCenter AddVm(string name, string power = "PoweredOn", int snaps = 0, int cpu = 0, int mem = 0)
        {
            Vms[name] = new VmInfo { Id = "VirtualMachine-" + name, Name = name, PowerState = power, SnapshotCount = snaps, CpuReservationMhz = cpu, MemReservationMB = mem };
            Disks[name] = new List<VmDisk>();
            return this;
        }

        public FakeVCenter Attach(string vm, string key, string file, string uuid = null)
        {
            Disks[vm].Add(new VmDisk { Id = "VirtualMachine-" + vm + "/" + key, FileName = file, CapacityGB = 100, Uuid = uuid });
            return this;
        }

        public VmInfo GetVm(string name) { if (!Vms.ContainsKey(name)) throw new InvalidOperationException("VM not found " + name); return Vms[name]; }
        public IReadOnlyList<VmDisk> GetDisks(string vmName) => Disks[vmName].ToList();
        public bool DatastoreFileExists(string fileName) => DatastoreFiles.Contains(fileName);

        public void Reconfigure(VmInfo vm, DiskChange change)
        {
            _j.Add(change.Action.ToString().ToLower() + ":" + vm.Name + ":" + string.Join(",", change.Disks.Select(d => d.DeviceKey)));
            Changes.Add(change);
            if (FailReconfigure(vm.Name, change)) throw new InvalidOperationException("vCenter refused " + change.Action + " on " + vm.Name);
            if (change.Action == DiskAction.Remove)
                Disks[vm.Name].RemoveAll(d => change.Disks.Any(c => c.DeviceKey == d.DeviceKey));
            else if (!SilentlyIgnoreAdd)
            {
                foreach (var c in change.Disks)
                {
                    var holder = Disks.FirstOrDefault(kv => kv.Key != vm.Name && kv.Value.Any(d => d.DeviceKey == c.DeviceKey && d.FileName == c.FileName));
                    if (holder.Key != null) { LockViolations++; throw new InvalidOperationException("vSphere: file " + c.FileName + " is locked by " + holder.Key); }
                }
                if (PartialAdd && change.Disks.Count > 1)
                {
                    Attach(vm.Name, change.Disks[0].DeviceKey, change.Disks[0].FileName);
                    throw new InvalidOperationException("vCenter: connection lost after the first device");
                }
                foreach (var c in change.Disks) Attach(vm.Name, c.DeviceKey, c.FileName);
            }
            if (change.CpuReservationMhz.HasValue) vm.CpuReservationMhz = change.CpuReservationMhz.Value;
            if (change.MemReservationMB.HasValue) vm.MemReservationMB = change.MemReservationMB.Value;
        }
    }

    public sealed class FakeStore : IConfigStore
    {
        private readonly Journal _j;
        public AgInfo Ag = new AgInfo { Id = 1, Name = "AG01", CurrentOwner = "VM1", ResCpuMHz = 1000, ResMemMB = 2048 };
        public List<NodeInfo> Nodes = new List<NodeInfo>();
        public List<ConfiguredDisk> Disks = new List<ConfiguredDisk>();
        public List<ManagedDatabase> Dbs = new List<ManagedDatabase> { new ManagedDatabase { Name = "Dummy01" }, new ManagedDatabase { Name = "Sales" } };
        public int Scn = 5;
        public bool FailSetOwnerTransitioning, FailWriteScn;

        public FakeStore(Journal j) { _j = j; }
        public AgInfo GetAg(string name) => Ag.Name == name ? Ag : null;
        public IReadOnlyList<NodeInfo> GetNodes(int agId) => Nodes;
        public IReadOnlyList<ConfiguredDisk> GetDisks(int agId) => Disks;
        public IReadOnlyList<ManagedDatabase> GetDatabases(int agId) => Dbs;
        public int GetLastScn(int agId) => Scn;
        public void SetOwner(int agId, string owner)
        {
            if (FailSetOwnerTransitioning && owner == FailoverCoordinator.TransitioningMarker) throw new InvalidOperationException("sql down");
            _j.Add("owner:" + owner); Ag.CurrentOwner = owner;
        }
        public void UpdateDisk(int agId, ConfiguredDisk disk, string fileName, bool attached) { _j.Add("jdisk:" + disk.DeviceKey); disk.FileName = fileName; disk.Attached = attached; }
        public void WriteScn(int agId, int scn)
        {
            if (FailWriteScn) throw new InvalidOperationException("scn insert failed");
            _j.Add("scn:" + scn); Scn = scn;
        }
    }

    public sealed class FakeConnector : INodeConnector
    {
        private readonly Journal _j;
        public Dictionary<string, ConnectStatus> Status = new Dictionary<string, ConnectStatus>(StringComparer.OrdinalIgnoreCase);
        public bool ReleaseThrows;
        public FakeConnector(Journal j) { _j = j; }

        public NodeConnection Connect(NodeInfo node)
        {
            var st = Status.TryGetValue(node.VmName, out var s) ? s : ConnectStatus.Connected;
            return st == ConnectStatus.Connected ? new NodeConnection(st, new FakeSqlNode(_j, node.VmName, ReleaseThrows)) : new NodeConnection(st, null, "fake");
        }

        private sealed class FakeSqlNode : ISqlNode
        {
            private readonly Journal _j; private readonly string _n; private readonly bool _throw;
            public FakeSqlNode(Journal j, string n, bool t) { _j = j; _n = n; _throw = t; }
            public void ReleaseDatabases(IReadOnlyList<ManagedDatabase> d) { if (_throw) throw new InvalidOperationException("detach failed"); _j.Add("release-dbs:" + _n); }
            public void Dispose() { }
        }
    }

    public sealed class FakeGuest : IGuestDisks
    {
        private readonly Journal _j;
        public FakeGuest(Journal j) { _j = j; }
        public void SetOffline(NodeInfo node, IReadOnlyList<ConfiguredDisk> disks) => _j.Add("guest-offline:" + node.VmName);
        public void SetOnlineLocal(IReadOnlyList<ConfiguredDisk> disks) => _j.Add("guest-online");
    }

    public sealed class FakeLocalSql : ILocalSql
    {
        private readonly Journal _j;
        public FakeLocalSql(Journal j) { _j = j; }
        public void BringOnline(IReadOnlyList<ManagedDatabase> databases) => _j.Add("dbs-online");
    }

    public sealed class FakeCsuTask : ICsuTask
    {
        private readonly Journal _j;
        public int RunningChecks;      // IsRunning returns true this many times
        public bool NeverStops;
        public bool EnableThrows;
        public FakeCsuTask(Journal j) { _j = j; }
        public void Disable() => _j.Add("csu-disable");
        public bool IsRunning() { if (NeverStops) return true; if (RunningChecks > 0) { RunningChecks--; return true; } return false; }
        public void Stop() { _j.Add("csu-stop"); if (!NeverStops) RunningChecks = 0; }
        public void EnableAndRun() { if (EnableThrows) throw new InvalidOperationException("scheduler gone"); _j.Add("csu-enable"); }
    }
}
