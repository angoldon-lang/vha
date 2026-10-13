<#
.SYNOPSIS
  Prints the INSERT statements for dbo.JDisks of an availability group (replaces MatchHD_UniqueID_DeviceKey.ps1 and the Excel sheet).
.DESCRIPTION
  Connect to vCenter first (Connect-VIServer ...). Disks are read from the virtual machine that currently has them attached.
  Requires disk.EnableUUID=TRUE on the VM: the UUID is what the service uses to recognise the disk inside Windows.
.EXAMPLE
  Connect-VIServer vc01.corp.local
  .\Get-VhaDiskInfo.ps1 -VmName ENTGSQL01 -AgName isql2016a-AG01 -FileNameLike '*isql2016a*'
#>
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$AgName,
    [string]$FileNameLike = '*'
)
$ErrorActionPreference = 'Stop'
$vm = Get-VM -Name $VmName
$disks = @($vm | Get-HardDisk | Where-Object { $_.Filename -like $FileNameLike })
if ($disks.Count -eq 0) { throw "No disk of $VmName matches '$FileNameLike'" }
function Q([string]$s) { "N'" + $s.Replace("'", "''") + "'" }
Write-Output "-- JDisks for $AgName (generated $(Get-Date -Format s))"
foreach ($d in $disks) {
    $key = [int]($d.Id.Substring($d.Id.LastIndexOf('/') + 1))
    $uuid = [string]$d.ExtensionData.Backing.Uuid
    if (-not $uuid) { throw "Disk $($d.Filename) has no UUID: set disk.EnableUUID=TRUE on $VmName and power-cycle it." }
    if ($key -lt 2000 -or $key -gt 2063 -or (($key - 2000) % 16) -eq 7) { throw "Disk $($d.Filename) has device key $key: only SCSI disks (2000-2063) are supported." }
    $serial = ($uuid -replace '[^0-9A-Za-z]', '').ToUpper()
    Write-Output ("INSERT dbo.JDisks (JAGroupsID, DeviceKey, FileName, CapacityGB, UniqueId, Attached) SELECT ID, '{0}', {1}, {2}, '{3}', 1 FROM dbo.JAGroups WHERE Name = {4};" -f $key, (Q $d.Filename), [math]::Round($d.CapacityGB, 0), $serial, (Q $AgName))
}
