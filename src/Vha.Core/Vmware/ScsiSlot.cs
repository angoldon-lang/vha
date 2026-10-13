using System;
using System.Globalization;

namespace Vha.Core.Vmware
{
    /// <summary>
    /// A virtual SCSI position. vSphere device keys for SCSI disks are 2000 + 16 * controller + unit
    /// (controller 0..3 -> controllerKey 1000..1003, unit 0..15, unit 7 is the controller itself).
    /// </summary>
    public readonly struct ScsiSlot : IEquatable<ScsiSlot>
    {
        public const int FirstDiskKey = 2000;
        public const int FirstControllerKey = 1000;

        public int Controller { get; }
        public int Unit { get; }

        public ScsiSlot(int controller, int unit)
        {
            if (controller < 0 || controller > 3) throw new ArgumentOutOfRangeException(nameof(controller), "SCSI controller must be 0..3");
            if (unit < 0 || unit > 15 || unit == 7) throw new ArgumentOutOfRangeException(nameof(unit), "SCSI unit must be 0..15 and not 7");
            Controller = controller;
            Unit = unit;
        }

        public int DeviceKey => FirstDiskKey + 16 * Controller + Unit;
        public int ControllerKey => FirstControllerKey + Controller;

        public static bool TryFromDeviceKey(int key, out ScsiSlot slot)
        {
            slot = default;
            var offset = key - FirstDiskKey;
            if (offset < 0 || offset >= 64) return false;
            var controller = offset / 16;
            var unit = offset % 16;
            if (unit == 7) return false;
            slot = new ScsiSlot(controller, unit);
            return true;
        }

        public static bool TryFromDeviceKey(string key, out ScsiSlot slot)
        {
            slot = default;
            return int.TryParse((key ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var k) && TryFromDeviceKey(k, out slot);
        }

        /// <summary>Parses a device key or throws a descriptive exception (NVMe/SATA keys are not supported).</summary>
        public static ScsiSlot FromDeviceKey(string key)
        {
            if (!TryFromDeviceKey(key, out var slot))
                throw new NotSupportedException("DeviceKey '" + key + "' is not a SCSI disk key (2000-2063, unit 7 excluded). NVMe and SATA virtual disks are not supported.");
            return slot;
        }

        public override string ToString() => Controller + ":" + Unit;
        public bool Equals(ScsiSlot other) => Controller == other.Controller && Unit == other.Unit;
        public override bool Equals(object obj) => obj is ScsiSlot s && Equals(s);
        public override int GetHashCode() => Controller * 16 + Unit;
    }
}
