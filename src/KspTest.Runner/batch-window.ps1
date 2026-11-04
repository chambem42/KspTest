# A taskbar entry for a KSP running in -batchmode, which has no window of its own. Started by KspTest's runner
# (BatchWindow.cs) with the run in its environment: KSP's icon and the run's name on the taskbar; in the window the
# install, which test of how many is running and its step, the results so far, how long it has run, and a button that
# stops KSP. It starts minimized (no focus taken) and closes itself when KSP exits, however it exits.
#   KSPTEST_WINDOW_PID      KSP's process id
#   KSPTEST_WINDOW_TITLE    the taskbar entry's title
#   KSPTEST_WINDOW_EXE      KSP's executable (its icon)
#   KSPTEST_WINDOW_INSTALL  the install KSP runs from
#   KSPTEST_WINDOW_HARNESS  the run's harness.log (the test, the step, the results)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$ksp = Get-Process -Id ([int]$env:KSPTEST_WINDOW_PID) -ErrorAction SilentlyContinue
if (-not $ksp) { exit }
$started = $ksp.StartTime
$harness = $env:KSPTEST_WINDOW_HARNESS

$form = New-Object System.Windows.Forms.Form
$form.Text = $env:KSPTEST_WINDOW_TITLE
$form.ClientSize = New-Object System.Drawing.Size(620, 170)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(60, 60)
$form.ShowInTaskbar = $true
$form.WindowState = 'Minimized'
try { $form.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon($env:KSPTEST_WINDOW_EXE) } catch { }

$text = New-Object System.Windows.Forms.Label
$text.Font = New-Object System.Drawing.Font('Consolas', 9)
$text.Location = New-Object System.Drawing.Point(10, 10)
$text.Size = New-Object System.Drawing.Size(600, 112)
$text.AutoEllipsis = $true
$form.Controls.Add($text)

$stop = New-Object System.Windows.Forms.Button
$stop.Text = 'Stop KSP'
$stop.Location = New-Object System.Drawing.Point(500, 130)
$stop.Size = New-Object System.Drawing.Size(110, 30)
$form.Controls.Add($stop)

$script:closing = $false
$script:read = 0L
$script:plan = 0
$script:test = ''
$script:testIndex = 0
$script:step = ''
$script:passed = 0
$script:failed = 0

# what the harness log says since the last look: the plan, the test running, its step, the results
function Read-Harness {
    if (-not $harness -or -not (Test-Path -LiteralPath $harness)) { return }
    try {
        $fs = [System.IO.File]::Open($harness, 'Open', 'Read', 'ReadWrite')
        try {
            if ($fs.Length -lt $script:read) { $script:read = 0L }
            $fs.Seek($script:read, 'Begin') | Out-Null
            $reader = New-Object System.IO.StreamReader($fs)
            $chunk = $reader.ReadToEnd()
            $script:read = $fs.Length
        } finally { $fs.Dispose() }
    } catch { return }
    foreach ($line in $chunk -split "`r?`n") {
        if ($line -match 'INFO  plan: (.*)$') { $script:plan = ($Matches[1] -split ',\s*').Count }
        elseif ($line -match '==== TEST (\S+)') { $script:test = $Matches[1]; $script:testIndex++; $script:step = '' }
        elseif ($line -match 'INFO  STEP (.*)$') { $script:step = $Matches[1] }
        elseif ($line -match 'RESULT \S+: (\w+)') { if ($Matches[1] -eq 'pass') { $script:passed++ } else { $script:failed++ } }
    }
}

function Show-Status {
    Read-Harness
    $el = (Get-Date) - $started
    $of = if ($script:plan -gt 0) { " of $($script:plan)" } else { '' }
    $now = if ($script:test) { "test $($script:testIndex)${of}: $($script:test)" } else { 'loading' }
    $lines = @(
        "KSP (PID $($ksp.Id)), running $('{0:hh\:mm\:ss}' -f $el)",
        "install: $env:KSPTEST_WINDOW_INSTALL",
        $now,
        $(if ($script:step) { "  step $($script:step)" } else { '' }),
        "passed $($script:passed), failed $($script:failed)"
    )
    $text.Text = ($lines -join "`r`n")
}

function Stop-Ksp {
    try { $ksp.Kill() } catch { }
}

$stop.Add_Click({
    $answer = [System.Windows.Forms.MessageBox]::Show($form, "Stop KSP (PID $($ksp.Id))? The run fails.", $form.Text, 'YesNo', 'Warning')
    if ($answer -eq 'Yes') { Stop-Ksp }
})

$form.Add_FormClosing({
    param($sender, $e)
    if ($script:closing) { return }
    $ksp.Refresh()
    if ($ksp.HasExited) { return }
    $answer = [System.Windows.Forms.MessageBox]::Show($form, "Stop KSP as well?`r`n`r`nYes: stop KSP (the run fails).`r`nNo: close this window, KSP runs on.", $form.Text, 'YesNoCancel', 'Question')
    if ($answer -eq 'Cancel') { $e.Cancel = $true }
    elseif ($answer -eq 'Yes') { Stop-Ksp }
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$timer.Add_Tick({
    $ksp.Refresh()
    if ($ksp.HasExited) {
        $script:closing = $true
        $timer.Stop()
        $form.Close()
        return
    }
    Show-Status
})

Show-Status
$timer.Start()
[System.Windows.Forms.Application]::Run($form)
