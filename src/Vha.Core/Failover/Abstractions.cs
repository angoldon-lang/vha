using System;
using System.Collections.Generic;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Core.Failover
{
    public interface IVCenter
    {
        VmInfo GetVm(string name);
        IReadOnlyList<VmDisk> GetDisks(string vmName);
        bool DatastoreFileExists(string fileName);
        void Reconfigure(VmInfo vm, DiskChange change);
    }

    /// <summary>The Jelena configuration database of the local instance.</summary>
    public interface IConfigStore
    {
        AgInfo GetAg(string name);
        IReadOnlyList<NodeInfo> GetNodes(int agId);
        IReadOnlyList<ConfiguredDisk> GetDisks(int agId);
        IReadOnlyList<ManagedDatabase> GetDatabases(int agId);
        int GetLastScn(int agId);
        /// <summary>Must throw when the AG row is not updated.</summary>
        void SetOwner(int agId, string owner);
        void UpdateDisk(int agId, ConfiguredDisk disk, string fileName, bool attached);
        /// <summary>Must throw when the row is not inserted.</summary>
        void WriteScn(int agId, int scn);
    }

    public enum ConnectStatus { Connected, NotAvailable, Timeout }

    public sealed class NodeConnection : IDisposable
    {
        public ConnectStatus Status { get; }
        public ISqlNode Node { get; }
        public string Detail { get; }
        public NodeConnection(ConnectStatus status, ISqlNode node, string detail = null) { Status = status; Node = node; Detail = detail; }
        public void Dispose() { Node?.Dispose(); }
    }

    public interface INodeConnector
    {
        NodeConnection Connect(NodeInfo node);
    }

    /// <summary>SQL instance of a node that has to release the databases.</summary>
    public interface ISqlNode : IDisposable
    {
        void ReleaseDatabases(IReadOnlyList<ManagedDatabase> databases);
    }

    public interface IGuestDisks
    {
        void SetOffline(NodeInfo node, IReadOnlyList<ConfiguredDisk> disks);
        void SetOnlineLocal(IReadOnlyList<ConfiguredDisk> disks);
        /// <summary>Configured disks that Windows on THIS machine does not see (by serial / unique id). Used when vCenter is unavailable.</summary>
        IReadOnlyList<ConfiguredDisk> MissingLocally(IReadOnlyList<ConfiguredDisk> disks);
    }

    /// <summary>Local instance: makes the databases available once the disks are in place.</summary>
    public interface ILocalSql
    {
        void BringOnline(IReadOnlyList<ManagedDatabase> databases);
    }

    /// <summary>The scheduled task that runs the continuous update (CSU).</summary>
    public interface ICsuTask
    {
        void Disable();
        bool IsRunning();
        void Stop();
        void EnableAndRun();
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
        void Sleep(TimeSpan time);
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public void Sleep(TimeSpan time) => System.Threading.Thread.Sleep(time);
    }

    public sealed class FailoverException : Exception
    {
        public FailoverException(string message) : base(message) { }
        public FailoverException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>The node answered the connection but did not complete a command in time (hung instance, frozen VM): same meaning as an unreachable node for the policy.</summary>
    public sealed class NodeUnresponsiveException : Exception
    {
        public NodeUnresponsiveException(string message, Exception inner = null) : base(message, inner) { }
    }

    public enum FailoverOutcome { AlreadyOwner, Completed, FailbackRequired, StartedWithoutVCenter }

    public sealed class FailoverResult
    {
        public FailoverOutcome Outcome { get; set; }
        /// <summary>Node that must get the cluster group back (VM with snapshots holds the disks).</summary>
        public NodeInfo FailbackNode { get; set; }
    }

    /// <summary>Raised by <see cref="UnavailableVCenter"/>: vCenter could not be reached.</summary>
    public sealed class VCenterUnavailableException : Exception
    {
        public VCenterUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Stands in for vCenter when the connection failed: every call throws, so the coordinator falls back to local evidence (if enabled) before changing anything.</summary>
    public sealed class UnavailableVCenter : IVCenter
    {
        private readonly Exception _cause;
        public UnavailableVCenter(Exception cause) { _cause = cause; }
        private Exception Err() => new VCenterUnavailableException("vCenter is not available: " + (_cause?.Message ?? "unknown"), _cause);
        public VmInfo GetVm(string name) { throw Err(); }
        public IReadOnlyList<VmDisk> GetDisks(string vmName) { throw Err(); }
        public bool DatastoreFileExists(string fileName) { throw Err(); }
        public void Reconfigure(VmInfo vm, DiskChange change) { throw Err(); }
    }
}
