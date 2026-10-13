using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Model;

namespace Vha.Core.Vmware
{
    public enum MatchKind { None, Exact, MovedFile }

    public sealed class DiskMatch
    {
        public ConfiguredDisk Configured { get; set; }
        public VmDisk Actual { get; set; }
        public MatchKind Kind { get; set; }
    }

    public static class DiskMatcher
    {
        /// <summary>
        /// Finds the vCenter disk that corresponds to a configured disk.
        /// The device key is compared as a whole token (the old code used LIKE '%key%' on the whole id,
        /// which also matched the VM moref, e.g. VirtualMachine-vm-2050/2034 for key 2050).
        /// A key match with a different file name is accepted only when the disk UUID confirms it is the same
        /// disk (e.g. after a Storage vMotion); otherwise it is a different disk using the same slot.
        /// </summary>
        public static DiskMatch Find(ConfiguredDisk configured, IEnumerable<VmDisk> vmDisks)
        {
            var key = (configured.DeviceKey ?? "").Trim();
            var candidates = vmDisks.Where(d => string.Equals(d.DeviceKey, key, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0) return new DiskMatch { Configured = configured, Kind = MatchKind.None };
            if (candidates.Count > 1) throw new InvalidOperationException("More than one disk with device key " + key + " on the same virtual machine");

            var actual = candidates[0];
            if (string.Equals(actual.FileName, configured.FileName, StringComparison.OrdinalIgnoreCase))
                return new DiskMatch { Configured = configured, Actual = actual, Kind = MatchKind.Exact };

            if (!string.IsNullOrWhiteSpace(configured.UniqueId) && !string.IsNullOrWhiteSpace(actual.Uuid)
                && DiskIdentity.SameDisk(configured.UniqueId, actual.Uuid))
                return new DiskMatch { Configured = configured, Actual = actual, Kind = MatchKind.MovedFile };

            if (!string.IsNullOrWhiteSpace(configured.UniqueId) && !string.IsNullOrWhiteSpace(actual.Uuid))
                return new DiskMatch { Configured = configured, Actual = actual, Kind = MatchKind.None };

            // No UUID available on either side: same slot, different file name -> cannot prove identity.
            throw new InvalidOperationException("Device key " + key + " is used by '" + actual.FileName + "' but JDisks says '" + configured.FileName
                + "' and no disk UUID is available to decide whether it is the same disk (enable disk.EnableUUID).");
        }
    }
}
