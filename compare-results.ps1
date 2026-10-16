<#
.SYNOPSIS
  Diffs the recorded values of two test runs (results\<dir>\result.json), for A/B comparison of a mod build:
  every key present in both, with the absolute and relative difference, sorted by relative difference. Keys with a
  suffix in -Noise (defaults to the flown steady-state means, which scatter run to run) are reported separately.

.EXAMPLE
  .\compare-results.ps1 results\20260920-190000-Far_Regress_Editor results\20260920-193000-Far_Regress_Editor
  .\compare-results.ps1 $a $b -RelTol 1e-6 -Csv out.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $A,
    [Parameter(Mandatory = $true)] [string] $B,
    [double] $RelTol = 1e-9,
    [double] $AbsTol = 1e-9,
    [string] $Csv = '',
    [int] $Show = 40
)

function Load($dir) {
    $f = Join-Path $dir 'result.json'
    if (-not (Test-Path $f)) { throw "no result.json in $dir" }
    $j = Get-Content $f -Raw | ConvertFrom-Json
    $vals = @{}
    foreach ($t in $j.tests) {
        $prefix = if ($j.tests.Count -gt 1) { "$($t.test)." } else { '' }
        if ($t.values) {
            foreach ($p in $t.values.PSObject.Properties) { $vals[$prefix + $p.Name] = $p.Value }
        }
    }
    return @{ status = $j.status; tests = @($j.tests | ForEach-Object { "$($_.test)=$($_.status)" }); values = $vals }
}

$ra = Load $A
$rb = Load $B
Write-Host "A: $A  [$($ra.status): $($ra.tests -join ', ')]"
Write-Host "B: $B  [$($rb.status): $($rb.tests -join ', ')]"
if ($ra.values['farBuild']) { Write-Host "A farBuild: $($ra.values['farBuild'])" }
if ($rb.values['farBuild']) { Write-Host "B farBuild: $($rb.values['farBuild'])" }

$rows = @()
$onlyA = @(); $onlyB = @(); $textDiff = @()
foreach ($k in ($ra.values.Keys + $rb.values.Keys | Sort-Object -Unique)) {
    if ($k -eq 'farBuild') { continue }
    $ina = $ra.values.ContainsKey($k); $inb = $rb.values.ContainsKey($k)
    if ($ina -and -not $inb) { $onlyA += $k; continue }
    if ($inb -and -not $ina) { $onlyB += $k; continue }
    $va = $ra.values[$k]; $vb = $rb.values[$k]
    $da = 0.0; $db = 0.0
    $na = [double]::TryParse([string]$va, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$da)
    $nb = [double]::TryParse([string]$vb, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$db)
    if (-not ($na -and $nb)) {
        if ([string]$va -ne [string]$vb) { $textDiff += "$k : '$va' -> '$vb'" }
        continue
    }
    $abs = [math]::Abs($db - $da)
    $scale = [math]::Max([math]::Abs($da), [math]::Abs($db))
    $rel = if ($scale -gt 0) { $abs / $scale } else { 0.0 }
    $rows += [pscustomobject]@{ key = $k; a = $da; b = $db; abs = $abs; rel = $rel }
}

$flown = $rows | Where-Object { $_.key -match '\.hold-?\d+\.' }
$exact = $rows | Where-Object { $_.key -notmatch '\.hold-?\d+\.' }
$changed = $exact | Where-Object { $_.abs -gt $AbsTol -and $_.rel -gt $RelTol }

Write-Host ""
Write-Host "deterministic keys compared: $($exact.Count); differing beyond tol (abs>$AbsTol and rel>$RelTol): $($changed.Count)"
if ($changed.Count -gt 0) {
    $changed | Sort-Object rel -Descending | Select-Object -First $Show | Format-Table -AutoSize key, @{n='A';e={$_.a}}, @{n='B';e={$_.b}}, @{n='abs';e={'{0:G4}' -f $_.abs}}, @{n='rel';e={'{0:G4}' -f $_.rel}} | Out-String -Width 220 | Write-Host
    # group the changes by their first two key segments so the shape of a difference is visible
    $changed | Group-Object { ($_.key -split '\.')[0..1] -join '.' } | Sort-Object Count -Descending | ForEach-Object {
        $mx = ($_.Group | Measure-Object rel -Maximum).Maximum
        Write-Host ("  {0,-40} {1,5} changed, max rel {2:G3}" -f $_.Name, $_.Count, $mx)
    }
}
if ($flown.Count -gt 0) {
    Write-Host ""
    Write-Host "flown steady-state keys (run-to-run scatter expected): $($flown.Count)"
    $flown | Where-Object { $_.key -match '(Ratio|aoa|Cl|Cd|LD|deflectDeg|pitchInput|appPitchM|pitchMDiff|samples|mach|qkPa)$' } | Sort-Object key |
        Format-Table -AutoSize key, @{n='A';e={$_.a}}, @{n='B';e={$_.b}}, @{n='rel';e={'{0:G3}' -f $_.rel}} | Out-String -Width 220 | Write-Host
}
if ($onlyA.Count -gt 0) { Write-Host "only in A ($($onlyA.Count)): $(($onlyA | Select-Object -First 10) -join ', ')$(if ($onlyA.Count -gt 10) { ' ...' })" }
if ($onlyB.Count -gt 0) { Write-Host "only in B ($($onlyB.Count)): $(($onlyB | Select-Object -First 10) -join ', ')$(if ($onlyB.Count -gt 10) { ' ...' })" }
if ($textDiff.Count -gt 0) { Write-Host "text values differing:"; $textDiff | ForEach-Object { Write-Host "  $_" } }
if ($Csv) { $rows | Sort-Object key | Export-Csv -NoTypeInformation -Path $Csv; Write-Host "written $Csv" }
