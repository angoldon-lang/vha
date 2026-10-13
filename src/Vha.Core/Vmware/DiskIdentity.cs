using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Model;

namespace Vha.Core.Vmware
{
    /// <summary>A disk seen from inside Windows.</summary>
    public sealed class WindowsDisk
    {
        public int Number { get; set; }
        public string SerialNumber { get; set; }
        /// <summary>Mount point / root path of the first volume, e.g. M:\isql\AG01\Data\ (null when none).</summary>
        public string MountPath { get; set; }
        public double SizeGB { get; set; }
    }

    /// <summary>A Windows disk joined with the vCenter disk that backs it.</summary>
    public sealed class DetectedDisk
    {
        public int Number { get; set; }
        public string MountPath { get; set; }
        /// <summary>Availability group declared in &lt;mount&gt;\jelena\disksign.ini (null when the file is missing).</summary>
        public string AgName { get; set; }
        public string UniqueId { get; set; }
        public string DeviceKey { get; set; }
        public string FileName { get; set; }
        public double CapacityGB { get; set; }
    }

    public sealed class DiskJoinResult
    {
        public List<DetectedDisk> Detected { get; } = new List<DetectedDisk>();
        public List<WindowsDisk> Unmatched { get; } = new List<WindowsDisk>();
    }

    public static class DiskIdentity
    {
        /// <summary>
        /// VMware exposes the backing UUID (disk.EnableUUID=TRUE) as the disk serial number:
        /// "6000C29b-6f1d-..." in vCenter and "6000C29B6F1D..." inside Windows.
        /// </summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        public static bool SameDisk(string a, string b)
        {
            var na = Normalize(a);
            return na.Length > 0 && na == Normalize(b);
        }

        /// <summary>
        /// Replaces the old SCSIPort arithmetic (which depended on the OS major version and on the position
        /// of the boot controller) with an exact match on the disk UUID.
        /// </summary>
        public static DiskJoinResult Join(IEnumerable<WindowsDisk> windowsDisks, IEnumerable<VmDisk> vmDisks)
        {
            var result = new DiskJoinResult();
            var vm = vmDisks.ToList();
            foreach (var w in windowsDisks)
            {
                var hit = vm.Where(v => SameDisk(v.Uuid, w.SerialNumber)).ToList();
                if (hit.Count > 1)
                    throw new InvalidOperationException("Windows disk " + w.Number + " (serial " + w.SerialNumber + ") matches more than one vCenter disk");
                if (hit.Count == 0) { result.Unmatched.Add(w); continue; }
                var d = hit[0];
                result.Detected.Add(new DetectedDisk
                {
                    Number = w.Number,
                    MountPath = w.MountPath,
                    UniqueId = (w.SerialNumber ?? "").Trim(),
                    DeviceKey = d.DeviceKey,
                    FileName = d.FileName,
                    CapacityGB = Math.Round(d.CapacityGB, 0)
                });
            }
            return result;
        }
    }
}
