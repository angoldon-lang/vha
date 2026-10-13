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

    public enum FailoverOutcome { AlreadyOwner, Completed, FailbackRequired }

    public sealed class FailoverResult
    {
        public FailoverOutcome Outcome { get; set; }
        /// <summary>Node that must get the cluster group back (VM with snapshots holds the disks).</summary>
        public NodeInfo FailbackNode { get; set; }
    }
}
