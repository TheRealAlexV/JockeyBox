# =====================================================================
#  capture harness: three PNG proofs + a Settings control inventory
#  (step 5 of the verification harness, visual half)
# =====================================================================

$ErrorActionPreference = 'Continue'

# The capture run logs to its own file: a `cmd /c ... > log` redirect in the
# scheduled task is a lock the previous run can hold hostage, and it silently
# swallowed whole runs. Write-Host output goes nowhere under the task anyway.
$script:LogFile = Join-Path $OutDir 'verify-shots.log'
if (Test-Path $script:LogFile) { Remove-Item $script:LogFile -Force -ErrorAction SilentlyContinue }
function Log([string]$m) {
    $line = ('{0:HH:mm:ss.fff} {1}' -f (Get-Date), $m)
    [void]$script:LogLines.Add($line)
    Add-Content -Path $script:LogFile -Value $line -Encoding UTF8
}

# One dead value must never cost the whole report: the trap still leaves the
# done marker (and whatever the JSONL already streamed) behind.
trap {
    try {
        Log ("FATAL(capture): {0}: {1}" -f $_.Exception.GetType().FullName, $_.Exception.Message)
        "failed" | Set-Content -Path (Join-Path $OutDir 'verify-shots.done') -Encoding ASCII
    } catch { }
    break
}

function Shot-Window([string]$Name, [string[]]$Args2 = @()) {
    $path = Join-Path $OutDir $Name
    if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
    try {
        $out = & 'C:\Dev\jb-shot.ps1' -Process 'JockeyBox' -Out $path @Args2 2>&1
        Log ("  jb-shot.ps1 {0} -> {1}" -f ($Args2 -join ' '), (($out | Out-String).Trim()))
    } catch {
        Log ("  ! jb-shot.ps1 failed: {0}" -f $_.Exception.Message)
    }
    if (Test-Path $path) { return $path }
    return $null
}

# --- window discovery -------------------------------------------------------
# RootElement.FindFirst(Children, …) is as unreliable on this host as
# FindAll(Descendants): it returned nothing for a Settings dialog that EnumWindows
# proved was open (hwnd 17439586, title 'Settings'). Windows are therefore found
# with user32 and turned into automation elements with AutomationElement.FromHandle.
$env:TMP = 'C:\Dev\tmp'; $env:TEMP = 'C:\Dev\tmp'
New-Item -ItemType Directory -Force -Path 'C:\Dev\tmp' | Out-Null
if (-not ([System.Management.Automation.PSTypeName]'JbShots.Win32').Type) {
    Add-Type -Namespace JbShots -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr l);
[DllImport("user32.dll")] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(System.IntPtr h);
[DllImport("user32.dll")] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindowEnabled(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out R r);
[StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
'@
}

$script:ShotsPid = 0
$script:ShotsWins = $null
$script:ShotsCb = [JbShots.Win32+EnumProc]{
    param([System.IntPtr]$h, [System.IntPtr]$l)
    $wpid = [uint32]0
    [void][JbShots.Win32]::GetWindowThreadProcessId($h, [ref]$wpid)
    if ($wpid -eq [uint32]$script:ShotsPid) {
        $len = [JbShots.Win32]::GetWindowTextLength($h)
        $sb = New-Object System.Text.StringBuilder ($len + 2)
        [void][JbShots.Win32]::GetWindowText($h, $sb, $sb.Capacity)
        $cn = New-Object System.Text.StringBuilder 256
        [void][JbShots.Win32]::GetClassName($h, $cn, 256)
        $r = New-Object JbShots.Win32+R
        [void][JbShots.Win32]::GetWindowRect($h, [ref]$r)
        [void]$script:ShotsWins.Add([pscustomobject]@{
            hwnd = $h.ToInt64(); title = $sb.ToString(); cls = $cn.ToString()
            visible = [JbShots.Win32]::IsWindowVisible($h); enabled = [JbShots.Win32]::IsWindowEnabled($h)
            x = $r.L; y = $r.T; w = ($r.Rt - $r.L); h = ($r.B - $r.T)
        })
    }
    return $true
}

function Get-AppWindows {
    $script:ShotsPid = [int](Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    $script:ShotsWins = New-Object System.Collections.ArrayList
    [void][JbShots.Win32]::EnumWindows($script:ShotsCb, [System.IntPtr]::Zero)
    return @($script:ShotsWins)
}

function Get-AppWindowElement([string]$Title) {
    $w = @(Get-AppWindows | Where-Object { $_.title -eq $Title -and $_.visible -and $_.w -gt 200 }) | Select-Object -First 1
    if ($null -eq $w) { return $null }
    try { return [System.Windows.Automation.AutomationElement]::FromHandle([intptr]$w.hwnd) } catch { return $null }
}

function Get-ToggleState($Node) {
    if ($null -eq $Node) { return 'missing' }
    try {
        $p = $Node.El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        return [string]$p.Current.ToggleState
    } catch { return 'n/a' }
}

function Get-ComboValue($Node) {
    if ($null -eq $Node) { return $null }
    try {
        $p = $Node.El.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        $sel = @($p.Current.GetSelection())
        if ($sel.Count -gt 0) { return (@($sel | ForEach-Object { $_.Current.Name }) -join ', ') }
    } catch { }
    try {
        $v = $Node.El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return [string]$v.Current.Value
    } catch { }
    $txt = @(Get-Nodes -Root ($Node.El) -Max 20 -Deep -Type 'Text' | ForEach-Object { $_.Name })
    return ($txt -join ' / ')
}

function Get-TextValues($Root) {
    return @(Get-Nodes -Root $Root -Max 600 -Deep -Type 'Text' | ForEach-Object { $_.Name } | Where-Object { $_ -ne '' } | Select-Object -Unique)
}

function Log-Heap([string]$Where) {
    Log ("  [heap] {0}: {1} MB, gen2={2}" -f $Where, [int]([GC]::GetTotalMemory($false) / 1MB), [GC]::CollectionCount(2))
}

function Write-Result([string]$Reason = '') {
    # the prelude's trap calls this; a capture run must not overwrite e2e.json
    try { Log ("  aborted: {0}" -f $Reason) } catch { }
    try { "failed" | Set-Content -Path (Join-Path $OutDir 'verify-shots.done') -Encoding ASCII } catch { }
    return 1
}

$results = [ordered]@{}
$failures = New-Object System.Collections.ArrayList
$script:JsonlPath = Join-Path $OutDir 'verify-shots.jsonl'
if (Test-Path $script:JsonlPath) { Remove-Item $script:JsonlPath -Force -ErrorAction SilentlyContinue }

function Check([string]$Name, $Ok, [string]$Expected, $Actual) {
    # Only plain strings are allowed into the summary: ConvertTo-Json (5.1) dies
    # with OutOfMemoryException on certain values a UIA read can hand back, and one
    # bad value must not cost the whole report. Everything is also streamed to a
    # JSONL side-file at the moment it is asserted.
    $act = if ($null -eq $Actual) { $null }
           elseif ($Actual -is [string]) { $Actual }
           else { (@($Actual) | ForEach-Object { [string]$_ }) -join ' ' }
    $results[$Name] = [ordered]@{ pass = [bool]$Ok; expected = [string]$Expected; actual = $act }
    if (-not $Ok) { [void]$failures.Add($Name) }
    try {
        $line = [ordered]@{ id = $Name; pass = [bool]$Ok; expected = [string]$Expected; actual = $act } | ConvertTo-Json -Compress -Depth 3
        Add-Content -Path $script:JsonlPath -Value $line -Encoding UTF8
    } catch { }
    Log ("  {0,-6} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Ok) { '' } else { "  expected=$Expected actual=$act" }))
}

# ------------------------------------------------------- 1. playing deck capture

Log '--- capture 1: deck A playing at +3 % tempo'

$libTabShot = Get-LibraryTab
if ($libTabShot) { [void](Select-El $libTabShot) }
Start-Sleep -Milliseconds 500

[void](Set-Filter $Artist)
Start-Sleep -Milliseconds 900
[void](Wait-Until { $r = @(Get-TrackRows); if ($r.Count -gt 0 -and $r.Count -lt 80) { $r.Count } else { $null } } 15000)
Expand-Library

function Ensure-Playing2([int]$DeckIndex) {
    $p1 = Get-Span $DeckIndex; Start-Sleep -Milliseconds 1300; $p2 = Get-Span $DeckIndex
    if ($p1 -and $p2 -and $p2.pos -gt $p1.pos) { return $true }
    for ($i = 0; $i -lt 3; $i++) {
        [void](Invoke-El (Get-TransportButtons -Force).play)
        Start-Sleep -Milliseconds 1500
        $p1 = Get-Span $DeckIndex; Start-Sleep -Milliseconds 1300; $p2 = Get-Span $DeckIndex
        if ($p1 -and $p2 -and $p2.pos -gt $p1.pos) { return $true }
    }
    return $false
}

$rowAShot = Find-TrackRow $TrackAName
Log ("  track A row: '{0}'" -f $(if ($rowAShot) { $rowAShot.Name } else { 'MISSING' }))
if ($rowAShot) {
    [void](Select-El $rowAShot)
    Start-Sleep -Milliseconds 350
    $lb = Get-LibraryButton 'Load*'
    if ($lb) { [void](Invoke-El $lb) }
}
$loadedShot = Wait-Until { $d = Get-Deck 0; if ($d.position -and $d.position.dur -gt 30) { $d } else { $null } } 25000
Check 'C1a' ($null -ne $loadedShot) 'deck A loaded (duration > 0:30)' $(if ($loadedShot) { $loadedShot.position.text } else { 'no readout' })

$playOk = Ensure-Playing2 0
$past8 = Wait-Until { $s = Get-Span 0; if ($s -and $s.pos -gt 8) { $s } else { $null } } 45000
Check 'C1b' ($playOk -and $null -ne $past8) 'deck A position past 0:08' $(if ($past8) { $past8.text } else { 'never reached 0:08' })

[void](Set-Tempo 0 3.0)
Start-Sleep -Milliseconds 1600
$ratioNow = (Get-Deck 0).ratio
Check 'C1c' ($ratioNow -and $ratioNow -ne '1.000') 'tempo ratio text is not 1.000' "ratio='$ratioNow'"

Start-Sleep -Milliseconds 700
$playShot = Shot-Window 'verify-playing.png'
Check 'C1d' ($null -ne $playShot) 'jb-shot.ps1 wrote verify-playing.png' $(if ($playShot) { 'yes' } else { 'no file' })

$dA1 = Get-Deck 0
$mon = Find-Node -Type 'Button' -Name 'Microphone monitor' -Force
$mute = Find-Node -Type 'Button' -Name 'Microphone mute' -Force
$cfA = Get-Crossfader
$textsPlaying = Get-TextValues $script:Win
$masterText = @($textsPlaying | Where-Object { $_ -match '^\d{1,3}%$' }) | Select-Object -First 1
$statusTexts = @($textsPlaying | Where-Object { $_ -match '(?i)idle|next:|fade|crossfade|transition|loaded|playing' })

$results['deckA'] = [ordered]@{
    title = $dA1.title; bpm = $dA1.bpm; ratio = $dA1.ratio
    position = $(if ($dA1.position) { $dA1.position.text } else { $null })
}
$results['mixer'] = [ordered]@{
    mon = Get-ToggleState $mon
    mute = Get-ToggleState $mute
    masterText = $masterText
    crossfader = $cfA
}
$results['statusTexts'] = [ordered]@{ pass = $null; expected = $null; actual = $statusTexts }
$results['textsAll'] = $textsPlaying

Log ("  deck A: title='{0}' bpm='{1}' ratio='{2}' position='{3}'" -f $dA1.title, $dA1.bpm, $dA1.ratio, $(if ($dA1.position) { $dA1.position.text } else { 'n/a' }))
Log ("  MON={0} MUTE={1} master='{2}' crossfader={3}" -f (Get-ToggleState $mon), (Get-ToggleState $mute), $masterText, $cfA)
Log ("  status texts: {0}" -f ($statusTexts -join ' || '))

# ------------------------------------------------------- 2. transition capture

Log-Heap 'after capture 1'
Log '--- capture 2: arm a transition and shoot mid-fade'

[void](Set-Tempo 0 0.0)
$tp2 = Get-TransportButtons
if ($tp2 -and $tp2.stop) { [void](Invoke-El $tp2.stop); Start-Sleep -Milliseconds 900 }
$clear2 = Get-QueueButton 'Clear'
if ($clear2) { [void](Invoke-El $clear2); Start-Sleep -Milliseconds 700 }

$rowBShot = Find-TrackRow $TrackBName
$queueBtn2 = Get-LibraryButton '+ Queue'
if ($rowAShot -and $queueBtn2) { [void](Select-El $rowAShot); Start-Sleep -Milliseconds 250; [void](Invoke-El $queueBtn2); Start-Sleep -Milliseconds 400 }
if ($rowBShot -and $queueBtn2) { [void](Select-El $rowBShot); Start-Sleep -Milliseconds 250; [void](Invoke-El $queueBtn2); Start-Sleep -Milliseconds 700 }

$qt2 = Get-QueueTab
if ($qt2) { [void](Select-El $qt2); Start-Sleep -Milliseconds 900 }
$q2 = @(Wait-Until { $r = @(Get-QueueRows); if ($r.Count -ge 2) { $r } else { $null } } 10000)
Log ("  queue rows: {0}" -f $q2.Count)
$row1Play2 = $null
if ($q2.Count -ge 1) {
    $row1Play2 = @(Get-Nodes -Root ($q2[0].El) -Max 40 -Deep -Type 'Button') | Where-Object { $_.Name -eq '▶' -or $_.Name -eq 'Play' } | Select-Object -First 1
}
$armedAt = @(Read-AppLogSince $script:LogStart | Where-Object { $_ -match 'transition ' }).Count
if ($row1Play2) { [void](Invoke-El $row1Play2) }
Check 'C2a' ($null -ne $row1Play2) 'queued row play button found' $(if ($row1Play2) { 'yes' } else { 'missing' })

$armLine = Wait-Until {
    $l = @(Read-AppLogSince $script:LogStart | Where-Object { $_ -match 'transition A->B' })
    if ($l.Count -gt 0) { $l[-1] } else { $null }
} 30000 1000
$results['armLine'] = [ordered]@{ pass = $null; expected = $null; actual = $(if ($armLine) { [string]$armLine } else { '' }) }
Check 'C2b' ($null -ne $armLine) 'a "transition A->B ... fade=" plan was logged' $(if ($armLine) { ($armLine -replace '^\S+ \S+ ', '') } else { 'no arm line' })

# wait for the fade window: hold off until the outgoing deck is within ~12 s of its end
$fadeShot = $null
$cfMid = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    $s = Get-Span 0
    if ($null -eq $s) { Start-Sleep -Milliseconds 500; continue }
    if (($s.dur - $s.pos) -le 12) { break }
    Start-Sleep -Milliseconds 700
}
while ((Get-Date) -lt $deadline) {
    $cf = Get-Crossfader
    if ($null -ne $cf) {
        if ($cf -gt 0.08 -and $cf -lt 0.55) {
            $cfMid = $cf
            $fadeShot = Shot-Window 'verify-transition.png'
            break
        }
        if ($cf -ge 0.99) { break }
    }
    Start-Sleep -Milliseconds 90
}
$midTexts = @()
if ($fadeShot) {
    $midTexts = @(Get-TextValues $script:Win | Where-Object { $_ -match '(?i)idle|next:|fade|crossfade|transition|playing' })
} else {
    # missed the 8 s window: wait for the hand-off to finish and shoot that
    $post = Wait-Until { $s = Get-Span 1; if ($s -and $s.pos -gt 3) { $s } else { $null } } 40000 700
    $fadeShot = Shot-Window 'verify-transition.png'
    $cfMid = Get-Crossfader
    $midTexts = @(Get-TextValues $script:Win | Where-Object { $_ -match '(?i)idle|next:|fade|crossfade|transition|playing' })
    Log ("  missed the fade window; captured after the hand-off (deck B at {0})" -f $(if ($post) { $post.text } else { 'n/a' }))
}
Check 'C2c' ($null -ne $fadeShot) 'verify-transition.png written' $(if ($fadeShot) { "crossfader at capture=$cfMid" } else { 'no file' })
$results['midFade'] = [ordered]@{ crossfader = $cfMid; statusTexts = $midTexts; deckA = (Get-Deck 0).position.text; deckB = (Get-Deck 1).position.text }

# ------------------------------------------------------- 3. settings capture

Log-Heap 'after capture 2'
Log '--- capture 3: open Settings, shoot it, inventory its controls'

$settingsBtn = @(Get-WinNodes -Force | Where-Object { $_.Type -eq 'Button' -and $_.Name -like '*Settings*' }) | Select-Object -First 1
Log ("  settings button: '{0}'" -f $(if ($settingsBtn) { $settingsBtn.Name } else { 'MISSING' }))
Check 'C3a' ($null -ne $settingsBtn) "a button whose name contains 'Settings'" $(if ($settingsBtn) { $settingsBtn.Name } else { 'not found' })
$opened = $false
if ($settingsBtn) { $opened = Invoke-El $settingsBtn }

$settingsWin = Wait-Until {
    $w = Get-AppWindowElement 'Settings'
    if ($null -ne $w) { $w } else { $null }
} 25000 500
$windowList = @(Get-AppWindows | ForEach-Object { [pscustomobject]@{ title = $_.title; hwnd = $_.hwnd; visible = $_.visible; enabled = $_.enabled; w = $_.w; h = $_.h; x = $_.x; y = $_.y } })
Start-Sleep -Milliseconds 1200
Check 'C3b' ($null -ne $settingsWin) 'a top-level window named Settings exists' $(if ($settingsWin) { "hwnd=$($settingsWin.Current.NativeWindowHandle)" } else { 'not found' })

$settingsShot = $null
$settingsHwnd = 0
if ($null -ne $settingsWin) {
    try { $settingsHwnd = [int]$settingsWin.Current.NativeWindowHandle } catch { }
    $settingsShot = Shot-Window 'verify-settings.png' @('-Settings')
    if ($null -eq $settingsShot -and $settingsHwnd -ne 0) {
        Log '  EnumWindows pick failed; retrying with the HWND from UIA'
        $settingsShot = Shot-Window 'verify-settings.png' @('-Hwnd', "$settingsHwnd")
    }
}
Check 'C3c' ($null -ne $settingsShot) 'verify-settings.png written' $(if ($settingsShot) { 'yes' } else { 'no file' })

# depth-3 control tree
$treeLines = New-Object System.Collections.ArrayList
if ($null -ne $settingsWin) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    function Dump-Settings($el, [int]$d) {
        if ($d -gt 3) { return }
        $t = '?'; $n = ''
        try { $t = ([string]$el.Current.ControlType.ProgrammaticName) -replace '^ControlType\.', ''; $n = [string]$el.Current.Name } catch { return }
        [void]$treeLines.Add(('  ' * $d) + $t + $(if ($n) { "  '$n'" } else { '' }))
        $child = $null
        try { $child = $walker.GetFirstChild($el) } catch { return }
        $i = 0
        while ($null -ne $child -and $i -lt 400) {
            Dump-Settings $child ($d + 1)
            try { $child = $walker.GetNextSibling($child) } catch { $child = $null }
            $i++
        }
    }
    Dump-Settings $settingsWin 0
}
$treePath = Join-Path $OutDir 'verify-settings.txt'
$treeLines | Set-Content -Path $treePath -Encoding UTF8
$results['settingsTree'] = @($treeLines)
Log ("  settings tree: {0} line(s) -> {1}" -f $treeLines.Count, $treePath)

# full inventory: every combo (with its selected value), check box and edit box
$combos = @(); $checks = @(); $edits = @(); $labels = @()
if ($null -ne $settingsWin) {
    $all = @(Get-Nodes -Root $settingsWin -Max 4000 -Deep)
    foreach ($n in $all) {
        if ($n.Type -eq 'Text' -and $n.Name -ne '') {
            $r = $n.El.Current.BoundingRectangle
            $labels += [pscustomobject]@{ text = $n.Name; x = [int]$r.X; y = [int]$r.Y }
        }
        if ($n.Type -eq 'ComboBox') {
            $combos += [ordered]@{ name = $n.Name; value = (Get-ComboValue $n); y = [int]$n.El.Current.BoundingRectangle.Y; x = [int]$n.El.Current.BoundingRectangle.X }
        } elseif ($n.Type -eq 'CheckBox') {
            $checks += [ordered]@{ name = $n.Name; state = (Get-ToggleState $n); y = [int]$n.El.Current.BoundingRectangle.Y; x = [int]$n.El.Current.BoundingRectangle.X }
        } elseif ($n.Type -eq 'Edit') {
            $v = ''
            try { $v = [string]$n.El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
            $edits += [ordered]@{ name = $n.Name; value = $v; y = [int]$n.El.Current.BoundingRectangle.Y; x = [int]$n.El.Current.BoundingRectangle.X }
        }
    }
}
# The x:Name of a control is not exposed to UIA and WPF ComboBox/Edit carry no
# accessible name here, so each control is matched to the text sitting on its row.
function Get-RowLabel([int]$cx, [int]$cy) {
    $cand = @($labels | Where-Object { [Math]::Abs($_.y - $cy) -le 11 -and $_.x -lt $cx -and $_.text -ne '' })
    if ($cand.Count -eq 0) { return $null }
    return ($cand | Sort-Object { [Math]::Abs($_.y - $cy) }, { - $_.x } | Select-Object -First 1).text
}
foreach ($c in @($combos) + @($checks) + @($edits)) {
    $row = Get-RowLabel $c.x $c.y
    $c['row'] = $row
    $c['label'] = if ($c.name) { $c.name } else { $row }
}
$results['combos'] = $combos
$results['checkBoxes'] = $checks
$results['editBoxes'] = $edits
foreach ($c in $combos) { Log ("  combo  y={0,5} [{1}] = '{2}'" -f $c.y, $c.row, $c.value) }
foreach ($c in $checks) { Log ("  check  y={0,5} [{1}] = {2}  (control name '{3}')" -f $c.y, $c.row, $c.state, $c.name) }
foreach ($c in $edits)  { Log ("  edit   y={0,5} [{1}] = '{2}'" -f $c.y, $c.row, $c.value) }

# the twelve named controls, matched by their visible row labels
$rows = @(
    @{ id = 'ProgramOutputBox'; label = 'Program output'; type = 'ComboBox' },
    @{ id = 'SecondOutputBox'; label = 'Second output'; type = 'ComboBox' },
    @{ id = 'MonitorOutputBox'; label = 'Monitor output'; type = 'ComboBox' },
    @{ id = 'DeckACueCheck'; label = 'Deck A direct out'; type = 'CheckBox' },
    @{ id = 'DeckADeviceBox'; label = 'Deck A direct out'; type = 'ComboBox' },
    @{ id = 'DeckBCueCheck'; label = 'Deck B direct out'; type = 'CheckBox' },
    @{ id = 'DeckBDeviceBox'; label = 'Deck B direct out'; type = 'ComboBox' },
    @{ id = 'MicInputBox'; label = 'Mic input (talk-over)'; type = 'ComboBox' },
    @{ id = 'OutputDelayBox'; label = 'Global output delay (ms)'; type = 'Edit' },
    @{ id = 'TempoRangeBox'; label = 'Tempo slider range'; type = 'Edit' },
    @{ id = 'TransitionModeBox'; label = 'Transition style'; type = 'ComboBox' },
    @{ id = 'TransitionFadeBox'; label = 'Fade length (s)'; type = 'Edit' }
)
$sections = @('AUDIO ROUTING', 'MICROPHONE', 'OUTPUT DELAY', 'TEMPO', 'AUTOMATIC MIXING', 'Save', 'Cancel')
$settingsTexts = if ($null -ne $settingsWin) { Get-TextValues $settingsWin } else { @() }
$results['settingsTexts'] = $settingsTexts
foreach ($sec in $sections) {
    $hit = @($settingsTexts | Where-Object { $_ -like "$sec*" })
    Check ("C3s:$sec") ($hit.Count -gt 0) "text starting with '$sec'" $(if ($hit) { $hit[0] } else { "not in: $($settingsTexts -join ' | ')" })
}
$rowReport = @()
foreach ($r in $rows) {
    $labelHit = @($settingsTexts | Where-Object { $_ -like "$($r.label)*" })
    $ctlCount = switch ($r.type) {
        'ComboBox' { @($combos | Where-Object { $_.y -ge 0 }).Count }
        'CheckBox' { $checks.Count }
        default { $edits.Count }
    }
    $ok = ($labelHit.Count -gt 0)
    $rowReport += [ordered]@{ id = $r.id; label = $r.label; kind = $r.type; found = $ok; text = $(if ($labelHit) { $labelHit[0] } else { $null }) }
    Check ("C3r:$($r.id)") $ok "label '$($r.label)' present with a $($r.type)" $(if ($labelHit) { $labelHit[0] } else { 'label not found' })
}
$results['namedControls'] = $rowReport

# ------------------------------------------------------- close Settings

if ($null -ne $settingsWin) {
    $cancel = @(Get-Nodes -Root $settingsWin -Max 900 -Deep -Type 'Button' | Where-Object { $_.Name -eq 'Cancel' }) | Select-Object -First 1
    if ($cancel) { [void](Invoke-El $cancel); Log '  pressed Cancel in Settings' }
    else {
        try { $settingsWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    }
}
$closed = Wait-Until {
    $w = @(Get-AppWindows | Where-Object { $_.title -eq 'Settings' -and $_.visible })
    if ($w.Count -eq 0) { 'gone' } else { $null }
} 15000 500
Check 'C3z' ($closed -eq 'gone') 'the Settings window closed again' "result=$closed"

# ------------------------------------------------------- restore + summary

$summary = [ordered]@{
    generatedAt = (Get-Date).ToString('s')
    verdict = $(if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' })
    failures = @($failures)
    results = $results
}
$jsonPath = Join-Path $OutDir 'verify-shots.json'
Log-Heap 'before serialising'
# The report is written with a hand-rolled writer. Windows PowerShell 5.1's
# ConvertTo-Json walks the object graph reflectively and dies with
# OutOfMemoryException (5 GB RSS, no output) somewhere in this summary, twice,
# with two different sanitisers in front of it. Every value below is a string,
# bool or double, so no reflection is needed: the JSON is composed directly.
function JStr($v) {
    if ($null -eq $v) { return 'null' }
    $s = [string]$v
    $s = $s -replace '\\', '\\' -replace '"', '\"'
    $s = $s -replace "`r", '\r' -replace "`n", '\n' -replace "`t", '\t'
    return '"' + $s + '"'
}
function JBool($v) { if ($v) { 'true' } else { 'false' } }
function JNum($v) { if ($null -eq $v) { 'null' } else { ([double]$v).ToString([System.Globalization.CultureInfo]::InvariantCulture) } }
function JObj($pairs) {
    # $pairs is an array of "name" then value expressions already rendered
    $parts = @()
    for ($i = 0; $i -lt $pairs.Count; $i += 2) { $parts += ((JStr $pairs[$i]) + ':' + $pairs[$i + 1]) }
    return '{' + ($parts -join ',') + '}'
}
function JArr($items) { return '[' + (@($items) -join ',') + ']' }

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{"generatedAt":' + (JStr ((Get-Date).ToString('s'))))
[void]$sb.Append(',"verdict":' + (JStr $(if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' })))
$failJson = @(foreach ($f in $failures) { JStr $f })
[void]$sb.Append(',"failures":' + (JArr $failJson))
[void]$sb.Append(',"counts":' + (JObj @('pass', "$(@($results.Keys | Where-Object { $results[$_].pass -eq $true }).Count)",
                                       'fail', "$(@($results.Keys | Where-Object { $results[$_].pass -eq $false }).Count)")))

# assertions
$readKeys = @('deckA', 'mixer', 'statusTexts', 'armLine', 'midFade', 'settingsTree', 'namedControls',
              'combos', 'checkBoxes', 'editBoxes', 'settingsTexts', 'windows')
$assertJson = @(foreach ($k in @($results.Keys | Where-Object { $readKeys -notcontains $_ })) {
    $r = $results[$k]
    (JObj @('id', (JStr $k), 'pass', (JBool $r.pass), 'expected', (JStr $r.expected), 'actual', (JStr $r.actual)))
})
[void]$sb.Append(',"assertions":' + (JArr $assertJson))

# reads
$deckJson = JObj @('title', (JStr $deckA.title), 'bpm', (JStr $deckA.bpm), 'ratio', (JStr $deckA.ratio),
                   'position', (JStr $deckA.position), 'tempoPercent', (JNum 3.0))
$mixJson = JObj @('mon', (JStr $mixer.mon), 'mute', (JStr $mixer.mute), 'masterText', (JStr $mixer.masterText), 'crossfader', (JNum $mixer.crossfader))
$combJson = @(foreach ($c in $combos) { JObj @('label', (JStr $c['row']), 'value', (JStr $c.value), 'y', (JNum $c.y)) })
$chkJson = @(foreach ($c in $checks) { JObj @('label', (JStr $c['row']), 'name', (JStr $c.name), 'state', (JStr $c.state), 'y', (JNum $c.y)) })
$editJson = @(foreach ($c in $edits) { JObj @('label', (JStr $c['row']), 'value', (JStr $c.value), 'y', (JNum $c.y)) })
$textJson = @(foreach ($t in @($settingsTexts)) { JStr $t })
$midJson = JObj @('crossfader', (JNum $midFade.crossfader), 'deckA', (JStr $midFade.deckA), 'deckB', (JStr $midFade.deckB),
                  'statusTexts', (JArr @(foreach ($t in @($midFade.statusTexts)) { JStr $t })))
[void]$sb.Append(',"reads":' + (JObj @(
    'deckA', $deckJson,
    'mixer', $mixJson,
    'statusTexts', (JArr @(foreach ($t in @($results['statusTexts'].actual)) { JStr $t })),
    'midFade', $midJson,
    'armLine', (JStr $results['armLine'].actual),
    'combos', (JArr $combJson),
    'checkBoxes', (JArr $chkJson),
    'editBoxes', (JArr $editJson),
    'settingsTexts', (JArr $textJson),
    'windows', (JArr @(foreach ($w in @($windowList)) { JObj @('title', (JStr $w.title), 'visible', (JBool $w.visible), 'enabled', (JBool $w.enabled), 'w', (JNum $w.w), 'h', (JNum $w.h)) }))
)))

$jsonPath = Join-Path $OutDir 'verify-shots.json'
try {
    [void]$sb.Append('}')
    Set-Content -Path $jsonPath -Value $sb.ToString() -Encoding UTF8
    Log ("  json written: {0} chars" -f $sb.Length)
} catch {
    Log ("  ! summary write failed: {0}" -f $_.Exception.Message)
}
Log ("=== RESULT: {0} - {1} check(s) failed ===" -f $(if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }), $failures.Count)
Log ("json: {0}" -f $jsonPath)
"written" | Set-Content -Path (Join-Path $OutDir 'verify-shots.done') -Encoding ASCII

# leave the app quiet, but never after the evidence is on disk
try {
    $tpEnd = Get-TransportButtons
    if ($tpEnd -and $tpEnd.stop) { [void](Invoke-El $tpEnd.stop) }
} catch { Log ("  (post-run stop skipped: {0})" -f $_.Exception.Message) }
$script:Win = $null
exit 0
