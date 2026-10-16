<#
.SYNOPSIS
  Copies the harness and the test projects in this repo into a KSP install. A thin wrapper over `ksptest deploy`
  (src/KspTest.Tool); -Build first builds everything against that install's KSP and mod DLLs.

.EXAMPLE
  .\deploy.ps1 -Install C:\Games\KSP-RP1-Stock -Build
#>
[CmdletBinding()]
param(
    [string] $Install = 'D:\Games\KSP-Vanilla',
    [switch] $Build
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Install = [IO.Path]::GetFullPath($Install)

if ($Build) {
    dotnet build (Join-Path $here 'KspTest.slnx') -c Release -nologo -v:m "-p:KspDir=$Install"
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}
$tool = Join-Path $here 'src\KspTest.Tool\bin\Release\net8.0\KspTest.Tool.dll'
if (-not (Test-Path $tool)) { throw "ksptest is not built ($tool): run with -Build" }

# the test projects still in this repo, each deployed only where the mods it needs are
$tests = @()
$needs = @{ 'KspTest.Tests.Stock' = @(); 'KspTest.Tests.MechJeb' = @('MechJeb2\Plugins\MechJeb2.dll')
            'KspTest.Tests.RP1' = @('RP-1\Plugins\RP0.dll', 'MechJeb2\Plugins\MechJeb2.dll') }
foreach ($name in $needs.Keys) {
    $dll = Join-Path $here "tests\$name\bin\Release\$name.dll"
    $missing = @($needs[$name] | Where-Object { -not (Test-Path (Join-Path $Install "GameData\$_")) })
    if (-not (Test-Path $dll)) { Write-Host "skip   $name (not built)"; continue }
    if ($missing.Count -gt 0) { Write-Host "skip   $name (needs $($missing -join ', '))"; continue }
    $tests += @('--tests', "$name=$dll")
}

& dotnet $tool deploy --install $Install --harness (Join-Path $here 'GameData\KspTest') @tests
exit $LASTEXITCODE
