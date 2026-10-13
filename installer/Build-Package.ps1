<#
.SYNOPSIS
  Builds the vHA installation package (VhaSetup-<version>.zip): programs, setup, SQL scripts, documentation and the prerequisite downloader.
.DESCRIPTION
  Works with Windows PowerShell 5.1 and PowerShell 7 (also on Linux/macOS: the programs are .NET Framework 4.8 binaries built with reference assemblies).
  The unit tests must pass before a package is produced. Use -IncludePrerequisites on a machine with Internet access to bundle the .NET Framework 4.8
  offline installer and the VMware PowerCLI module (needed for servers without Internet access).
.EXAMPLE
  .\installer\Build-Package.ps1 -IncludePrerequisites
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$Version = '3.0.0',
    [switch]$IncludePrerequisites,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }

function Invoke-Checked([string]$What, [scriptblock]$Block) {
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}

Push-Location $root
try {
    Invoke-Checked 'dotnet build' { dotnet build (Join-Path $root 'vha.sln') -c $Configuration --nologo -v q }
    if (-not $SkipTests) {
        Invoke-Checked 'unit tests' { dotnet test (Join-Path $root 'tests/Vha.Core.Tests') -c $Configuration --no-build --nologo }
    }
} finally { Pop-Location }

$stage = Join-Path $OutputDirectory "VhaSetup-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Copy-Output([string]$Project, [string]$Target) {
    $src = Join-Path $root "src/$Project/bin/$Configuration/net48"
    if (-not (Test-Path $src)) { throw "Build output not found: $src" }
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    Get-ChildItem -LiteralPath $src -File | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $Target -Force
}
Copy-Output 'VhaSetup'        (Join-Path $stage 'setup')
Copy-Output 'ClusteredService' (Join-Path $stage 'bin/ClusteredService')
Copy-Output 'CsuCmd'          (Join-Path $stage 'bin/CsuCmd')

Copy-Item (Join-Path $root 'sql')      (Join-Path $stage 'sql')      -Recurse
Copy-Item (Join-Path $root 'scripts')  (Join-Path $stage 'scripts')  -Recurse
Copy-Item (Join-Path $root 'docs')     (Join-Path $stage 'docs')     -Recurse
Copy-Item (Join-Path $root 'examples') (Join-Path $stage 'examples') -Recurse
Copy-Item (Join-Path $root 'installer/Setup.cmd')   $stage
Copy-Item (Join-Path $root 'installer/LEGGIMI.txt') $stage
Copy-Item (Join-Path $root 'README.md')              $stage
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'prereq') | Out-Null
Copy-Item (Join-Path $root 'installer/prereq/*') (Join-Path $stage 'prereq') -Recurse

if ($IncludePrerequisites) {
    & (Join-Path $stage 'prereq/Get-Prerequisites.ps1') -Destination (Join-Path $stage 'prereq')
}

# the package must contain everything the setup needs
$required = @('setup/VhaSetup.exe', 'setup/Vha.Core.dll', 'setup/Vha.Infrastructure.dll',
    'bin/ClusteredService/ClusteredService.exe', 'bin/ClusteredService/Vha.Core.dll', 'bin/ClusteredService/Vha.Infrastructure.dll', 'bin/ClusteredService/Microsoft.Win32.TaskScheduler.dll',
    'bin/CsuCmd/CsuCmd.exe', 'bin/CsuCmd/Vha.Core.dll', 'bin/CsuCmd/Vha.Infrastructure.dll', 'bin/CsuCmd/Microsoft.Win32.TaskScheduler.dll',
    'sql/01_schema.sql', 'sql/02_register_availability_group.sql', 'sql/03_register_databases.sql', 'sql/04_attach_script_generator.sql', 'sql/05_health_check.sql',
    'Setup.cmd', 'LEGGIMI.txt', 'examples/vha-deployment.example.json', 'prereq/Get-Prerequisites.ps1')
$missing = $required | Where-Object { -not (Test-Path (Join-Path $stage $_)) }
if ($missing) { throw "The package is incomplete, missing: $($missing -join ', ')" }
# a reference-only System.Management.Automation.dll next to the programs would shadow the real Windows PowerShell assembly at run time
$stubs = Get-ChildItem $stage -Recurse -Filter 'System.Management.Automation.dll'
if ($stubs) { throw "Reference assemblies leaked into the package: $($stubs.FullName -join ', ')" }

# checksums of every file, to verify a copy on the target servers: Get-FileHash vs SHA256SUMS.txt
$sums = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $rel
}
Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Value $sums -Encoding ascii

$zip = Join-Path $OutputDirectory "VhaSetup-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Package: $zip"
Write-Host ("Files: {0}, prerequisites bundled: {1}" -f (Get-ChildItem $stage -Recurse -File).Count, [bool]$IncludePrerequisites)
