<#
.SYNOPSIS
  Copies the built KSPUITest DLLs into a KSP install's GameData (backing up what was there).
  Integration DLLs (KSPUITest.RP1, KSPUITest.MechJeb and their test DLLs) are copied only when the
  mod they reference is present in that install, so nothing type-loads against a missing assembly.

.EXAMPLE
  .\deploy.ps1                                  # KSP-Vanilla
  .\deploy.ps1 -Install D:\Games\KSP-RP1-Stock
  .\deploy.ps1 -Build                           # msbuild all projects first
#>
[CmdletBinding()]
param(
    [string] $Install = 'D:\Games\KSP-Vanilla',
    [switch] $Build,
    [string] $MsBuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Install = [IO.Path]::GetFullPath($Install)
$src = Join-Path $here 'GameData\KSPUITest\Plugins'
$dst = Join-Path $Install 'GameData\KSPUITest\Plugins'
if (-not (Test-Path (Join-Path $Install 'KSP_x64.exe'))) { throw "not a KSP install: $Install" }

$running = Get-Process -Name KSP_x64 -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$Install\*" } catch { $false } }
if ($running) { throw "KSP is running from $Install; plugins are only loaded at startup, deploy after it exits." }

if ($Build) {
    $projects = @('Source\Tests.Stock\Tests.Stock.csproj')
    foreach ($p in @('Source\KSPUITest.MechJeb\KSPUITest.MechJeb.csproj', 'Source\Tests.MechJeb\Tests.MechJeb.csproj',
                     'Source\KSPUITest.RP1\KSPUITest.RP1.csproj', 'Source\Tests.RP1\Tests.RP1.csproj')) {
        if (Test-Path (Join-Path $here $p)) { $projects += $p }
    }
    foreach ($p in $projects) {
        Write-Host "building $p"
        # build MechJeb/RP-1 integrations against the DLLs of the install they are deployed into
        $extra = @()
        if (Test-Path (Join-Path $Install 'GameData\MechJeb2\Plugins\MechJeb2.dll')) { $extra += "-p:MechJebPath=$(Join-Path $Install 'GameData\MechJeb2\Plugins')" }
        if (Test-Path (Join-Path $Install 'GameData\RP-1\Plugins\RP0.dll')) { $extra += "-p:RP0Path=$(Join-Path $Install 'GameData\RP-1\Plugins')" }
        & $MsBuild (Join-Path $here $p) -p:Configuration=Release -p:Platform=x64 -nologo -v:m @extra
        if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
    }
}

# which DLLs may go into this install
$hasRp1 = Test-Path (Join-Path $Install 'GameData\RP-1\Plugins\RP0.dll')
$hasMj  = Test-Path (Join-Path $Install 'GameData\MechJeb2\Plugins\MechJeb2.dll')
$requires = @{
    'KSPUITest.RP1.dll'           = $hasRp1
    'KSPUITest.Tests.RP1.dll'     = $hasRp1
    'KSPUITest.MechJeb.dll'       = $hasMj
    'KSPUITest.Tests.MechJeb.dll' = $hasMj
}

New-Item -ItemType Directory -Force $dst | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Install 'GameData\KSPUITest\PluginData') | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($f in Get-ChildItem $src -Filter '*.dll') {
    if ($requires.ContainsKey($f.Name) -and -not $requires[$f.Name]) {
        Write-Host "skip   $($f.Name) (dependency not in this install)"
        $stale = Join-Path $dst $f.Name
        if (Test-Path $stale) { Remove-Item $stale; Write-Host "removed stale $($f.Name)" }
        continue
    }
    $target = Join-Path $dst $f.Name
    if (Test-Path $target) { Copy-Item $target "$target.bak.$stamp" }
    Copy-Item $f.FullName $target
    $pdb = [IO.Path]::ChangeExtension($f.FullName, '.pdb')
    if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $dst ([IO.Path]::GetFileName($pdb))) }
    Write-Host "deploy $($f.Name) -> $target"
}
# keep only the 3 newest backups per DLL
Get-ChildItem $dst -Filter '*.bak.*' | Group-Object { $_.Name -replace '\.bak\..*$', '' } | ForEach-Object {
    $_.Group | Sort-Object LastWriteTime -Descending | Select-Object -Skip 3 | Remove-Item
}
Write-Host "done: $Install (RP-1: $hasRp1, MechJeb: $hasMj)"
