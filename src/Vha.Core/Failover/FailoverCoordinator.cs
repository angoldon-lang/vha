using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Config;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Core.Failover
{
    /// <summary>
    /// Moves the VMDKs of an availability group to this node and brings its databases online.
    ///
    /// Differences from the original service (see docs/ANALISI.md):
    ///  * the CSU task is really waited for (the old by-value flag never worked) and stopped if stuck;
    ///  * the real position of the disks is read from vCenter, CurrentOwner is only informative, so a crash in the
    ///    middle of a failover is recovered automatically at the next start;
    ///  * the "##transitioning##" marker is written BEFORE any disk is detached, and the failover is aborted if it
    ///    cannot be written;
    ///  * the policy for an owner that does not answer is explicit (Force/Abort) and a powered off owner is detected;
    ///  * a failed attach is rolled back to the previous holder;
    ///  * SCN/owner write errors are no longer swallowed.
    /// </summary>
    public sealed class FailoverCoordinator
    {
        public const string TransitioningMarker = "##transitioning##";

        private readonly ServiceSettings _s;
        private readonly IConfigStore _store;
        private readonly IVCenter _vc;
        private readonly INodeConnector _connector;
        private readonly IGuestDisks _guest;
        private readonly ILocalSql _local;
        private readonly ICsuTask _csu;
        private readonly IClock _clock;
        private readonly ILog _log;

        public FailoverCoordinator(ServiceSettings settings, IConfigStore store, IVCenter vc, INodeConnector connector,
            IGuestDisks guest, ILocalSql local, ICsuTask csu, IClock clock, ILog log)
        {
            _s = settings; _store = store; _vc = vc; _connector = connector; _guest = guest; _local = local; _csu = csu; _clock = clock; _log = log ?? NullLog.Instance;
        }

        public FailoverResult Run()
        {
            _log.Info("Failover procedure started for AG " + _s.AgName + " on " + _s.VirtualMachineName);
            _csu.Disable();
            try
            {
                WaitForCsuIdle();
                return Execute();
            }
            finally
            {
                try { _csu.EnableAndRun(); _log.Info("CSU task enabled"); }
                catch (Exception ex) { _log.Error("Could not re-enable the CSU task " + _s.TaskName + ": " + ex.Message); }
            }
        }

        private void WaitForCsuIdle()
        {
            var deadline = _clock.UtcNow + _s.CsuWaitTimeout;
            while (_csu.IsRunning())
            {
                if (_clock.UtcNow >= deadline)
                {
                    _log.Warn("CSU task still running after " + _s.CsuWaitTimeout.TotalSeconds + "s, stopping it");
                    _csu.Stop();
                    for (int i = 0; i < 10 && _csu.IsRunning(); i++) _clock.Sleep(TimeSpan.FromSeconds(1));
                    if (_csu.IsRunning()) _log.Warn("CSU task did not stop, continuing anyway (its drop policy is restricted to ghost databases)");
                    return;
                }
                _log.Info("CSU task is running, waiting 2 seconds");
                _clock.Sleep(TimeSpan.FromSeconds(2));
            }
        }

        private FailoverResult Execute()
        {
            var self = _s.VirtualMachineName;
            var ag = _store.GetAg(_s.AgName) ?? throw new FailoverException("Availability group '" + _s.AgName + "' is not present in dbo.JAGroups");
            var nodes = _store.GetNodes(ag.Id).ToList();
            var disks = _store.GetDisks(ag.Id).ToList();
            var dbs = _store.GetDatabases(ag.Id).ToList();
            if (disks.Count == 0) throw new FailoverException("There are no hard disks defined in the database for availability group " + ag.Name);
            if (dbs.Count == 0) throw new FailoverException("No database defined in dbo.JDatabases for availability group " + ag.Name);
            if (nodes.All(n => !string.Equals(n.VmName, self, StringComparison.OrdinalIgnoreCase)))
                throw new FailoverException("This virtual machine (" + self + ") is not listed in dbo.JNodes for " + ag.Name);

            // 1. where are the disks, really?
            var snapshot = ReadHolders(nodes, disks);
            var onSelf = snapshot.Held.Where(h => Same(h.Vm, self)).ToList();
            var onOthers = snapshot.Held.Where(h => !Same(h.Vm, self)).ToList();
            var free = snapshot.Free;

            if (onSelf.Count == disks.Count)
            {
                _log.Info("All hard disks are already attached to this virtual machine");
                if (!Same(ag.CurrentOwner, self))
                {
                    _log.Warn("CurrentOwner is '" + ag.CurrentOwner + "', correcting it");
                    PublishOwner(ag, self);
                }
                _guest.SetOnlineLocal(disks);
                _local.BringOnline(dbs);
                return new FailoverResult { Outcome = FailoverOutcome.AlreadyOwner };
            }

            // 2. free disks must exist in the datastore
            foreach (var d in free)
            {
                if (!_vc.DatastoreFileExists(d.FileName))
                    throw new FailoverException("VMDK " + d.FileName + " is not attached to any node and was not found in the datastore");
            }

            // 3. snapshots on a holder make the hot remove impossible: send the group back
            foreach (var group in onOthers.GroupBy(h => h.Vm, StringComparer.OrdinalIgnoreCase))
            {
                var vm = _vc.GetVm(group.Key);
                if (vm.SnapshotCount > 0)
                {
                    var node = nodes.First(n => Same(n.VmName, group.Key));
                    _log.Warn("Virtual machine " + vm.Name + " holds disks of this AG and has " + vm.SnapshotCount + " snapshot(s): the group will fail back to " + node.DnsName);
                    return new FailoverResult { Outcome = FailoverOutcome.FailbackRequired, FailbackNode = node };
                }
            }

            // 4. release the databases and the disks on the nodes that hold them (nothing is detached yet)
            foreach (var group in onOthers.GroupBy(h => h.Vm, StringComparer.OrdinalIgnoreCase))
                ReleaseOnHolder(nodes.First(n => Same(n.VmName, group.Key)), group.Select(g => g.Disk).ToList(), dbs);

            // 5. intent record: if it cannot be written nothing has been changed yet
            try { _store.SetOwner(ag.Id, TransitioningMarker); }
            catch (Exception ex) { throw new FailoverException("Cannot mark the availability group as transitioning, failover aborted before touching any disk: " + ex.Message, ex); }

            // 6. detach from every holder
            var detached = new List<KeyValuePair<string, List<ConfiguredDisk>>>();
            foreach (var group in onOthers.GroupBy(h => h.Vm, StringComparer.OrdinalIgnoreCase))
            {
                var toRemove = group.Select(g => WithActualFile(g.Disk, g.Actual)).ToList();
                var vm = _vc.GetVm(group.Key);
                _log.Info("Detaching " + toRemove.Count + " disk(s) from " + vm.Name);
                _vc.Reconfigure(vm, new DiskChange
                {
                    Action = DiskAction.Remove,
                    Disks = toRemove,
                    CpuReservationMhz = _s.ReservationEnabled ? Math.Max(vm.CpuReservationMhz - ag.ResCpuMHz, 0) : (int?)null,
                    MemReservationMB = _s.ReservationEnabled ? Math.Max(vm.MemReservationMB - ag.ResMemMB, 0) : (int?)null
                });
                detached.Add(new KeyValuePair<string, List<ConfiguredDisk>>(group.Key, toRemove));
            }

            // 7. attach to this node (rollback on failure)
            var toAdd = disks.Where(d => onSelf.All(h => h.Disk.DeviceKey != d.DeviceKey))
                             .Select(d => { var h = onOthers.FirstOrDefault(x => x.Disk.DeviceKey == d.DeviceKey); return h == null ? d : WithActualFile(d, h.Actual); })
                             .ToList();
            var me = _vc.GetVm(self);
            try
            {
                _log.Info("Attaching " + toAdd.Count + " disk(s) to " + me.Name);
                _vc.Reconfigure(me, new DiskChange
                {
                    Action = DiskAction.Add,
                    Disks = toAdd,
                    CpuReservationMhz = _s.ReservationEnabled ? me.CpuReservationMhz + ag.ResCpuMHz : (int?)null,
                    MemReservationMB = _s.ReservationEnabled ? me.MemReservationMB + ag.ResMemMB : (int?)null
                });
                VerifyAttached(self, disks);
            }
            catch (Exception ex)
            {
                _log.Error("Attach failed: " + ex.Message);
                RollbackToHolders(detached, ag);
                throw new FailoverException("Attach of the disks to " + self + " failed; the disks were returned to their previous holder when possible: " + ex.Message, ex);
            }

            // 8. persist the new reality (errors are fatal now)
            var attachedNow = _vc.GetDisks(self);
            foreach (var d in disks)
            {
                var actual = attachedNow.First(v => v.DeviceKey == d.DeviceKey);
                _store.UpdateDisk(ag.Id, d, actual.FileName, true);
            }
            PublishOwner(ag, self);

            // 9. make everything available
            _guest.SetOnlineLocal(disks);
            _local.BringOnline(dbs);
            _log.Info("Failover completed");
            return new FailoverResult { Outcome = FailoverOutcome.Completed };
        }

        private void PublishOwner(AgInfo ag, string owner)
        {
            _store.SetOwner(ag.Id, owner);
            var next = _store.GetLastScn(ag.Id) + 1;
            _store.WriteScn(ag.Id, next);
            _log.Info("Owner set to " + owner + ", new SCN " + next);
        }

        private void VerifyAttached(string vmName, IEnumerable<ConfiguredDisk> disks)
        {
            var now = _vc.GetDisks(vmName);
            var missing = disks.Where(d => now.All(v => v.DeviceKey != d.DeviceKey)).Select(d => d.DeviceKey).ToList();
            if (missing.Count > 0) throw new FailoverException("After the reconfiguration the devices " + string.Join(",", missing) + " are not attached to " + vmName);
        }

        private void RollbackToHolders(List<KeyValuePair<string, List<ConfiguredDisk>>> detached, AgInfo ag)
        {
            foreach (var kv in detached)
            {
                try
                {
                    var vm = _vc.GetVm(kv.Key);
                    _vc.Reconfigure(vm, new DiskChange
                    {
                        Action = DiskAction.Add,
                        Disks = kv.Value,
                        CpuReservationMhz = _s.ReservationEnabled ? vm.CpuReservationMhz + ag.ResCpuMHz : (int?)null,
                        MemReservationMB = _s.ReservationEnabled ? vm.MemReservationMB + ag.ResMemMB : (int?)null
                    });
                    _log.Warn("Disks re-attached to " + kv.Key);
                }
                catch (Exception ex)
                {
                    _log.Error("ROLLBACK FAILED for " + kv.Key + ": " + ex.Message + ". Disks are detached; the next start of the service will re-attach them.");
                }
            }
        }

        private void ReleaseOnHolder(NodeInfo node, IReadOnlyList<ConfiguredDisk> heldDisks, IReadOnlyList<ManagedDatabase> dbs)
        {
            var vm = _vc.GetVm(node.VmName);
            if (vm.IsPoweredOff)
            {
                _log.Info("Virtual machine " + vm.Name + " is powered off: nothing to release");
                return;
            }
            using (var conn = _connector.Connect(node))
            {
                switch (conn.Status)
                {
                    case ConnectStatus.Connected:
                        conn.Node.ReleaseDatabases(dbs);
                        _guest.SetOffline(node, heldDisks);
                        break;
                    default:
                        var why = conn.Status == ConnectStatus.Timeout ? "timed out" : "not available";
                        if (_s.UnreachablePolicy == OwnerUnreachablePolicy.Abort)
                            throw new FailoverException("SQL Server on " + node.VmName + " is " + why + " while the VM is powered on (" + vm.PowerState + "); OwnerUnreachablePolicy=Abort, failover aborted. " + conn.Detail);
                        _log.Warn("SQL Server on " + node.VmName + " is " + why + " while the VM is powered on; OwnerUnreachablePolicy=Force, the disks will be detached anyway. " + conn.Detail);
                        break;
                }
            }
        }

        private sealed class Holder { public string Vm; public ConfiguredDisk Disk; public VmDisk Actual; }
        private sealed class HolderSnapshot { public List<Holder> Held = new List<Holder>(); public List<ConfiguredDisk> Free = new List<ConfiguredDisk>(); }

        private HolderSnapshot ReadHolders(IEnumerable<NodeInfo> nodes, IReadOnlyList<ConfiguredDisk> disks)
        {
            var perVm = new Dictionary<string, IReadOnlyList<VmDisk>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes.Select(x => x.VmName).Distinct(StringComparer.OrdinalIgnoreCase))
                perVm[n] = _vc.GetDisks(n);

            var snap = new HolderSnapshot();
            foreach (var d in disks)
            {
                var found = new List<Holder>();
                foreach (var kv in perVm)
                {
                    DiskMatch m;
                    try { m = DiskMatcher.Find(d, kv.Value); }
                    catch (InvalidOperationException ex) { throw new FailoverException("Disk " + d.DeviceKey + " on " + kv.Key + ": " + ex.Message, ex); }
                    if (m.Kind != MatchKind.None) found.Add(new Holder { Vm = kv.Key, Disk = d, Actual = m.Actual });
                }
                if (found.Count > 1)
                    throw new FailoverException("Disk " + d.DeviceKey + " (" + d.FileName + ") appears attached to more than one node (" + string.Join(", ", found.Select(f => f.Vm)) + "); manual intervention required");
                if (found.Count == 1) snap.Held.Add(found[0]); else snap.Free.Add(d);
            }
            return snap;
        }

        private static ConfiguredDisk WithActualFile(ConfiguredDisk d, VmDisk actual)
        {
            return new ConfiguredDisk { DeviceKey = d.DeviceKey, UniqueId = d.UniqueId, CapacityGB = d.CapacityGB, Attached = d.Attached, FileName = actual?.FileName ?? d.FileName };
        }

        private static bool Same(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
