<#
.SYNOPSIS
  Downloads the offline prerequisites of vHA and verifies their digital signatures.
.DESCRIPTION
  - .NET Framework 4.8 offline installer from Microsoft (signature must be a valid Microsoft Corporation signature)
  - the VMware.PowerCLI module with all its dependencies through Save-Module (every script/module/dll must carry a valid signature)
  Run it on a machine with Internet access and copy the whole package folder to the servers.
#>
[CmdletBinding()]
param(
    [string]$Destination = $PSScriptRoot,
    [string]$PowerCliMinimumVersion = '12.0'
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# ---- .NET Framework 4.8
$dotnet = Join-Path $Destination 'ndp48-x86-x64-allos-enu.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    Write-Host 'Downloading the .NET Framework 4.8 offline installer...'
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=2088631' -OutFile $dotnet -UseBasicParsing
}
$sig = Get-AuthenticodeSignature -LiteralPath $dotnet
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
    Remove-Item -LiteralPath $dotnet -Force
    throw ".NET Framework installer rejected: signature status $($sig.Status). The file was deleted."
}
Write-Host ".NET Framework 4.8 installer OK ($($sig.SignerCertificate.Subject))"

# ---- VMware.PowerCLI
$ps = Join-Path $Destination 'PowerCLI'
if (-not (Test-Path -LiteralPath $ps)) {
    Write-Host 'Downloading VMware.PowerCLI (this takes a few minutes)...'
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force | Out-Null
    New-Item -ItemType Directory -Force -Path $ps | Out-Null
    Save-Module -Name VMware.PowerCLI -MinimumVersion $PowerCliMinimumVersion -Path $ps -Force
}
$bad = @()
foreach ($f in Get-ChildItem -LiteralPath $ps -Recurse -File -Include *.psd1, *.psm1, *.ps1, *.dll) {
    $s = Get-AuthenticodeSignature -LiteralPath $f.FullName
    # third party dependencies of PowerCLI are not all signed by VMware: require a VALID signature when one is present
    if ($s.Status -notin @('Valid', 'NotSigned')) { $bad += "$($f.FullName): $($s.Status)" }
}
$psd1 = Get-ChildItem -LiteralPath $ps -Recurse -Filter 'VMware.PowerCLI.psd1' | Select-Object -First 1
if (-not $psd1) { throw 'VMware.PowerCLI.psd1 not found after Save-Module' }
$sigMain = Get-AuthenticodeSignature -LiteralPath $psd1.FullName
if ($sigMain.Status -ne 'Valid' -or $sigMain.SignerCertificate.Subject -notmatch 'VMware|Broadcom') {
    throw "VMware.PowerCLI.psd1 is not signed by VMware/Broadcom (status $($sigMain.Status))"
}
if ($bad) { throw ("Files with an invalid signature:`n" + ($bad -join "`n")) }
Write-Host "VMware.PowerCLI OK ($($sigMain.SignerCertificate.Subject))"
Write-Host 'Prerequisites ready in' $Destination
