<#
.SYNOPSIS
  Runs one or more in-game KspTest tests in a single KSP session: resets the test save, writes run.cfg, launches KSP, waits for
  it to exit, collects result.json / screenshots / KSP.log and returns a non-zero exit code on failure.

.EXAMPLE
  .\run-test.ps1 -Test Smoke_MainMenuToVab
  .\run-test.ps1 -Test Kct_SimConfig_Reentry -Install D:\Games\KSP-RP1-Stock -Save rp1-career
  .\run-test.ps1 -Test list                      # just discover the tests deployed in that install
  .\run-test.ps1 -Test "Smoke_MainMenuToVab,Launch_StockButton,Popup_Dismiss"   # several per session
  .\run-test.ps1 -Test "Mj_*" -Params "orbitAltKm=120;inclination=30"           # wildcard, string params
  .\run-test.ps1 -Test all -Keep
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Test,
    [string] $Install = 'D:\Games\KSP-Vanilla',
    # golden save template under KspTest\saves\<Save>, copied fresh into <Install>\saves\<SaveFolder>
    [string] $Save = 'stock-sandbox',
    [string] $SaveFolder = 'ksptest',
    [switch] $NoReset,          # reuse <Install>\saves\<SaveFolder> as-is
    [int] $TimeoutSec = 900,
    [switch] $Keep,             # leave KSP open after the test (quitWhenDone = false)
    [int] $Width = 1920,
    [int] $Height = 1080,
    # hashtable, or a string "key=value;key=value" (the only form that survives powershell -File)
    [object] $Params = @{},
    [string] $ResultsRoot = '',
    # a KSP whose load goes quiet this long before the first test starts - KSP.log not written, and KSPCF's texture
    # cache not written either - is killed and started again, up to LoadRetries times. A modded install now and then
    # hangs in the texture load; one hung 52 min "responding", so the window's state is not asked. A healthy load of the
    # RP-1 install never went more than 19 s without a KSP.log line (30 loads); the long quiet one, KSPCF's FastLoader
    # rebuilding its texture cache (up to 11 min), writes the cache as it goes.
    [int] $LoadStallSec = 120,
    [int] $LoadRetries = 2,
    # once the tests run: KSP.log and the harness's log both silent this long is a frozen game, killed (the harness's
    # own timeouts cannot fire in a frozen game, and the hard timeout is the test's whole budget)
    [int] $RunStallSec = 600,
    # bdwgc's GC_FREE_SPACE_DIVISOR for this KSP (0: leave it unset). 1 trades heap for about 4x fewer collections.
    [int] $GcDivisor = 0,
    # the install's MASTER_VOLUME at 0 for the run, the player's put back after it
    [switch] $Mute,
    # how long to wait for another KSP (any install) or another run holding ksp.lock to finish before giving up
    [int] $WaitForOtherKspSec = 7200
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ResultsRoot) { $ResultsRoot = Join-Path $here 'results' }
$Install = [IO.Path]::GetFullPath($Install)
$exe = Join-Path $Install 'KSP_x64.exe'
if (-not (Test-Path $exe)) { throw "KSP_x64.exe not found under $Install" }

# --- refuse to run while that install is already running (never kill a foreign instance) ---
$running = Get-Process -Name KSP_x64 -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ($_.Path -like "$Install\*") } catch { $false }
}
# (it is waited for below, like any running KSP: queued runs of the same install follow each other)
if ($running) { Write-Host "KSP is already running from $Install (PID $($running.Id -join ',')); waiting for it to exit" }

$harnessDll = Join-Path $Install 'GameData\KspTest\Plugins\KspTest.dll'
if (-not (Test-Path $harnessDll)) { throw "harness not deployed into $Install (run deploy.ps1 -Install '$Install')" }

# --- one KSP at a time on this machine (two modded RSS games at once ran the GPU out of memory): wait while any KSP
# runs, from any install, or while another run holds ksp.lock (beside this folder, shared with whoever else launches
# KSP here), then hold the lock for this run. A lock whose holder process is gone is stale and taken over. ---
$lockFile = Join-Path (Split-Path $here -Parent) 'ksp.lock'
$lockHeld = $false
# a script that holds the lock over several KSP runs may call this one: a lock held by one of its parents is its own
$ancestors = @()
$p = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction SilentlyContinue
while ($p -and $p.ParentProcessId -and $ancestors.Count -lt 16) {
    $ancestors += [int]$p.ParentProcessId
    $p = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.ParentProcessId)" -ErrorAction SilentlyContinue
}
$lockedByParent = $false
$waitDeadline = (Get-Date).AddSeconds($WaitForOtherKspSec)
$nextWaitNote = Get-Date
while (-not $lockHeld) {
    $holder = $null
    if (Test-Path $lockFile) {
        $text = Get-Content $lockFile -Raw -ErrorAction SilentlyContinue
        # a lock written by hand without a pid= is held until it is 12 hours old
        $live = if ($text -match 'pid=(\d+)') { [bool](Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue) }
                else { ((Get-Date) - (Get-Item $lockFile).LastWriteTime).TotalHours -lt 12 }
        if ($live) { $holder = "$text".Trim() }
        else { Remove-Item $lockFile -Force -ErrorAction SilentlyContinue }
        if ($live -and $text -match 'pid=(\d+)' -and $ancestors -contains [int]$Matches[1]) { $lockedByParent = $true }
    }
    $others = @(Get-Process -Name KSP_x64 -ErrorAction SilentlyContinue)
    # the parent's lock is this run's: go ahead once no KSP runs (and leave the lock to the parent)
    if ($lockedByParent -and $others.Count -eq 0) { break }
    $lockedByParent = $false
    if (-not $holder -and $others.Count -eq 0) {
        try {
            # CreateNew fails if another run created it since the check
            $fs = [IO.File]::Open($lockFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            $bytes = [Text.Encoding]::ASCII.GetBytes("pid=$PID install=$Install test=$Test started=$(Get-Date -Format s)")
            $fs.Write($bytes, 0, $bytes.Length)
            $fs.Close()
            $lockHeld = $true
            break
        } catch [IO.IOException] { }
    }
    if ((Get-Date) -gt $waitDeadline) {
        throw "gave up after $WaitForOtherKspSec s waiting for other KSP runs (lock: $holder; KSP PIDs: $($others.Id -join ','))"
    }
    if ((Get-Date) -ge $nextWaitNote) {
        $why = if ($holder) { "lock held ($holder)" } else { "KSP running: $(($others | ForEach-Object { try { $_.Path } catch { $_.Id } }) -join ', ')" }
        Write-Host "waiting for another KSP run to finish: $why"
        $nextWaitNote = (Get-Date).AddMinutes(5)
    }
    Start-Sleep -Seconds 15
}

if ($Params -is [string]) {
    $h = @{}
    foreach ($kv in ($Params -split '[;,]')) { if ($kv -match '^\s*([^=]+?)\s*=\s*(.*?)\s*$') { $h[$Matches[1]] = $Matches[2] } }
    $Params = $h
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$label = (($Test -split '[,;]' | ForEach-Object { $_.Trim() }) -join '+') -replace '[^A-Za-z0-9_+*.-]', '_'
if ($label.Length -gt 60) { $label = $label.Substring(0, 60) }
$resultsDir = Join-Path $ResultsRoot "$stamp-$label"
New-Item -ItemType Directory -Force $resultsDir | Out-Null

# --- golden save reset ---
$saveDst = Join-Path $Install "saves\$SaveFolder"
if (-not $NoReset -and $Test -ne 'list') {
    $saveSrc = Join-Path $here "saves\$Save"
    if (-not (Test-Path (Join-Path $saveSrc 'persistent.sfs'))) {
        throw "golden save template not found: $saveSrc\persistent.sfs (put a save + Ships\VAB\*.craft there, or use -NoReset)"
    }
    # A craft or subassembly the template does not have, or has different, is someone's work (one saved while playing in
    # this save went 13 s before a reset deleted it): then every file that differs from the template is copied to
    # save-backups\<folder>-<time>\ first, same layout. persistent.sfs and the like change every run and alone are not.
    if (Test-Path $saveDst) {
        $differ = @()
        foreach ($f in Get-ChildItem $saveDst -Recurse -File) {
            $rel = $f.FullName.Substring($saveDst.Length).TrimStart('\')
            $tpl = Join-Path $saveSrc $rel
            if ((Test-Path $tpl) -and (Get-FileHash $tpl -Algorithm MD5).Hash -eq (Get-FileHash $f.FullName -Algorithm MD5).Hash) { continue }
            $differ += $rel
        }
        # not work: KSP's Auto-Saved Ship, its .loadmeta beside every craft, and the .original a mod leaves when it
        # rewrites a craft on load
        # a craft that left an .original matching the template was rewritten by the game on load (made on another install,
        # its _modVersions restamped), not by anyone
        $rewritten = { param($rel) $orig = Join-Path $saveDst "$rel.original"; $tpl = Join-Path $saveSrc $rel
                       (Test-Path $orig) -and (Test-Path $tpl) -and (Get-FileHash $orig -Algorithm MD5).Hash -eq (Get-FileHash $tpl -Algorithm MD5).Hash }
        $work = @($differ | Where-Object { $_ -match '^(Ships|Subassemblies)\\' -and $_ -notmatch '\\Auto-Saved Ship\.craft$' -and
                                            $_ -notmatch '^Ships\\temp\.craft$' -and
                                            $_ -notmatch '\.(loadmeta|original)$' -and -not (& $rewritten $_) })
        if ($work.Count -gt 0) {
            $backup = Join-Path $here ("save-backups\{0}-{1}" -f $SaveFolder, (Get-Date -Format 'yyyyMMdd-HHmmss'))
            foreach ($rel in $differ) {
                $to = Join-Path $backup $rel
                New-Item -ItemType Directory -Force (Split-Path $to -Parent) | Out-Null
                Copy-Item (Join-Path $saveDst $rel) $to
            }
            Write-Host "save reset: kept $($differ.Count) file(s) that differ from the template ($($work -join ', ')) in $backup"
        }
        Remove-Item -Recurse -Force $saveDst
    }
    Copy-Item -Recurse $saveSrc $saveDst
    Write-Host "save reset: $saveSrc -> $saveDst"
}

# --- QuickStart would race the harness: disable it for this run if present ---
$qsCfg = Join-Path $Install 'GameData\QuickMods\QuickStart\Config.txt'
$qsBackup = $null
if (Test-Path $qsCfg) {
    $qsBackup = Get-Content $qsCfg -Raw
    $disabled = [regex]::Replace($qsBackup, '(?m)^(\s*Enabled\s*=\s*)True\s*$', '${1}False')
    if ($disabled -eq $qsBackup -and $qsBackup -notmatch '(?m)^\s*Enabled\s*=') { $disabled = "Enabled = False`r`n" + $qsBackup }
    Set-Content -Path $qsCfg -Value $disabled -Encoding ASCII -NoNewline
    Write-Host "QuickStart disabled for this run"
}

# --- run manifest ---
$runCfgDir = Join-Path $Install 'GameData\KspTest\PluginData'
New-Item -ItemType Directory -Force $runCfgDir | Out-Null
$runCfg = Join-Path $runCfgDir 'run.cfg'
$paramLines = ($Params.GetEnumerator() | ForEach-Object { "        $($_.Key) = $($_.Value)" }) -join "`r`n"
$quit = if ($Keep) { 'false' } else { 'true' }
$runCfgText = @"
KSPUITEST_RUN
{
    test = $Test
    save = $SaveFolder
    resultsDir = $resultsDir
    timeoutSec = $TimeoutSec
    quitWhenDone = $quit
    width = $Width
    height = $Height
    PARAMS
    {
$paramLines
    }
}
"@

# --- Unity's own window prefs (one registry key shared by every KSP install): the run's size, windowed, set before the
# launch, and the player's put back after it. Unity writes the window it had on exit, so a KSP that crashed or closed
# minimized leaves something like 160x28 there and the next game of any install opens that small; the command line alone
# did not override it. What was there is restored unless it is such a size, then the install's settings.cfg size. ---
$screenKey = 'HKCU:\Software\Squad\Kerbal Space Program'
$screenNames = @{ width = 'Screenmanager Resolution Width_h182942802'; height = 'Screenmanager Resolution Height_h2627697771'
                  mode = 'Screenmanager Fullscreen mode_h3630240806'; native = 'Screenmanager Resolution Use Native_h1405027254' }
$screenWas = @{}
if (-not (Test-Path $screenKey)) { New-Item -Path $screenKey -Force | Out-Null }
$prefs = Get-ItemProperty $screenKey
foreach ($k in $screenNames.Keys) { $screenWas[$k] = $prefs.($screenNames[$k]) }
# Fullscreen mode: 0 exclusive, 1 fullscreen window, 2 maximized window, 3 windowed
foreach ($kv in @{ width = $Width; height = $Height; mode = 3; native = 0 }.GetEnumerator()) {
    Set-ItemProperty $screenKey $screenNames[$kv.Key] $kv.Value -Type DWord
}

# --- mute: KSP reads MASTER_VOLUME from settings.cfg at startup and writes it back on exit, so the player's value is put
# back once KSP has exited ---
$settingsCfg = Join-Path $Install 'settings.cfg'
$volumeWas = $null
if ($Mute -and (Test-Path $settingsCfg)) {
    $text = [IO.File]::ReadAllText($settingsCfg)
    $m = [regex]::Match($text, '(?m)^MASTER_VOLUME\s*=\s*(\S+)')
    if ($m.Success) {
        $volumeWas = $m.Groups[1].Value
        [IO.File]::WriteAllText($settingsCfg, [regex]::Replace($text, '(?m)^MASTER_VOLUME\s*=\s*\S+', 'MASTER_VOLUME = 0'))
        Write-Host "muted (MASTER_VOLUME was $volumeWas)"
    }
}

$proc = $null
$status = 'unknown'
try {
    # --- launch (a silent Start-Process is success) ---
    $kspArgs = @('-screen-fullscreen', '0', '-screen-width', "$Width", '-screen-height', "$Height", '-popupwindow')
    $kspLogLive = Join-Path $Install 'KSP.log'
    # KSPCF's FastLoader writes a .dds here per texture while it rebuilds its cache (a long load with KSP.log quiet)
    $kspcfCache = Join-Path $Install 'GameData\KSPCommunityFixes\PluginData\TextureCache'
    $attempt = 0
    do {
    $loadHung = $false
    # bdwgc reads its settings from the environment at startup: set for the child only
    $gcDivisorWas = $env:GC_FREE_SPACE_DIVISOR
    if ($GcDivisor -gt 0) { $env:GC_FREE_SPACE_DIVISOR = "$GcDivisor" }
    # the harness deletes run.cfg as it reads it (a run it did not live to clean up after must not start again), so it is
    # written for every start: a retry after a load hang would otherwise start no test
    Set-Content -Path $runCfg -Value $runCfgText -Encoding ASCII
    try { $proc = Start-Process -FilePath $exe -WorkingDirectory $Install -ArgumentList $kspArgs -PassThru }
    finally { $env:GC_FREE_SPACE_DIVISOR = $gcDivisorWas }
    Write-Host "KSP started (PID $($proc.Id)); waiting up to $TimeoutSec s for $Test ..."

    # the harness has its own timeout; ours is a safety net for hangs before the harness even loads
    $hardDeadline = (Get-Date).AddSeconds($TimeoutSec + 120)
    # once result.json is written the harness has asked KSP to quit; a modded KSP can hang in teardown (mod OnDestroy
    # exceptions), so give it a grace period and then kill it rather than wait out the whole timeout
    $quitDeadline = $null
    $begun = $false
    $nextLoadCheck = (Get-Date).AddSeconds(10)
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        # stalls, checked every 10 s. Before the first test starts (not the harness's BEGIN: it is logged at the start of
        # loading, and a hang in the texture load came after it): KSP.log and KSPCF's texture cache both untouched for
        # LoadStallSec is a load hang, retried. Once the tests run: KSP.log and harness.log both untouched for
        # RunStallSec is a frozen game.
        if ((Get-Date) -gt $nextLoadCheck) {
            $nextLoadCheck = (Get-Date).AddSeconds(10)
            $lastWrite = $proc.StartTime
            $watched = @($kspLogLive)
            if ($begun) { $watched += (Join-Path $resultsDir 'harness.log') } else { $watched += $kspcfCache }
            foreach ($path in $watched) {
                if ($path -and (Test-Path $path)) {
                    $w = (Get-Item $path).LastWriteTime
                    if ($w -gt $lastWrite) { $lastWrite = $w }
                }
            }
            if (-not $begun -and (Test-Path $kspLogLive) -and (Get-Item $kspLogLive).LastWriteTime -gt $proc.StartTime -and
                (Select-String -Path $kspLogLive -Pattern '[KspTest] ==== TEST' -SimpleMatch -Quiet)) { $begun = $true }
            $quiet = ((Get-Date) - $lastWrite).TotalSeconds
            if (-not $begun -and $quiet -gt $LoadStallSec) {
                Write-Warning "KSP hung while loading (KSP.log silent since $($lastWrite.ToString('HH:mm:ss'))); killing PID $($proc.Id)"
                Stop-Process -Id $proc.Id -Force
                $proc.WaitForExit(30000) | Out-Null
                if (Test-Path $kspLogLive) { Copy-Item $kspLogLive (Join-Path $resultsDir "KSP.loadhang$($attempt + 1).log") -ErrorAction SilentlyContinue }
                $loadHung = $true
                $status = 'load-hang'
                break
            }
            if ($begun -and -not $finished -and $quiet -gt $RunStallSec) {
                Write-Warning "KSP froze during the test (KSP.log and harness.log silent since $($lastWrite.ToString('HH:mm:ss'))); killing PID $($proc.Id)"
                Stop-Process -Id $proc.Id -Force
                $status = 'frozen'
                break
            }
        }
        # result.json is rewritten after every test; only a final status means the harness has asked KSP to quit
        $rj = Join-Path $resultsDir 'result.json'
        $finished = (Test-Path $rj) -and ((Get-Content $rj -Raw -ErrorAction SilentlyContinue) -notmatch '"status":\s*"running"')
        if ($finished) {
            if ($Keep) { break }   # test done, KSP left open
            if ($quitDeadline -eq $null) { $quitDeadline = (Get-Date).AddSeconds(60) }
            elseif ((Get-Date) -gt $quitDeadline) {
                Write-Warning "KSP did not exit within 60 s of writing result.json; killing PID $($proc.Id)"
                Stop-Process -Id $proc.Id -Force
                break
            }
        }
        if ((Get-Date) -gt $hardDeadline) {
            Write-Warning "hard timeout: killing KSP PID $($proc.Id)"
            Stop-Process -Id $proc.Id -Force
            $status = 'timeout'
            break
        }
    }
    if ($loadHung -and $attempt -lt $LoadRetries) { Write-Host "starting KSP again ($($attempt + 1) of $LoadRetries retries)" }
    } while ($loadHung -and $attempt++ -lt $LoadRetries)
}
finally {
    if ($qsBackup -ne $null) { Set-Content -Path $qsCfg -Value $qsBackup -Encoding ASCII -NoNewline; Write-Host "QuickStart config restored" }
    if (Test-Path $runCfg) { Remove-Item $runCfg -Force }
    # the player's window prefs back, once KSP has written its own on exit (left alone while a -Keep KSP still runs)
    if ($proc -ne $null -and -not $Keep) { $proc.WaitForExit(15000) | Out-Null }
    if ($proc -eq $null -or $proc.HasExited) {
        $restore = $screenWas.Clone()
        if (-not ($restore.width -ge 640 -and $restore.height -ge 480)) {
            $cfg = Get-Content (Join-Path $Install 'settings.cfg') -ErrorAction SilentlyContinue
            $w = ($cfg | Select-String '^\s*SCREEN_RESOLUTION_WIDTH\s*=\s*(\d+)' | Select-Object -First 1).Matches.Groups[1].Value
            $h = ($cfg | Select-String '^\s*SCREEN_RESOLUTION_HEIGHT\s*=\s*(\d+)' | Select-Object -First 1).Matches.Groups[1].Value
            $full = ($cfg | Select-String '^\s*FULLSCREEN\s*=\s*True' -Quiet)
            $restore.width = if ($w) { [int]$w } else { 1920 }
            $restore.height = if ($h) { [int]$h } else { 1080 }
            $restore.mode = if ($full) { 1 } else { 3 }
        }
        foreach ($k in $screenNames.Keys) {
            if ($restore[$k] -ne $null) { Set-ItemProperty $screenKey $screenNames[$k] $restore[$k] -Type DWord }
        }
        Write-Host "window prefs restored: $($restore.width)x$($restore.height) mode $($restore.mode)"
        if ($volumeWas -ne $null) {
            $text = [IO.File]::ReadAllText($settingsCfg)
            [IO.File]::WriteAllText($settingsCfg, [regex]::Replace($text, '(?m)^MASTER_VOLUME\s*=\s*\S+', "MASTER_VOLUME = $volumeWas"))
            Write-Host "MASTER_VOLUME restored: $volumeWas"
        }
    }
    $kspLog = Join-Path $Install 'KSP.log'
    if (Test-Path $kspLog) { Copy-Item $kspLog (Join-Path $resultsDir 'KSP.log') -ErrorAction SilentlyContinue }
    # the lock goes once this KSP is gone (a -Keep KSP still running keeps others waiting by being a running KSP)
    if ($lockHeld -and (Get-Content $lockFile -Raw -ErrorAction SilentlyContinue) -match "pid=$PID\b") {
        Remove-Item $lockFile -Force -ErrorAction SilentlyContinue
    }
}

# --- report ---
$resultFile = Join-Path $resultsDir 'result.json'
if (Test-Path $resultFile) {
    $r = Get-Content $resultFile -Raw | ConvertFrom-Json
    $status = $r.status
    Write-Host ""
    foreach ($t in $r.tests) {
        Write-Host "==== $($t.test): $($t.status.ToUpper())  ($($t.durationSec)s) ===="
        if ($t.message) { Write-Host $t.message }
        foreach ($s in $t.steps) { Write-Host ("  [{0,-7}] {1,6:N1}s  {2}" -f $s.status, $s.sec, $s.name) }
        if ($t.values) { $t.values.PSObject.Properties | ForEach-Object { Write-Host "  $($_.Name) = $($_.Value)" } }
        if ($t.screenshots) { Write-Host "  screenshots: $($t.screenshots -join ', ')" }
    }
    Write-Host ""
    Write-Host "==== RUN: $($status.ToUpper())  ($($r.durationSec)s total, $(@($r.tests).Count) tests, ended in $($r.scene)) ===="
    # exceptions logged by anything while the test ran (between the harness BEGIN/END markers)
    $logCopy = Join-Path $resultsDir 'KSP.log'
    if (Test-Path $logCopy) {
        $inRun = $false; $exc = @()
        foreach ($line in [IO.File]::ReadLines($logCopy)) {
            if ($line -match '\[KspTest\] BEGIN') { $inRun = $true }
            elseif ($inRun -and $line -match 'Exception') { $exc += $line }
            if ($line -match '\[KspTest\] END') { break }
        }
        if ($exc.Count -gt 0) {
            Write-Host "  exceptions in KSP.log during the run: $($exc.Count) (first: $($exc[0].Trim()))"
            $exc | Set-Content (Join-Path $resultsDir 'exceptions.txt')
        }
    }
    Write-Host "  results: $resultsDir"
} else {
    if ($status -eq 'unknown') { $status = 'no-result' }
    Write-Warning "no result.json written (status: $status) - KSP crashed or the harness never started; see $resultsDir\KSP.log"
}

if ($status -in @('pass', 'skipped')) { exit 0 } else { exit 1 }
