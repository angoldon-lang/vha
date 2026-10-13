using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Core.Csu
{
    public interface ICsuStore
    {
        int GetAgId(string agName);
        void SetWorking(int agId, bool working);
        void EnsureSchema();
        /// <summary>Refreshes JDBFiles/JDBFileGroups for the online managed databases (own transaction).</summary>
        void DescribeDatabases(int agId);
        void BeginTransaction();
        void Commit();
        void Rollback();
        int GetLastScn(int agId);
        IReadOnlyList<NodeInfo> GetRemoteNodes(int agId);
        /// <summary>Replicates JAGroups, JDisks, JDatabases, JDBFileGroups and JDBFiles from the peer, inside the open transaction.</summary>
        void CopyFromPeer(NodeInfo peer, int agId);
        void WriteScn(int agId, int scn);
        IReadOnlyList<ConfiguredDisk> GetDisks(int agId);
        void ApplyDiskOperation(int agId, DiskOperation op);
        string GetOwner(int agId);
        void SetOwner(int agId, string owner);
        IReadOnlyList<ManagedDatabase> GetDatabases(int agId);
    }

    public interface IPeerProbe
    {
        PeerProbe Probe(NodeInfo node, int agId);
    }

    public interface IDiskInventory
    {
        DiskJoinResult Detect();
        /// <summary>Writes the VMware slot into disksign.ini of the disk.</summary>
        void StampSlot(DetectedDisk disk, string slot);
    }

    public sealed class LocalDatabase
    {
        public string Name { get; set; }
        public DbState State { get; set; }
        public IReadOnlyList<FileProbe> Files { get; set; }
    }

    public interface ILocalDatabases
    {
        /// <summary>Databases of the instance that are OFFLINE or RECOVERY_PENDING, with the existence of each file.</summary>
        IReadOnlyList<LocalDatabase> ListInactive();
        void Drop(string name);
    }

    public sealed class CsuResult
    {
        public bool Synced { get; set; }
        public int UnreachablePeers { get; set; }
        public bool DisksChanged { get; set; }
        public List<string> Dropped { get; } = new List<string>();
    }

    /// <summary>One run of the continuous update service (what Jelena's CsuCmd does every minute).</summary>
    public sealed class CsuCoordinator
    {
        private readonly string _vmName, _agName;
        private readonly ICsuStore _store;
        private readonly IPeerProbe _probe;
        private readonly IDiskInventory _inventory;
        private readonly ILocalDatabases _dbs;
        private readonly ILog _log;

        public CsuCoordinator(string vmName, string agName, ICsuStore store, IPeerProbe probe, IDiskInventory inventory, ILocalDatabases dbs, ILog log)
        {
            _vmName = vmName; _agName = agName; _store = store; _probe = probe; _inventory = inventory; _dbs = dbs; _log = log ?? NullLog.Instance;
        }

        public CsuResult Run()
        {
            var result = new CsuResult();
            var agId = _store.GetAgId(_agName);
            _store.SetWorking(agId, true);
            try
            {
                _store.EnsureSchema();
                _store.DescribeDatabases(agId);

                var agDisks = new List<DetectedDisk>();
                _store.BeginTransaction();
                try
                {
                    var local = _store.GetLastScn(agId);
                    var probes = _store.GetRemoteNodes(agId).Select(n => SafeProbe(n, agId)).ToList();
                    var decision = ScnSync.Decide(local, probes);
                    result.UnreachablePeers = decision.UnreachablePeers;
                    if (decision.UnreachablePeers > 0) _log.Warn(decision.UnreachablePeers + " peer node(s) not reachable, continuing with the local checks");
                    if (decision.CopyNeeded)
                    {
                        _log.Warn("Local SCN " + local + " is behind " + decision.Source.Node.VmName + " (" + decision.Source.Scn + "), replicating configuration");
                        _store.CopyFromPeer(decision.Source.Node, agId);
                        _store.WriteScn(agId, decision.Source.Scn);
                        local = decision.Source.Scn;
                        result.Synced = true;
                    }

                    var join = _inventory.Detect();
                    foreach (var u in join.Unmatched) _log.Debug("Windows disk " + u.Number + " is not a vCenter disk of this VM, ignored");
                    agDisks = join.Detected.Where(d => string.Equals(d.AgName, _agName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var d in agDisks) _inventory.StampSlot(d, ScsiSlot.FromDeviceKey(d.DeviceKey).ToString());

                    var rec = DiskReconciler.Reconcile(_store.GetDisks(agId), agDisks);
                    bool changed = false;
                    foreach (var op in rec.Operations.Where(o => o.Op != DiskOp.Unchanged))
                    {
                        _store.ApplyDiskOperation(agId, op);
                        changed = true;
                        _log.Info("JDisks " + op.Op + " " + op.DeviceKey);
                    }
                    if (rec.AllAttached)
                    {
                        var owner = _store.GetOwner(agId);
                        if (!string.Equals(owner, _vmName, StringComparison.OrdinalIgnoreCase))
                        {
                            _store.SetOwner(agId, _vmName);
                            changed = true;
                            _log.Warn("CurrentOwner was '" + owner + "', set to " + _vmName);
                        }
                    }
                    if (changed)
                    {
                        _store.WriteScn(agId, local + 1);
                        result.DisksChanged = true;
                    }
                    _store.Commit();
                }
                catch
                {
                    try { _store.Rollback(); } catch (Exception rex) { _log.Error("Rollback failed: " + rex.Message); }
                    throw;
                }

                CleanGhosts(agId, agDisks.Count > 0 || string.Equals(_store.GetOwner(agId), _vmName, StringComparison.OrdinalIgnoreCase), result);
            }
            finally
            {
                try { _store.SetWorking(agId, false); }
                catch (Exception ex) { _log.Error("Could not reset Is_CSU_Working: " + ex.Message); }
            }
            return result;
        }

        private PeerProbe SafeProbe(NodeInfo node, int agId)
        {
            try { return _probe.Probe(node, agId); }
            catch (Exception ex)
            {
                _log.Warn("Peer " + node.VmName + " not reachable: " + ex.Message);
                return new PeerProbe { Node = node, Reachable = false, Error = ex.Message };
            }
        }

        private void CleanGhosts(int agId, bool ownsDisks, CsuResult result)
        {
            var managed = _store.GetDatabases(agId).Select(d => d.Name).ToList();
            foreach (var db in _dbs.ListInactive())
            {
                var decision = GhostDropPolicy.Evaluate(db.Name, db.State, managed, db.Files, ownsDisks);
                if (decision.Verdict == DropVerdict.Drop)
                {
                    _log.Warn("Dropping database [" + db.Name + "]: " + decision.Reason);
                    _dbs.Drop(db.Name);
                    result.Dropped.Add(db.Name);
                }
                else if (managed.Contains(db.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _log.Warn("Database [" + db.Name + "] is " + db.State + " and is kept: " + decision.Reason);
                }
            }
        }
    }
}
