<#
.SYNOPSIS
  Read-only environment certification for vHA: collects versions and checks the assumptions vHA depends on, on REAL Windows / vCenter / ESXi.
.DESCRIPTION
  Run it (1) on every node ("Node" checks) and (2) once from any machine with PowerCLI ("VCenter" checks).
  It changes nothing. It writes a report (Markdown + JSON) that is the evidence for the compatibility matrix in docs/LICENZE-E-COMPATIBILITA.md.
  Windows PowerShell 5.1:  .\Test-VhaEnvironment.ps1 -Mode Node
                           .\Test-VhaEnvironment.ps1 -Mode VCenter -VCenter vc01.example.local -Vm SQLVM1,SQLVM2 -Credential (Get-Credential)
.NOTES
  Written but not yet executed on real systems when this file was added: treat the first run as part of the certification.
#>
[CmdletBinding()]
param(
    [ValidateSet('Node', 'VCenter')][string]$Mode = 'Node',
    [string]$VCenter,
    [string[]]$Vm,
    [pscredential]$Credential,
    [string]$OutputDirectory = (Get-Location).Path,
    [switch]$AllowUntrustedCertificate
)
$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
$facts = [ordered]@{}

function Add-Result([string]$Area, [string]$Name, [ValidateSet('PASS', 'WARN', 'FAIL')][string]$Status, [string]$Detail) {
    $results.Add([pscustomobject]@{ Area = $Area; Check = $Name; Status = $Status; Detail = $Detail })
    $color = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red' }[$Status]
    Write-Host ('[{0}] {1}: {2} - {3}' -f $Status, $Area, $Name, $Detail) -ForegroundColor $color
}
function Test-Step([string]$Area, [string]$Name, [scriptblock]$Block) {
    try { $r = & $Block; if ($r -is [hashtable]) { Add-Result $Area $Name $r.Status $r.Detail } else { Add-Result $Area $Name 'PASS' ([string]$r) } }
    catch { Add-Result $Area $Name 'FAIL' $_.Exception.Message }
}

if ($Mode -eq 'Node') {
    $facts.Machine = $env:COMPUTERNAME
    $os = Get-CimInstance Win32_OperatingSystem
    $facts.Windows = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
    $facts.PowerShell = $PSVersionTable.PSVersion.ToString()

    Test-Step 'OS' 'Windows PowerShell 5.1' { if ($PSVersionTable.PSEdition -eq 'Desktop' -and $PSVersionTable.PSVersion.Major -eq 5) { "$($PSVersionTable.PSVersion)" } else { @{ Status = 'FAIL'; Detail = "vHA needs Windows PowerShell 5.1, found $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)" } } }
    Test-Step 'OS' '.NET Framework 4.8 or later' {
        $rel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop).Release
        $facts.DotNetRelease = $rel
        if ($rel -ge 528040) { "release $rel" } else { @{ Status = 'FAIL'; Detail = "release $rel is older than 4.8 (528040)" } } }
    Test-Step 'OS' 'Failover Clustering feature' { $f = Get-WindowsFeature Failover-Clustering -ErrorAction Stop; if ($f.Installed) { 'installed' } else { @{ Status = 'WARN'; Detail = 'not installed (VhaSetup installs it)' } } }
    Test-Step 'OS' 'Domain member' { $cs = Get-CimInstance Win32_ComputerSystem; $facts.Domain = $cs.Domain; if ($cs.PartOfDomain) { $cs.Domain } else { @{ Status = 'WARN'; Detail = 'workgroup machine: WSFC needs a domain' } } }
    Test-Step 'OS' 'PowerCLI module' {
        $m = Get-Module -ListAvailable VMware.PowerCLI, VMware.VimAutomation.Core | Sort-Object Version -Descending | Select-Object -First 1
        if (-not $m) { @{ Status = 'FAIL'; Detail = 'VMware.PowerCLI not installed' } } else { $facts.PowerCLI = "$($m.Name) $($m.Version)"; $facts.PowerCLI } }

    # --- the assumption vHA depends on: Windows disk serial number = VMDK UUID without dashes
    Test-Step 'Disks' 'Windows sees disks with a serial number' {
        $disks = @(Get-Disk | Where-Object { [string]$_.BusType -ne 'NVMe' })
        $facts.Disks = @($disks | ForEach-Object { [pscustomobject]@{ Number = $_.Number; BusType = [string]$_.BusType; Serial = ([string]$_.SerialNumber).Trim(); UniqueId = [string]$_.UniqueId; SizeGB = [math]::Round($_.Size / 1GB, 1); Offline = $_.IsOffline } })
        $blank = @($disks | Where-Object { -not ([string]$_.SerialNumber).Trim() })
        if ($disks.Count -eq 0) { @{ Status = 'FAIL'; Detail = 'no disk found' } }
        elseif ($blank.Count) { @{ Status = 'FAIL'; Detail = "$($blank.Count) disk(s) without serial number: check disk.EnableUUID=TRUE on the VM and power-cycle it" } }
        else { "$($disks.Count) disk(s)" } }
    Test-Step 'Disks' 'No NVMe/SATA data disks (not supported by vHA)' {
        $bad = @(Get-Disk | Where-Object { $_.BusType -in 'NVMe', 'SATA', 'ATA' -and -not $_.IsBoot -and -not $_.IsSystem })
        if ($bad.Count) { @{ Status = 'WARN'; Detail = "disks $($bad.Number -join ',') use bus type $(($bad.BusType | Select-Object -Unique) -join ',')" } } else { 'none' } }

    Test-Step 'SQL' 'SQL Server instance(s)' {
        $names = @(Get-Service | Where-Object { $_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'MSSQL$*' })
        if (-not $names) { @{ Status = 'FAIL'; Detail = 'no SQL Server service found' } } else {
            $out = foreach ($s in $names) {
                $inst = if ($s.Name -eq 'MSSQLSERVER') { '.' } else { ".\$($s.Name.Substring(6))" }
                try {
                    $cn = New-Object System.Data.SqlClient.SqlConnection("Server=$inst;Integrated Security=SSPI;Connect Timeout=10;TrustServerCertificate=True")
                    $cn.Open(); $cmd = $cn.CreateCommand(); $cmd.CommandText = "SELECT CONVERT(varchar(64),SERVERPROPERTY('ProductVersion')) + ' | ' + CONVERT(varchar(64),SERVERPROPERTY('Edition')) + ' | ' + CONVERT(varchar(64),SERVERPROPERTY('ProductLevel'))"
                    $v = [string]$cmd.ExecuteScalar(); $cn.Close(); "$inst : $v"
                } catch { "$inst : not reachable ($($_.Exception.Message))" } }
            $facts.Sql = @($out); ($out -join '; ') } }

    Test-Step 'vHA' 'ClusteredService.exe --version' {
        $exe = Get-ChildItem -Path 'C:\Program Files*\vHA*', 'C:\vHA*', "$PSScriptRoot\..\bin\ClusteredService" -Filter ClusteredService.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $exe) { @{ Status = 'WARN'; Detail = 'ClusteredService.exe not found (not installed yet)' } } else { $v = (& $exe.FullName --version) -join ' '; $facts.VhaVersion = $v; $v } }
}
else {
    if (-not $VCenter) { throw '-VCenter is required for -Mode VCenter' }
    Import-Module VMware.VimAutomation.Core -ErrorAction Stop
    $facts.PowerCLI = (Get-Module VMware.VimAutomation.Core).Version.ToString()
    if ($AllowUntrustedCertificate) { Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -Scope Session | Out-Null }
    Set-PowerCLIConfiguration -Scope Session -DefaultVIServerMode Single -Confirm:$false | Out-Null
    $srv = $null
    Test-Step 'vCenter' 'Connect' {
        $args2 = @{ Server = $VCenter; ErrorAction = 'Stop' }; if ($Credential) { $args2.Credential = $Credential }
        $script:srv = Connect-VIServer @args2
        $facts.VCenter = "$($srv.Name) $($srv.Version) build $($srv.Build) ($($srv.ProductLine))"; $facts.VCenter }
    $srv = $script:srv
    if ($srv) {
        Test-Step 'vCenter' 'Supported generation (6.7 / 7.0 / 8.0)' {
            $major = [version]$srv.Version
            if ($major.Major -ge 6 -and $major.Major -le 8) { "vSphere $($srv.Version)" } else { @{ Status = 'WARN'; Detail = "vSphere $($srv.Version) is outside the generations vHA documents (6.x-8.x)" } } }
        Test-Step 'vCenter' 'Hosts' { $h = @(Get-VMHost); $facts.Hosts = @($h | ForEach-Object { "$($_.Name) ESXi $($_.Version) build $($_.Build) [$($_.ConnectionState)]" }); "$($h.Count) host(s)" }
        foreach ($name in $Vm) {
            Test-Step "VM $name" 'Found, powered on' { $v = Get-VM -Name $name -ErrorAction Stop; if ($v.PowerState -eq 'PoweredOn') { "host $($v.VMHost.Name), HW $($v.HardwareVersion)" } else { @{ Status = 'WARN'; Detail = "state $($v.PowerState)" } } }
            Test-Step "VM $name" 'disk.EnableUUID = TRUE' { $s = Get-AdvancedSetting -Entity (Get-VM $name) -Name 'disk.EnableUUID' -ErrorAction SilentlyContinue; if ($s -and "$($s.Value)" -eq 'True') { 'TRUE' } else { @{ Status = 'FAIL'; Detail = 'missing or FALSE: Windows will not see the VMDK UUID as serial number' } } }
            Test-Step "VM $name" 'No snapshots' { $n = @(Get-Snapshot -VM $name -ErrorAction SilentlyContinue).Count; if ($n -eq 0) { 'none' } else { @{ Status = 'FAIL'; Detail = "$n snapshot(s): hot-remove of the data disks would fail" } } }
            Test-Step "VM $name" 'Hard disks: SCSI, controller / unit, UUID' {
                $rows = foreach ($d in (Get-HardDisk -VM $name)) {
                    $c = $d.ExtensionData.ControllerKey; $u = $d.ExtensionData.UnitNumber
                    [pscustomobject]@{ File = $d.Filename; Key = $d.ExtensionData.Key; Controller = $c; Unit = $u; Uuid = $d.ExtensionData.Backing.Uuid; Type = $d.DiskType; Storage = $d.StorageFormat; Mode = $d.Persistence }
                }
                $facts["Disks:$name"] = @($rows)
                $odd = @($rows | Where-Object { $_.Key -lt 2000 -or $_.Key -ge 3000 })
                $rdm = @($rows | Where-Object { $_.Type -like '*Raw*' })
                if ($rdm.Count) { @{ Status = 'WARN'; Detail = "$($rdm.Count) raw device mapping disk(s): not supported by vHA" } }
                elseif ($odd.Count) { @{ Status = 'WARN'; Detail = "disk key outside 2000-2999 (not SCSI?): $(($odd.Key) -join ',')" } }
                else { "$(@($rows).Count) disk(s)" } }
        }
        Test-Step 'vCenter' 'Datastore visibility (every host sees every datastore of the VMs)' {
            $ds = @(foreach ($name in $Vm) { Get-VM $name | Get-Datastore }) | Sort-Object Id -Unique
            $hosts = @(Get-VMHost)
            $missing = foreach ($d in $ds) { foreach ($h in $hosts) { if (-not (Get-Datastore -VMHost $h -Name $d.Name -ErrorAction SilentlyContinue)) { "$($d.Name) not on $($h.Name)" } } }
            if ($missing) { @{ Status = 'WARN'; Detail = ($missing -join '; ') } } else { "$($ds.Count) datastore(s)" } }
        Disconnect-VIServer -Server $srv -Confirm:$false -ErrorAction SilentlyContinue
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base = Join-Path $OutputDirectory "vha-environment-$Mode-$stamp"
[pscustomobject]@{ Mode = $Mode; Date = (Get-Date -Format s); Facts = $facts; Results = $results } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$base.json" -Encoding UTF8
$md = New-Object System.Collections.Generic.List[string]
$md.Add("# vHA environment report ($Mode) - $stamp"); $md.Add('')
foreach ($k in $facts.Keys) { if ($facts[$k] -isnot [array]) { $md.Add("- **$k**: $($facts[$k])") } }
$md.Add(''); $md.Add('| Area | Check | Result | Detail |'); $md.Add('|---|---|---|---|')
foreach ($r in $results) { $md.Add("| $($r.Area) | $($r.Check) | $($r.Status) | $(([string]$r.Detail) -replace '\|', '/') |") }
$md | Set-Content -LiteralPath "$base.md" -Encoding UTF8
$fail = @($results | Where-Object Status -eq 'FAIL').Count; $warn = @($results | Where-Object Status -eq 'WARN').Count
Write-Host ("`nReport: {0}.md / .json  -  FAIL {1}, WARN {2}, PASS {3}" -f $base, $fail, $warn, ($results.Count - $fail - $warn))
exit $(if ($fail) { 1 } else { 0 })
