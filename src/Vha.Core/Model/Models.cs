using System;
using System.Collections.Generic;

namespace Vha.Core.Model
{
    public sealed class AgInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string CurrentOwner { get; set; }
        public int ResCpuMHz { get; set; }
        public int ResMemMB { get; set; }
    }

    public sealed class NodeInfo
    {
        public string VmName { get; set; }
        public string DnsName { get; set; }
        public string ConnectionString { get; set; }
        public bool IsLocal { get; set; }
    }

    /// <summary>A row of dbo.JDisks.</summary>
    public sealed class ConfiguredDisk
    {
        public string FileName { get; set; }
        public string DeviceKey { get; set; }
        public string UniqueId { get; set; }
        public double CapacityGB { get; set; }
        public bool Attached { get; set; }
    }

    /// <summary>A row of dbo.JDatabases.</summary>
    public sealed class ManagedDatabase
    {
        public string Name { get; set; }
        public string PrimaryFile { get; set; }
    }

    /// <summary>A row of dbo.JDBFiles.</summary>
    public sealed class DatabaseFile
    {
        public int FileId { get; set; }
        public string LogicalName { get; set; }
        public string PhysicalName { get; set; }
        public int FileGroupId { get; set; }
    }

    /// <summary>A virtual disk as reported by vCenter.</summary>
    public sealed class VmDisk
    {
        /// <summary>PowerCLI id, e.g. VirtualMachine-vm-772/2050.</summary>
        public string Id { get; set; }
        public string FileName { get; set; }
        public double CapacityGB { get; set; }
        /// <summary>Backing UUID (needs disk.EnableUUID=TRUE on the VM).</summary>
        public string Uuid { get; set; }

        /// <summary>Part of Id after the last '/'.</summary>
        public string DeviceKey
        {
            get
            {
                if (string.IsNullOrEmpty(Id)) return "";
                var i = Id.LastIndexOf('/');
                return i < 0 ? "" : Id.Substring(i + 1).Trim();
            }
        }
    }

    public sealed class VmInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string PowerState { get; set; }
        public int SnapshotCount { get; set; }
        public int CpuReservationMhz { get; set; }
        public int MemReservationMB { get; set; }
        public bool IsPoweredOff => string.Equals(PowerState, "PoweredOff", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>sys.databases.state</summary>
    public enum DbState
    {
        Online = 0, Restoring = 1, Recovering = 2, RecoveryPending = 3,
        Suspect = 4, Emergency = 5, Offline = 6, Copying = 7, OfflineSecondary = 10
    }
}
