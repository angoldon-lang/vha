using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vha.Core.Model;
using Vha.Core.Util;

namespace Vha.Core.Vmware
{
    public enum DiskAction { Add, Remove }

    public sealed class DiskChange
    {
        public DiskAction Action { get; set; }
        public IReadOnlyList<ConfiguredDisk> Disks { get; set; }
        public int? CpuReservationMhz { get; set; }
        public int? MemReservationMB { get; set; }
    }

    /// <summary>Builds the PowerCLI scripts. All user supplied text goes through PsQuote.</summary>
    public static class PowerCliScripts
    {
        public static string GetVm(string vmName)
        {
            return "$vm = Get-VM -Name " + PsQuote.Single(vmName) + " -ErrorAction Stop\r\n"
                 + "if (@($vm).Count -ne 1) { throw 'Expected exactly one virtual machine named ' + " + PsQuote.Single(vmName) + " }\r\n"
                 + "$rc = $vm | Get-VMResourceConfiguration\r\n"
                 + "[pscustomobject]@{ Id = [string]$vm.Id; Name = [string]$vm.Name; PowerState = [string]$vm.PowerState;"
                 + " SnapshotCount = [int]@(Get-Snapshot -VM $vm -ErrorAction SilentlyContinue).Count;"
                 + " CpuReservationMhz = [int]$rc.CpuReservationMhz; MemReservationMB = [int]$rc.MemReservationMB }";
        }

        public static string GetDisks(string vmName)
        {
            return "Get-VM -Name " + PsQuote.Single(vmName) + " -ErrorAction Stop | Get-HardDisk | ForEach-Object {"
                 + " [pscustomobject]@{ Id = [string]$_.Id; FileName = [string]$_.Filename; CapacityGB = [double]$_.CapacityGB;"
                 + " Uuid = [string]$_.ExtensionData.Backing.Uuid } }";
        }

        public static string DatastoreFileExists(string fileName)
        {
            var ds = DatastoreName(fileName);
            return "@(Get-Datastore -Name " + PsQuote.Single(ds) + " -ErrorAction Stop | Get-HardDisk -DatastorePath " + PsQuote.Single(fileName)
                 + " -ErrorAction SilentlyContinue).Count";
        }

        /// <summary>"[DATASTORE] folder/file.vmdk" -> "DATASTORE"</summary>
        public static string DatastoreName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName[0] != '[') throw new ArgumentException("Not a datastore path: " + fileName, nameof(fileName));
            var end = fileName.IndexOf(']');
            if (end < 2) throw new ArgumentException("Not a datastore path: " + fileName, nameof(fileName));
            return fileName.Substring(1, end - 1);
        }

        /// <summary>ReconfigVM spec that adds or removes existing VMDKs (never deletes files) and optionally sets reservations.</summary>
        public static string Reconfigure(string vmId, DiskChange change)
        {
            if (string.IsNullOrWhiteSpace(vmId)) throw new ArgumentException("VM id required", nameof(vmId));
            if (change == null) throw new ArgumentNullException(nameof(change));
            var disks = change.Disks ?? new List<ConfiguredDisk>();
            if (disks.Count == 0) throw new ArgumentException("No disks to " + change.Action);

            var sb = new StringBuilder();
            sb.Append("$spec = New-Object VMware.Vim.VirtualMachineConfigSpec\r\n");
            if (change.CpuReservationMhz.HasValue)
            {
                sb.Append("$spec.cpuAllocation = New-Object VMware.Vim.ResourceAllocationInfo\r\n");
                sb.Append("$spec.cpuAllocation.reservation = ").Append(Math.Max(change.CpuReservationMhz.Value, 0)).Append("\r\n");
            }
            if (change.MemReservationMB.HasValue)
            {
                sb.Append("$spec.memoryAllocation = New-Object VMware.Vim.ResourceAllocationInfo\r\n");
                sb.Append("$spec.memoryAllocation.reservation = ").Append(Math.Max(change.MemReservationMB.Value, 0)).Append("\r\n");
            }
            // The array has exactly one populated entry per disk (the old code left null holes).
            sb.Append("$spec.deviceChange = New-Object VMware.Vim.VirtualDeviceConfigSpec[] (").Append(disks.Count).Append(")\r\n");
            var op = change.Action == DiskAction.Add ? "add" : "remove";
            for (int i = 0; i < disks.Count; i++)
            {
                var d = disks[i];
                var slot = ScsiSlot.FromDeviceKey(d.DeviceKey);
                sb.Append("$spec.deviceChange[").Append(i).Append("] = New-Object VMware.Vim.VirtualDeviceConfigSpec\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].operation = '").Append(op).Append("'\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].device = New-Object VMware.Vim.VirtualDisk\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].device.key = ").Append(slot.DeviceKey).Append("\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].device.backing = New-Object VMware.Vim.VirtualDiskFlatVer2BackingInfo\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].device.backing.fileName = ").Append(PsQuote.Single(d.FileName)).Append("\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].device.backing.diskMode = 'persistent'\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].device.controllerKey = ").Append(slot.ControllerKey).Append("\r\n");
                sb.Append("$spec.deviceChange[").Append(i).Append("].device.unitNumber = ").Append(slot.Unit).Append("\r\n");
            }
            sb.Append("$_this = Get-View -Id ").Append(PsQuote.Single(vmId)).Append("\r\n");
            sb.Append("$_this.ReconfigVM($spec)");
            return sb.ToString();
        }

        public static string SetDiskOffline(IEnumerable<ConfiguredDisk> disks, string computerName)
        {
            var ids = string.Join(",", disks.Select(d => PsQuote.Single(d.UniqueId)));
            var inner = "$ids = @(" + ids + "); foreach ($id in $ids) { $d = Get-Disk | Where-Object { $_.UniqueId -eq $id -or ($_.SerialNumber -and $_.SerialNumber.Trim() -eq $id) };"
                      + " if (-not $d) { throw ('Disk ' + $id + ' not found') }; if (-not $d.IsOffline) { Set-Disk -InputObject $d -IsOffline $true -ErrorAction Stop } }";
            return "Invoke-Command -ComputerName " + PsQuote.Single(computerName) + " -ErrorAction Stop -ScriptBlock { " + inner + " }";
        }

        public static string SetDiskOnlineLocal(IEnumerable<ConfiguredDisk> disks)
        {
            var ids = string.Join(",", disks.Select(d => PsQuote.Single(d.UniqueId)));
            return "$ids = @(" + ids + "); foreach ($id in $ids) { $d = Get-Disk | Where-Object { $_.UniqueId -eq $id -or ($_.SerialNumber -and $_.SerialNumber.Trim() -eq $id) };"
                 + " if (-not $d) { throw ('Disk ' + $id + ' not found') };"
                 + " if ($d.IsOffline) { Set-Disk -InputObject $d -IsOffline $false -ErrorAction Stop };"
                 + " if ($d.IsReadOnly) { Set-Disk -InputObject $d -IsReadOnly $false -ErrorAction Stop } }";
        }

        /// <summary>Windows disks with serial number and the first mount path / drive of their partitions.</summary>
        public const string WindowsDisksScript =
            "Get-Disk | ForEach-Object { $disk = $_; $paths = @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | ForEach-Object { $_.AccessPaths } | Where-Object { $_ -and $_ -notlike '\\\\?\\Volume*' });"
            + " [pscustomobject]@{ Number = [int]$disk.Number; SerialNumber = [string]$disk.SerialNumber; SizeGB = [double]($disk.Size / 1GB); MountPath = [string]($paths | Select-Object -First 1) } }";
    }
}
