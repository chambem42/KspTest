<#
.SYNOPSIS
  Runs one or more in-game KspTest tests in a single KSP session. A thin wrapper over `ksptest run` (src/KspTest.Tool),
  kept with its old parameters for the scripts that call it; see `ksptest --help`.

.EXAMPLE
  .\run-test.ps1 -Test Smoke_MainMenuToVab
  .\run-test.ps1 -Test Kct_SimConfig_Reentry -Install C:\Games\KSP-RP1-Stock -Save rp1-career
  .\run-test.ps1 -Test list
  .\run-test.ps1 -Test "Mj_*" -Params "orbitAltKm=120;inclination=30"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Test,
    [string] $Install = 'D:\Games\KSP-Vanilla',
    # save template: a folder, or its name under KspTest\saves
    [string] $Save = 'stock-sandbox',
    [string] $SaveFolder = 'ksptest',
    [switch] $NoReset,
    [int] $TimeoutSec = 900,
    [switch] $Keep,
    [int] $Width = 1920,
    [int] $Height = 1080,
    # hashtable, or a string "key=value;key=value"
    [object] $Params = @{},
    [string] $ResultsRoot = '',
    [int] $LoadStallSec = 120,
    [int] $LoadRetries = 2,
    [int] $RunStallSec = 600,
    [int] $GcDivisor = 0,
    [switch] $Mute,
    [int] $WaitForOtherKspSec = 7200
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$tool = Join-Path $here 'src\KspTest.Tool\bin\Release\net8.0\KspTest.Tool.dll'
if (-not (Test-Path $tool)) {
    dotnet build (Join-Path $here 'src\KspTest.Tool\KspTest.Tool.csproj') -c Release -nologo -v:q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'building ksptest failed' }
}
if (-not $ResultsRoot) { $ResultsRoot = Join-Path $here 'results' }
if ($Params -is [hashtable]) { $Params = ($Params.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';' }

# the lock shared with the other KSP launchers on this machine sits beside this folder
$lock = Join-Path (Split-Path $here -Parent) 'ksp.lock'
$a = if ($Test -eq 'list') { @('list') } else { @('run', $Test) }
$a += @('--install', $Install, '--save', $Save, '--saves', (Join-Path $here 'saves'), '--save-folder', $SaveFolder,
        '--timeout', $TimeoutSec, '--width', $Width, '--height', $Height, '--results', $ResultsRoot,
        '--save-backups', (Join-Path $here 'save-backups'), '--lock', $lock, '--load-stall', $LoadStallSec,
        '--load-retries', $LoadRetries, '--run-stall', $RunStallSec, '--gc-divisor', $GcDivisor, '--wait', $WaitForOtherKspSec)
if ($Params) { $a += @('--params', $Params) }
if ($NoReset) { $a += '--no-reset' }
if ($Keep) { $a += '--keep' }
if ($Mute) { $a += '--mute' }

& dotnet $tool @a
exit $LASTEXITCODE
