using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Infrastructure.PowerShell
{
    /// <summary>IVCenter on top of the VMware PowerCLI module (VMware.PowerCLI 12/13, vSphere 6.7 - 8.0).</summary>
    public sealed class PowerCliVCenter : IVCenter, IDisposable
    {
        private readonly PowerShellHost _ps;
        private readonly ILog _log;
        private bool _connected;

        public PowerCliVCenter(PowerShellHost ps, ILog log) { _ps = ps; _log = log; }

        /// <param name="connectVCenterSetting">Text of JSettings.ConnectVCenter.</param>
        public void Connect(string connectVCenterSetting)
        {
            var spec = VCenterConnectionSpec.Parse(connectVCenterSetting, SecretProtector.Unprotect);
            foreach (var w in spec.Warnings) _log.Warn(w);
            try
            {
                _ps.Invoke("Import-Module VMware.VimAutomation.Core -ErrorAction Stop");
            }
            catch (PowerShellException ex)
            {
                throw new PowerShellException("The PowerCLI module VMware.PowerCLI is not available for the account running this process (Install-Module VMware.PowerCLI -Scope AllUsers). The legacy PSSnapin is not supported any more. " + ex.Message, ex);
            }
            _log.Info("PowerCLI module loaded, connecting to " + spec.Server);
            _ps.Invoke(spec.Script, spec.Variables);
            _connected = true;
            _log.Info("Connected to virtual center " + spec.Server);
        }

        public VmInfo GetVm(string name)
        {
            var r = _ps.Invoke(PowerCliScripts.GetVm(name)).FirstOrDefault();
            if (r == null) throw new PowerShellException("Virtual machine '" + name + "' not found in vCenter");
            return new VmInfo
            {
                Id = PowerShellHost.Str(r, "Id"), Name = PowerShellHost.Str(r, "Name"), PowerState = PowerShellHost.Str(r, "PowerState"),
                SnapshotCount = PowerShellHost.Int(r, "SnapshotCount"),
                CpuReservationMhz = PowerShellHost.Int(r, "CpuReservationMhz"), MemReservationMB = PowerShellHost.Int(r, "MemReservationMB")
            };
        }

        public IReadOnlyList<VmDisk> GetDisks(string vmName)
        {
            return _ps.Invoke(PowerCliScripts.GetDisks(vmName)).Select(o => new VmDisk
            {
                Id = PowerShellHost.Str(o, "Id"), FileName = PowerShellHost.Str(o, "FileName"),
                CapacityGB = PowerShellHost.Dbl(o, "CapacityGB"), Uuid = PowerShellHost.Str(o, "Uuid")
            }).ToList();
        }

        public bool DatastoreFileExists(string fileName)
        {
            var r = _ps.Invoke(PowerCliScripts.DatastoreFileExists(fileName)).FirstOrDefault();
            return r != null && Convert.ToInt32(r.BaseObject) > 0;
        }

        public void Reconfigure(VmInfo vm, DiskChange change)
        {
            var script = PowerCliScripts.Reconfigure(vm.Id, change);
            _log.Info("Reconfiguring " + vm.Name + " (" + change.Action + " " + string.Join(",", change.Disks.Select(d => d.DeviceKey)) + ")");
            _log.Debug(script);
            _ps.Invoke(script);
        }

        public void Dispose()
        {
            if (!_connected) return;
            try { _ps.Invoke("Disconnect-VIServer -Server * -Confirm:$false -Force -ErrorAction SilentlyContinue"); }
            catch (Exception ex) { _log.Warn("Disconnect from vCenter failed: " + ex.Message); }
            _connected = false;
        }
    }
}
