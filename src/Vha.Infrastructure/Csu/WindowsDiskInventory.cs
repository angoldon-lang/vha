using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vha.Core.Config;
using Vha.Core.Csu;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Vmware;
using Vha.Infrastructure.PowerShell;

namespace Vha.Infrastructure.Csu
{
    /// <summary>
    /// Joins the Windows disks with the vCenter disks of this VM by disk UUID. It replaces diskext64.exe
    /// (text parsing, first-row-is-C: assumption) and the SCSIPort arithmetic with the "+3" for Windows 10+.
    /// </summary>
    public sealed class WindowsDiskInventory : IDiskInventory
    {
        private readonly PowerShellHost _ps; private readonly IVCenter _vc; private readonly string _vmName; private readonly ILog _log;

        public WindowsDiskInventory(PowerShellHost ps, IVCenter vc, string vmName, ILog log) { _ps = ps; _vc = vc; _vmName = vmName; _log = log; }

        public DiskJoinResult Detect()
        {
            var windows = _ps.Invoke(PowerCliScripts.WindowsDisksScript).Select(o => new WindowsDisk
            {
                Number = PowerShellHost.Int(o, "Number"),
                SerialNumber = PowerShellHost.Str(o, "SerialNumber").Trim(),
                SizeGB = PowerShellHost.Dbl(o, "SizeGB"),
                MountPath = NullIfEmpty(PowerShellHost.Str(o, "MountPath"))
            }).ToList();

            var vmDisks = _vc.GetDisks(_vmName);
            if (vmDisks.Any(d => string.IsNullOrWhiteSpace(d.Uuid)))
                _log.Warn("Some disks of " + _vmName + " report no UUID: set disk.EnableUUID=TRUE on the virtual machine");

            var join = DiskIdentity.Join(windows, vmDisks);
            foreach (var d in join.Detected) d.AgName = ReadAgName(d);
            return join;
        }

        public void StampSlot(DetectedDisk disk, string slot)
        {
            var path = SignPath(disk);
            if (path == null || !File.Exists(path)) return;
            try
            {
                var ini = IniDocument.Load(path);
                if (ini.Get("SCSI", "VMware_ID") == slot) return;
                ini.Set("SCSI", "VMware_ID", slot);
                ini.Save(path);
            }
            catch (Exception ex) { _log.Warn("Cannot update " + path + ": " + ex.Message); }
        }

        private string ReadAgName(DetectedDisk d)
        {
            var path = SignPath(d);
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var name = IniDocument.Load(path).Get("AG", "Name").Trim();
                return name.Length == 0 ? "##ERROR##" : name;
            }
            catch (Exception ex) { _log.Error("Cannot read " + path + ": " + ex.Message); return "##ERROR##"; }
        }

        private static string SignPath(DetectedDisk d) => d.MountPath == null ? null : Path.Combine(d.MountPath, "jelena", "disksign.ini");
        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
