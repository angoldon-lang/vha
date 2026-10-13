using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Core.Csu
{
    public enum DiskOp { Insert, Update, MarkDetached, Unchanged }

    public sealed class DiskOperation
    {
        public DiskOp Op { get; set; }
        public string DeviceKey { get; set; }
        public string FileName { get; set; }
        public double CapacityGB { get; set; }
        public string UniqueId { get; set; }
    }

    public sealed class DiskReconcileResult
    {
        public List<DiskOperation> Operations { get; } = new List<DiskOperation>();
        public bool Changed => Operations.Any(o => o.Op != DiskOp.Unchanged);
        /// <summary>Every disk of the AG is attached to this VM.</summary>
        public bool AllAttached { get; set; }
    }

    /// <summary>Compares dbo.JDisks with the disks really attached to this VM (what CompareDataTables did).</summary>
    public static class DiskReconciler
    {
        public static DiskReconcileResult Reconcile(IReadOnlyList<ConfiguredDisk> configured, IReadOnlyList<DetectedDisk> detected)
        {
            var r = new DiskReconcileResult();
            var seen = new HashSet<string>();
            foreach (var d in detected)
            {
                if (!seen.Add(d.DeviceKey)) throw new InvalidOperationException("Device key " + d.DeviceKey + " detected twice");
                var c = configured.FirstOrDefault(x => x.DeviceKey == d.DeviceKey);
                var op = new DiskOperation { DeviceKey = d.DeviceKey, FileName = d.FileName, CapacityGB = d.CapacityGB, UniqueId = d.UniqueId };
                if (c == null) op.Op = DiskOp.Insert;
                else if (!c.Attached || !string.Equals(c.FileName, d.FileName, StringComparison.Ordinal) || Math.Abs(c.CapacityGB - d.CapacityGB) > 0.001)
                    op.Op = DiskOp.Update;
                else op.Op = DiskOp.Unchanged;
                r.Operations.Add(op);
            }
            foreach (var c in configured)
            {
                if (seen.Contains(c.DeviceKey)) continue;
                r.Operations.Add(new DiskOperation { DeviceKey = c.DeviceKey, FileName = c.FileName, CapacityGB = c.CapacityGB, UniqueId = c.UniqueId, Op = c.Attached ? DiskOp.MarkDetached : DiskOp.Unchanged });
            }
            r.AllAttached = configured.Count > 0 && configured.All(c => seen.Contains(c.DeviceKey));
            return r;
        }
    }
}
