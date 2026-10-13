using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Core.Setup
{
    public sealed class DiscoveredDisk
    {
        public string HolderVm { get; set; }
        public ConfiguredDisk Disk { get; set; }
    }

    /// <summary>Finds the disks of an availability group in vCenter (replaces the Excel sheet / MatchHD_UniqueID_DeviceKey.ps1).</summary>
    public static class DiskDiscovery
    {
        public static bool Matches(string filter, string fileName)
        {
            var rx = "^" + Regex.Escape(filter.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(fileName ?? "", rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <param name="disksByVm">Disks of every node VM as reported by vCenter.</param>
        /// <param name="thisVm">The node the setup runs on: disks attached to it are stored as Attached.</param>
        public static List<DiscoveredDisk> Discover(string filter, IDictionary<string, IReadOnlyList<VmDisk>> disksByVm, string thisVm)
        {
            if (string.IsNullOrWhiteSpace(filter)) throw new ArgumentException("The disk filter is empty", nameof(filter));
            var found = new List<DiscoveredDisk>();
            foreach (var kv in disksByVm)
                foreach (var d in kv.Value.Where(x => Matches(filter, x.FileName)))
                {
                    if (!ScsiSlot.TryFromDeviceKey(d.DeviceKey, out _))
                        throw new InvalidOperationException("Disk '" + d.FileName + "' on " + kv.Key + " has device key " + d.DeviceKey + ": only SCSI disks (keys 2000-2063, unit 7 excluded) are supported");
                    if (string.IsNullOrWhiteSpace(DiskIdentity.Normalize(d.Uuid)))
                        throw new InvalidOperationException("Disk '" + d.FileName + "' on " + kv.Key + " has no UUID: set disk.EnableUUID=TRUE on the virtual machine, power it off and on again");
                    found.Add(new DiscoveredDisk
                    {
                        HolderVm = kv.Key,
                        Disk = new ConfiguredDisk
                        {
                            DeviceKey = d.DeviceKey, FileName = d.FileName, CapacityGB = Math.Round(d.CapacityGB, 0),
                            UniqueId = DiskIdentity.Normalize(d.Uuid), Attached = string.Equals(kv.Key, thisVm, StringComparison.OrdinalIgnoreCase)
                        }
                    });
                }
            if (found.Count == 0) throw new InvalidOperationException("No disk matches the filter '" + filter + "' on any node");
            foreach (var g in found.GroupBy(f => f.Disk.DeviceKey).Where(g => g.Count() > 1))
                throw new InvalidOperationException("Device key " + g.Key + " is used by more than one matching disk (" + string.Join(", ", g.Select(x => x.Disk.FileName)) + "): refine the filter");
            foreach (var g in found.GroupBy(f => f.Disk.FileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                throw new InvalidOperationException("The disk '" + g.Key + "' appears attached to more than one VM (" + string.Join(", ", g.Select(x => x.HolderVm)) + ")");
            return found.OrderBy(f => int.Parse(f.Disk.DeviceKey)).ToList();
        }
    }
}
