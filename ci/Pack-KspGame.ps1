<#
.SYNOPSIS
    Packs a stock KSP into encrypted 7z volumes for in-game CI on GitHub's runners.

.DESCRIPTION
    Takes the stock files KspTest's instances use (see StockBase.cs: the executables, KSP_x64_Data or KSP_Data,
    MonoBleedingEdge, GameData/Squad, the small stock folders) from any install, modded or not. Left out: files the
    install's CKAN registry says a mod installed, KSPBurst's generated library, images in the data folder, logs, and the
    player's settings.cfg (KSP writes a default one).

    The archive is 7z AES-256 with encrypted headers (file names too), split into volumes under GitHub's 2 GiB
    release-asset limit, and tested after it is written. A new random key goes to key.txt beside the volumes unless -Key
    is given: it is the only copy, so put it in the repo secret and then delete the file.

    Nothing is uploaded. The gh commands to do it are printed at the end.

.EXAMPLE
    .\Pack-KspGame.ps1 -Out D:\ksp-ci
    Windows build from the Steam install.

.EXAMPLE
    .\Pack-KspGame.ps1 -DockerVolume ksptest-ksp -Out D:\ksp-ci -Key (Get-Content D:\ksp-ci\key.txt)
    Linux build from KspTest's Docker volume (docker/README.md), with the same key as the Windows one.
#>
[CmdletBinding()]
param(
    # The KSP install to take the stock files from (ignored with -DockerVolume)
    [string]$Source = 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program',

    # A Docker volume holding KSP (KspTest's ksptest-ksp has the Linux build): copied out to a staging folder first
    [string]$DockerVolume,

    # Where the volumes (and key.txt) go
    [Parameter(Mandatory)][string]$Out,

    # Also pack GameData/SquadExpansion (Making History, Breaking Ground)
    [switch]$IncludeDlc,

    # The archive key; a new random one when not given
    [string]$Key,

    # Only list what would be packed (and write the list file): no archive, no key
    [switch]$ListOnly,

    [string]$VolumeSize = '1900m',
    [string]$SevenZip = 'C:\Program Files\7-Zip\7z.exe',

    # The private repo the volumes go to, and the repo whose CI gets the secrets (for the printed commands)
    [string]$GameRepo = 'chambem42/ksp-ci-assets',
    [string]$CiRepo = 'chambem42/Ferram-Aerospace-Research'
)

$ErrorActionPreference = 'Stop'

# StockBase.cs's lists, less settings.cfg (the player's)
$rootFiles = 'KSP_x64.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'KSP.x86_64', 'UnityPlayer.so',
             'buildID.txt', 'buildID64.txt', 'readme.txt', 'LegalNotice.txt', 'Physics.cfg'
$bigDirs = @('KSP_x64_Data', 'KSP_Data', 'MonoBleedingEdge', 'GameData/Squad')
if ($IncludeDlc) { $bigDirs += 'GameData/SquadExpansion' }
$smallDirs = 'Internals', 'Missions', 'Resources', 'sounds', 'Ships/VAB', 'Ships/SPH', 'Ships/@thumbs/VAB',
             'Ships/@thumbs/SPH', 'saves/scenarios', 'saves/training'

# Not stock, wherever a mod or the player left them (relative paths, '/' separated)
$junk = @(
    '^KSP(_x64)?_Data/Plugins/(x86_64/)?lib_burst_generated',   # KSPBurst's compiled jobs
    '^KSP(_x64)?_Data/[^/]+\.(png|jpg)$',                        # screenshots some mods save there
    '\.log$'
)

if (-not (Test-Path $SevenZip)) { throw "no 7-Zip at $SevenZip (-SevenZip)" }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

$staging = $null
if ($DockerVolume) {
    $staging = Join-Path $Out "staging-$DockerVolume"
    if (Test-Path $staging) { throw "$staging exists: delete it first" }
    New-Item -ItemType Directory $staging | Out-Null
    Write-Host "copying volume $DockerVolume to $staging ..."
    # -L: real files on the Windows side; the execute bit is lost (the workflow sets it again)
    docker run --rm -v "${DockerVolume}:/ksp:ro" -v "${staging}:/out" alpine sh -c 'cp -rL /ksp/. /out/'
    if ($LASTEXITCODE -ne 0) { throw "docker copy failed ($LASTEXITCODE)" }
    $Source = $staging
}
$Source = (Resolve-Path $Source).Path

if (Test-Path (Join-Path $Source 'KSP_x64.exe')) { $platform = 'windows' }
elseif (Test-Path (Join-Path $Source 'KSP.x86_64')) { $platform = 'linux' }
else { throw "no KSP_x64.exe or KSP.x86_64 in $Source" }

$buildFile = @('buildID64.txt', 'buildID.txt') | ForEach-Object { Join-Path $Source $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
$build = if ($buildFile) { (Select-String -Path $buildFile -Pattern 'build id = (\d+)').Matches[0].Groups[1].Value } else { '?' }
$version = switch ($build) { '03190' { '1.12.5' } default { "build$build" } }
if ($build -ne '03190') { Write-Warning "build id $build is not KSP 1.12.5's (03190)" }
$name = "ksp-$version-$platform"

$existing = Get-ChildItem $Out -Filter "$name.7z*" -ErrorAction SilentlyContinue
if ($existing) { throw "$Out already has $name.7z volumes: move or delete them first" }

# CKAN's installed files: never stock, even inside a stock folder
$ckanOwned = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$registry = Join-Path $Source 'CKAN/registry.json'
if (Test-Path $registry) {
    $reg = Get-Content $registry -Raw | ConvertFrom-Json
    if ($reg.installed_files) {
        foreach ($p in $reg.installed_files.PSObject.Properties) { [void]$ckanOwned.Add($p.Name.Replace('\', '/')) }
    }
}

$files = New-Object System.Collections.Generic.List[string]
$left = @{ ckan = 0; junk = 0 }
$rootLen = $Source.TrimEnd('\', '/').Length + 1
function Add-File([string]$rel) {
    if ($ckanOwned.Contains($rel)) { $left.ckan++; return }
    foreach ($j in $junk) { if ($rel -match $j) { $left.junk++; return } }
    $files.Add($rel)
}

foreach ($f in $rootFiles) { if (Test-Path -LiteralPath (Join-Path $Source $f)) { Add-File $f } }
foreach ($d in $bigDirs + $smallDirs) {
    $full = Join-Path $Source $d
    if (-not (Test-Path -LiteralPath $full)) { continue }
    Get-ChildItem -LiteralPath $full -Recurse -File -Force | ForEach-Object {
        Add-File ($_.FullName.Substring($rootLen).Replace('\', '/'))
    }
}

$bytes = ($files | ForEach-Object { (Get-Item -LiteralPath (Join-Path $Source $_) -Force).Length } | Measure-Object -Sum).Sum
Write-Host ("{0}: {1} files, {2:N2} GiB from {3} ({4} CKAN-installed and {5} non-stock files left out)" -f
    $name, $files.Count, ($bytes / 1GB), $Source, $left.ckan, $left.junk)

# Crafts in the stock Ships folders: the player's own come along too. Look them over.
$crafts = $files | Where-Object { $_ -match '^Ships/(VAB|SPH)/[^/]+\.craft$' } | ForEach-Object { $_.Substring(6) }
if ($crafts) { Write-Host ("crafts: " + ($crafts -join ', ')) }

if ($ListOnly) {
    $list = Join-Path $Out "$name.files.txt"
    [IO.File]::WriteAllLines($list, $files, (New-Object Text.UTF8Encoding $false))
    Write-Host "list only: $list"
    if ($staging) { Remove-Item -Recurse -Force $staging }
    return
}

$freeGiB = (Get-PSDrive ($Out.Substring(0, 1))).Free / 1GB
if ($freeGiB -lt ($bytes / 1GB) + 1) { throw ("{0:N1} GiB free on {1}: not enough" -f $freeGiB, $Out.Substring(0, 2)) }

$newKey = -not $Key
if ($newKey) {
    $rnd = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($rnd)
    $Key = -join ($rnd | ForEach-Object { $_.ToString('x2') })
}

$list = Join-Path $Out "$name.files.txt"
[IO.File]::WriteAllLines($list, $files, (New-Object Text.UTF8Encoding $false))
$archive = Join-Path $Out "$name.7z"

Push-Location $Source
try {
    Write-Host "packing into $archive (volumes of $VolumeSize) ..."
    & $SevenZip a -t7z -mx=5 -mhe=on "-p$Key" "-v$VolumeSize" -scsUTF-8 -bso0 -bsp1 $archive "@$list"
    if ($LASTEXITCODE -ne 0) { throw "7z failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

Write-Host 'testing the archive ...'
& $SevenZip t "-p$Key" -bso0 -bsp1 "$archive.001"
if ($LASTEXITCODE -ne 0) { throw "7z test failed ($LASTEXITCODE)" }

if ($newKey) {
    $keyFile = Join-Path $Out 'key.txt'
    [IO.File]::WriteAllText($keyFile, $Key)
}
if ($staging) { Remove-Item -Recurse -Force $staging }

$volumes = Get-ChildItem $Out -Filter "$name.7z.*" | Sort-Object Name
Write-Host ''
$volumes | ForEach-Object { Write-Host ("  {0}  {1:N2} GiB" -f $_.Name, ($_.Length / 1GB)) }
Write-Host "  $name.files.txt  (what is in it; not needed by CI)"
if ($newKey) { Write-Host "  key.txt  (the only copy of the key)" }

$vols = ($volumes | ForEach-Object { '"' + $_.FullName + '"' }) -join ' '
Write-Host @"

Next, by hand:
  # once: the private repo the volumes live in
  gh repo create $GameRepo --private --add-readme --description "CI game files (encrypted)"
  # the volumes, as a release
  gh release create $name $vols -R $GameRepo --title $name --notes "encrypted"
  # the key, for $CiRepo's workflows only
  Get-Content "$Out\key.txt" | gh secret set KSP_GAME_KEY -R $CiRepo
  # a fine-grained token, Contents: read-only on $GameRepo (and chambem42/KspTest if it is private)
  gh secret set KSP_GAME_TOKEN -R $CiRepo
Then delete key.txt (keep the key in a password manager if you want to repack with it).
"@
