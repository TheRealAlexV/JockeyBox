# ===================================================== step S: the Settings window
# The user's two reports against the deployed app: (1) Settings still showed a
# "Karaoke folder" next to "Music folder" although the library is one root now,
# and (2) Save refused the config with "Music and Karaoke folders must be
# different directories." while both point at the same real folder.
#
# Everything below drives the real dialog a user gets: the toolbar button opens
# SettingsWindow.ShowDialog(), Save is the same Button, and the outcome is read
# from %APPDATA%\JockeyBox\settings.json - the file the app actually writes.

Log '--- step S: settings window - one library root, save, round trip'

$settingsFile = Join-Path $env:APPDATA 'JockeyBox\settings.json'
$musicPath = 'C:\Users\Alex\Music'

function Get-AppTopWindows {
    # Every top-level window of the app process. Settings is a second WPF window
    # in the same process, and a MessageBox is a third, so the process id - not
    # the window title - is what identifies "ours".
    if (-not $script:AppPid) { return @() }
    $pc = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $script:AppPid)
    $found = @()
    try { $found = @($AE::RootElement.FindAll($TS::Children, $pc)) } catch { return @() }
    $list = @()
    foreach ($w in $found) {
        $n = ''; $cls = ''
        try { $n = [string]$w.Current.Name } catch { }
        try { $cls = [string]$w.Current.ClassName } catch { }
        $r = $null
        try { $r = $w.Current.BoundingRectangle } catch { }
        $list += [pscustomobject]@{ El = $w; Name = $n; Cls = $cls; W = $(if ($r) { [int]$r.Width } else { 0 }); H = $(if ($r) { [int]$r.Height } else { 0 }) }
    }
    return $list
}

function Get-SettingsWindow {
    foreach ($w in @(Get-AppTopWindows)) { if ($w.Name -eq 'Settings') { return $w.El } }
    return $null
}

function Get-DialogWindows {
    # Anything that is neither the shell nor Settings: a MessageBox or a prompt.
    return @(Get-AppTopWindows | Where-Object { $_.Name -ne '' -and $_.Name -ne 'JockeyBox' -and $_.Name -ne 'Settings' })
}

function Get-SettingsNodes([int]$Max = 1400) {
    $w = Get-SettingsWindow
    if ($null -eq $w) { return @() }
    return @(Get-Nodes -Root $w -Max $Max -Deep)
}

function Get-ElValue($Node) {
    if ($null -eq $Node) { return $null }
    try { return [string]$Node.El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return $null }
}

function Set-ElValue($Node, [string]$Text) {
    if ($null -eq $Node) { return $false }
    try {
        $Node.El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
        return $true
    } catch {
        Log ("  ! setting '{0}' failed: {1}" -f $Node.Name, $_.Exception.Message)
        return $false
    }
}

function Set-CheckBox($Node, [string]$Want) {
    if ($null -eq $Node) { return $false }
    for ($i = 0; $i -lt 3; $i++) {
        $cur = ''
        try { $cur = [string]$Node.El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { }
        if ($cur -eq $Want) { return $true }
        try { $Node.El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch {
            try { $Node.El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        }
        Start-Sleep -Milliseconds 300
    }
    $cur = ''
    try { $cur = [string]$Node.El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { }
    return ($cur -eq $Want)
}

function Get-ElBounds($Node) {
    try { return $Node.El.Current.BoundingRectangle } catch { return $null }
}

function Get-RowLabelNear($Nodes, $Node) {
    # x:Name is not in UIA and a WPF CheckBox often reports only its own content
    # ("Enable"), so a control is identified by the text sitting on its row.
    $b = Get-ElBounds $Node
    if ($null -eq $b) { return '' }
    $cand = @($Nodes | Where-Object {
            $_.Type -eq 'Text' -and $_.Name -ne '' -and
            [Math]::Abs((Get-ElBounds $_).Y - $b.Y) -le 12 -and
            (Get-ElBounds $_).X -lt $b.X })
    if ($cand.Count -eq 0) { return '' }
    return ($cand | Sort-Object { [Math]::Abs((Get-ElBounds $_).Y - $b.Y) }, { - (Get-ElBounds $_).X } | Select-Object -First 1).Name
}

function Get-ComboItemNames($Node, [int]$Max = 40) {
    $el = $Node.El
    $walk = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $items = @()
    $collect = {
        $items = @()
        try { $c = $walk.GetFirstChild($el) } catch { $c = $null }
        while ($null -ne $c) {
            $t = ''
            try { $t = ([string]$c.Current.ControlType.ProgrammaticName) -replace '^ControlType\.', '' } catch { }
            if ($t -eq 'ListItem' -or $t -eq 'Text') {
                try { $n = [string]$c.Current.Name; if ($n -ne '') { $items += $n } } catch { }
            }
            try { $c = $walk.GetNextSibling($c) } catch { $c = $null }
        }
        return $items
    }
    $items = & $collect
    if ($items.Count -eq 0) {
        # WPF materialises the item peers when the dropdown opens.
        try { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 500 } catch { }
        $items = & $collect
        # the open dropdown is its own popup window; read the items from there
        if ($items.Count -eq 0) {
            foreach ($w in @(Get-AppTopWindows | Where-Object { $_.Name -eq '' -or $_.Cls -like '*Popup*' })) {
                $pn = @(Get-Nodes -Root $w.El -Max $Max -Deep | Where-Object { $_.Type -eq 'ListItem' })
                if ($pn.Count -gt 0) { $items = @($pn | ForEach-Object { $_.Name }); break }
            }
        }
        try { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep -Milliseconds 250 } catch { }
    }
    return @($items | Where-Object { $_ -ne '' } | Select-Object -Unique)
}

function Get-SettingsJsonRaw {
    if (-not (Test-Path $settingsFile)) { return '' }
    return (Get-Content -Path $settingsFile -Raw -ErrorAction SilentlyContinue)
}

function Get-SettingsJson {
    try { return (Get-SettingsJsonRaw | ConvertFrom-Json) } catch { return $null }
}

function Open-SettingsWindow {
    $script:Nodes = $null
    $btn = $null
    foreach ($b in @(Get-WinNodes -Force | Where-Object { $_.Type -eq 'Button' -and $_.Name -match 'Settings' })) { $btn = $b; break }
    if ($null -eq $btn) {
        $names = @(Get-WinNodes -Force | Where-Object { $_.Type -eq 'Button' } | ForEach-Object { "'" + $_.Name + "'" })
        Log ("  ! no Settings button; buttons: {0}" -f ($names -join ', '))
        return $null
    }
    Log ("  opening settings via button '{0}'" -f $btn.Name)
    [void](Invoke-El $btn)
    $w = Wait-Until { Get-SettingsWindow } 15000 300
    if ($null -ne $w) { Start-Sleep -Milliseconds 1200 }
    return $w
}

function Get-SettingsButton([string]$Text) {
    $nodes = @(Get-SettingsNodes)
    foreach ($n in $nodes) { if ($n.Type -eq 'Button' -and $n.Name -eq $Text) { return $n } }
    return $null
}

function Close-SettingsWindow {
    # Cancel is the documented way out; Save is used where the save itself is the
    # thing under test.
    $nodes = @(Get-SettingsNodes)
    $hasCancel = @($nodes | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq 'Cancel' }).Count
    $w = Get-SettingsWindow
    if ($null -eq $w) { return }
    if ($hasCancel -gt 0) { [void](Invoke-El (Get-SettingsButton 'Cancel')) }
    $null = Wait-Until { if (Get-SettingsWindow) { $null } else { $true } } 8000 300
    if (Get-SettingsWindow) {
        try { (New-Object -ComObject WScript.Shell).SendKeys('%{F4}') } catch { }
        $null = Wait-Until { if (Get-SettingsWindow) { $null } else { $true } } 5000 300
    }
}

# The app was restarted at the end of step 5, so this pid - not the preflight one.
$sproc = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
$script:AppPid = if ($sproc) { $sproc.Id } else { $null }
Log ("  app pid for the settings walk: {0}" -f $script:AppPid)

$jsonBefore = Get-SettingsJsonRaw
$jsonBeforeObj = Get-SettingsJson

# ---------------------------------------------------------------- S1: open + layout
$setWin = Open-SettingsWindow
Assert 'S1a' 'step S' 'the Settings window opens from the toolbar' ($null -ne $setWin) 'a Settings window in the app process' $(if ($setWin) { "opened, title 'Settings'" } else { 'not found' })

$setNodes = @(Get-SettingsNodes)
$karaokeLabels = @($setNodes | Where-Object { $_.Type -eq 'Text' -and $_.Name -match '^Karaoke folder' })
$headers = @{}
foreach ($n in $setNodes) { if ($n.Type -eq 'Text' -and $n.Name -in @('LIBRARY FOLDERS', 'VOCAL REMOVAL ENGINE (UVR)', 'AUDIO ROUTING', 'MICROPHONE')) { $headers[$n.Name] = (Get-ElBounds $n).Y } }
$bandTop = if ($headers.ContainsKey('LIBRARY FOLDERS')) { $headers['LIBRARY FOLDERS'] } else { $null }
$bandBottom = if ($headers.ContainsKey('VOCAL REMOVAL ENGINE (UVR)')) { $headers['VOCAL REMOVAL ENGINE (UVR)'] } else { $null }
$bandEdits = @()
if ($null -ne $bandTop -and $null -ne $bandBottom) {
    $bandEdits = @($setNodes | Where-Object { $_.Type -eq 'Edit' -and (Get-ElBounds $_).Y -gt $bandTop -and (Get-ElBounds $_).Y -lt $bandBottom })
}
$musicBoxes = @($setNodes | Where-Object { $_.Type -eq 'Edit' -and (Get-ElValue $_) -eq $musicPath })
$helpText = @($setNodes | Where-Object { $_.Type -eq 'Text' -and $_.Name -match 'One library root' })

Assert 'S1b' 'step S' 'no "Karaoke folder" label and exactly one folder box in LIBRARY FOLDERS' `
    (($karaokeLabels.Count -eq 0) -and ($bandEdits.Count -eq 1)) `
    '0 Karaoke-folder labels, 1 Edit in the LIBRARY FOLDERS band' `
    ("karaoke labels=$($karaokeLabels.Count) band=$($bandTop)-$($bandBottom) edits in band=$($bandEdits.Count): " + (@($bandEdits | ForEach-Object { "'" + (Get-ElValue $_) + "'" }) -join ', '))

Assert 'S1c' 'step S' 'the single library row still shows the real music folder' `
    (($musicBoxes.Count -eq 1) -and ($helpText.Count -ge 1)) `
    ("one box reading '{0}' plus the single-root help text" -f $musicPath) `
    ("boxes with that path={0} value='{1}' help='{2}'" -f $musicBoxes.Count, (Get-ElValue $musicBoxes[0]), (if ($helpText.Count -gt 0) { $helpText[0].Name } else { 'missing' }))

# ------------------------------------------------------------- S2: save as-is
Log ("  settings.json before: MusicDirectory='{0}' KaraokeDirectory='{1}'" -f $jsonBeforeObj.MusicDirectory, $jsonBeforeObj.KaraokeDirectory)
$beforeStamp = (Get-Item $settingsFile).LastWriteTimeUtc

$saveBtn = Get-SettingsButton 'Save'
$saveInvoked = Invoke-El $saveBtn
$dialogs = @()
if ($saveInvoked) {
    # A rejection is a modal MessageBox: it appears within a frame or two and it
    # blocks the dialog, so poll for a while before believing the save went through.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 6000) {
        $d = @(Get-DialogWindows)
        if ($d.Count -gt 0) { $dialogs = $d; break }
        if ($null -eq (Get-SettingsWindow)) { Start-Sleep -Milliseconds 400; $dialogs = @(Get-DialogWindows); break }
        Start-Sleep -Milliseconds 300
    }
}
$dialogNames = @($dialogs | ForEach-Object { $_.Name })
$dialogText = ''
foreach ($d in $dialogs) {
    $tn = @(Get-Nodes -Root $d.El -Max 60 -Deep | Where-Object { $_.Type -eq 'Text' -and $_.Name -ne '' } | ForEach-Object { $_.Name })
    $dialogText = ($tn -join ' | ')
}
Assert 'S2a' 'step S' 'Save accepts the config with Music and Karaoke pointing at the same folder' `
    ($saveInvoked -and $dialogs.Count -eq 0) `
    'no modal dialog appears' `
    ("save invoked={0} dialogs={1} {2}" -f $saveInvoked, $dialogNames.Count, $(if ($dialogText) { "text: $dialogText" } else { '(none)' }))

$closed = Wait-Until { if (Get-SettingsWindow) { $null } else { $true } } 10000 300
Assert 'S2b' 'step S' 'the Settings window closes on Save' ($null -ne $closed) 'window gone within 10 s' $(if ($closed) { 'closed' } else { 'still open' })

Start-Sleep -Milliseconds 800
$jsonAfterObj = Get-SettingsJson
$afterStamp = (Get-Item $settingsFile).LastWriteTimeUtc
$sameRoots = ($jsonAfterObj.MusicDirectory -eq $musicPath) -and ($jsonAfterObj.KaraokeDirectory -eq $musicPath)
Assert 'S2c' 'step S' 'settings.json keeps one root: MusicDirectory == KaraokeDirectory == the real folder' `
    $sameRoots `
    ("MusicDirectory='{0}' KaraokeDirectory='{1}'" -f $musicPath, $musicPath) `
    ("MusicDirectory='{0}' KaraokeDirectory='{1}' written={2}" -f $jsonAfterObj.MusicDirectory, $jsonAfterObj.KaraokeDirectory, ($afterStamp -gt $beforeStamp))

$audioBefore = [ordered]@{
    ProgramOutputDeviceId = $jsonAfterObj.ProgramOutputDeviceId
    SecondOutputDeviceId  = $jsonAfterObj.SecondOutputDeviceId
    MonitorOutputDeviceId = $jsonAfterObj.MonitorOutputDeviceId
    DeckADeviceId         = $jsonAfterObj.DeckADeviceId
    DeckBDeviceId         = $jsonAfterObj.DeckBDeviceId
    DeckACueEnabled       = $jsonAfterObj.DeckACueEnabled
    DeckBCueEnabled       = $jsonAfterObj.DeckBCueEnabled
    MicDeviceId           = $jsonAfterObj.MicDeviceId
    OutputDelayMs         = $jsonAfterObj.OutputDelayMs
    TransitionMode        = $jsonAfterObj.TransitionMode
}

# ------------------------------------------- S3: harmless change + second save
$setWin2 = Open-SettingsWindow
$setNodes2 = @(Get-SettingsNodes)
Assert 'S3a' 'step S' 'Settings reopens after a save' ($null -ne $setWin2) 'Settings window found' $(if ($setWin2) { 'opened' } else { 'not found' })

# "Also generate karaoke versions after a batch" is a harmless unrelated toggle:
# it is flipped off and back, so the file must come out identical apart from the
# write itself.
$toggle = $null
foreach ($n in $setNodes2) { if ($n.Type -eq 'CheckBox' -and $n.Name -match 'Also generate karaoke') { $toggle = $n; break } }
$toggleOk = $false
$toggleStates = @()
if ($null -ne $toggle) {
    $s0 = ''
    try { $s0 = [string]$toggle.El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { }
    $other = if ($s0 -eq 'On') { 'Off' } else { 'On' }
    $t1 = Set-CheckBox $toggle $other
    $s1 = ''
    try { $s1 = [string]$toggle.El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { }
    $t2 = Set-CheckBox $toggle $s0
    $s2 = ''
    try { $s2 = [string]$toggle.El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { }
    $toggleStates = @($s0, $s1, $s2)
    $toggleOk = ($t1 -and $t2 -and $s1 -eq $other -and $s2 -eq $s0)
    Log ("  toggled '{0}': {1}" -f $toggle.Name, ($toggleStates -join ' -> '))
}

$stampBefore3 = (Get-Item $settingsFile).LastWriteTimeUtc
$save2 = Invoke-El (Get-SettingsButton 'Save')
$dialogs3 = @()
if ($save2) {
    $sw3 = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw3.ElapsedMilliseconds -lt 5000) {
        $d3 = @(Get-DialogWindows)
        if ($d3.Count -gt 0) { $dialogs3 = $d3; break }
        if ($null -eq (Get-SettingsWindow)) { Start-Sleep -Milliseconds 400; $dialogs3 = @(Get-DialogWindows); break }
        Start-Sleep -Milliseconds 300
    }
}
$closed3 = Wait-Until { if (Get-SettingsWindow) { $null } else { $true } } 10000 300
$stampAfter3 = (Get-Item $settingsFile).LastWriteTimeUtc
Assert 'S3b' 'step S' 'a second save after an unrelated toggle closes cleanly with no dialog' `
    ($toggleOk -and $save2 -and $dialogs3.Count -eq 0 -and $null -ne $closed3) `
    'toggle off/on, Save, no dialog, window closes' `
    ("toggle={0} states={1} save={2} dialogs={3} closed={4} written={5}" -f $toggleOk, ($toggleStates -join '->'), $save2, $dialogs3.Count, ($null -ne $closed3), ($stampAfter3 -gt $stampBefore3))

Start-Sleep -Milliseconds 700
$jsonAfter3 = Get-SettingsJson
$audioAfter = [ordered]@{
    ProgramOutputDeviceId = $jsonAfter3.ProgramOutputDeviceId
    SecondOutputDeviceId  = $jsonAfter3.SecondOutputDeviceId
    MonitorOutputDeviceId = $jsonAfter3.MonitorOutputDeviceId
    DeckADeviceId         = $jsonAfter3.DeckADeviceId
    DeckBDeviceId         = $jsonAfter3.DeckBDeviceId
    DeckACueEnabled       = $jsonAfter3.DeckACueEnabled
    DeckBCueEnabled       = $jsonAfter3.DeckBCueEnabled
    MicDeviceId           = $jsonAfter3.MicDeviceId
    OutputDelayMs         = $jsonAfter3.OutputDelayMs
    TransitionMode        = $jsonAfter3.TransitionMode
}
$audioDiff = @()
foreach ($k in $audioBefore.Keys) { if ("$($audioBefore[$k])" -ne "$($audioAfter[$k])") { $audioDiff += "$k: '$($audioBefore[$k])' -> '$($audioAfter[$k])'" } }
Assert 'S3c' 'step S' 'the 2.0 audio fields survive the round trip unchanged' `
    ($audioDiff.Count -eq 0) `
    'ProgramOutputDeviceId, OutputDelayMs, MicDeviceId, TransitionMode unchanged' `
    ("diff={0}; Program={1} Delay={2} Mic={3} Mode={4}" -f $(if ($audioDiff.Count -eq 0) { 'none' } else { $audioDiff -join '; ' }), $audioAfter.ProgramOutputDeviceId, $audioAfter.OutputDelayMs, $audioAfter.MicDeviceId, $audioAfter.TransitionMode)

# --------------------------------- S4: routing combos list devices, keep choice
$setWin4 = Open-SettingsWindow
$setNodes4 = @(Get-SettingsNodes)
$combos = @($setNodes4 | Where-Object { $_.Type -eq 'ComboBox' })
$routingTop = -1
foreach ($n in $setNodes4) { if ($n.Type -eq 'Text' -and $n.Name -match '^AUDIO ROUTING') { $routingTop = (Get-ElBounds $n).Y; break } }
$routingBottom = 100000
foreach ($n in $setNodes4) {
    if ($n.Type -eq 'Text' -and $n.Name -match '^MICROPHONE') { $routingBottom = (Get-ElBounds $n).Y; break }
}
$routingCombos = @($combos | Where-Object { (Get-ElBounds $_).Y -gt $routingTop -and (Get-ElBounds $_).Y -lt $routingBottom })
$comboReport = @()
$comboOk = $true
$comboSelections = @()
foreach ($c in $routingCombos) {
    $label = Get-RowLabelNear $setNodes4 $c
    $sel = Get-ElValue $c
    if (-not $sel) { try { $sel = [string]$c.El.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name } catch { } }
    $items = @(Get-ComboItemNames $c)
    $comboSelections += [pscustomobject]@{ label = $label; value = $sel; items = $items.Count }
    $comboReport += ("{0}='{1}' ({2} item(s))" -f $label, $sel, $items.Count)
    if ($items.Count -lt 2 -or -not $sel) { $comboOk = $false }
    Log ("  routing combo [{0}] = '{1}', {2} item(s): {3}" -f $label, $sel, $items.Count, (($items | Select-Object -First 6) -join ', '))
}
Assert 'S4a' 'step S' 'every audio-routing combo lists devices and has a selection' `
    ($comboOk -and $routingCombos.Count -ge 5) `
    '>= 5 routing combos, each with >= 2 items and a selected value' `
    ("{0} combo(s): {1}" -f $routingCombos.Count, ($comboReport -join ' | '))

# Save from here so the selection is what the file carries, then compare on reopen.
$save4 = Invoke-El (Get-SettingsButton 'Save')
$null = Wait-Until { if (Get-SettingsWindow) { $null } else { $true } } 10000 300
$setWin5 = Open-SettingsWindow
$setNodes5 = @(Get-SettingsNodes)
$comboReport2 = @()
$comboOk2 = $true
foreach ($before in $comboSelections) {
    $found = $null
    foreach ($c in @($setNodes5 | Where-Object { $_.Type -eq 'ComboBox' })) {
        if ((Get-RowLabelNear $setNodes5 $c) -eq $before.label) { $found = $c; break }
    }
    if ($null -eq $found) { $comboOk2 = $false; $comboReport2 += ("{0}=MISSING" -f $before.label); continue }
    $sel = Get-ElValue $found
    if (-not $sel) { try { $sel = [string]$found.El.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name } catch { } }
    if ($sel -ne $before.value) { $comboOk2 = $false }
    $comboReport2 += ("{0}='{1}'->'{2}'" -f $before.label, $before.value, $sel)
}
Assert 'S4b' 'step S' 'routing selections survive save + reopen' $comboOk2 'every combo keeps its selected value' ($comboReport2 -join ' | ')

# ---------------------------------------- S5: invalid library folder is rejected
$setNodes6 = @(Get-SettingsNodes)
$musicBox = $null
foreach ($n in $setNodes6) { if ($n.Type -eq 'Edit' -and (Get-ElValue $n) -eq $musicPath) { $musicBox = $n; break } }
if ($null -eq $musicBox) { foreach ($n in $setNodes6) { if ($n.Type -eq 'Edit' -and (Get-RowLabelNear $setNodes6 $n) -match 'Music folder') { $musicBox = $n; break } } }
$jsonBeforeBad = Get-SettingsJsonRaw
$badSet = Set-ElValue $musicBox 'C:\NoSuchFolderXyz'
[void](Invoke-El (Get-SettingsButton 'Save'))
$badDialogs = @()
$sw5 = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw5.ElapsedMilliseconds -lt 8000) {
    $d5 = @(Get-DialogWindows)
    if ($d5.Count -gt 0) { $badDialogs = $d5; break }
    if ($null -eq (Get-SettingsWindow)) { break }
    Start-Sleep -Milliseconds 300
}
$badTitle = if ($badDialogs.Count -gt 0) { $badDialogs[0].Name } else { '' }
$badTexts = @()
foreach ($d in $badDialogs) {
    $tn = @(Get-Nodes -Root $d.El -Max 60 -Deep | Where-Object { $_.Type -eq 'Text' -and $_.Name -ne '' } | ForEach-Object { $_.Name })
    $badTexts += $tn
}
$badMsg = ($badTexts -join ' | ')
$jsonAfterBad = Get-SettingsJsonRaw
$warningOk = ($badDialogs.Count -ge 1) -and ($badMsg -match 'library folder does not exist')
$unchangedOk = ($jsonAfterBad -eq $jsonBeforeBad)
Assert 'S5a' 'step S' 'an invalid library folder is refused with the new warning' $warningOk 'a modal saying "The library folder does not exist."' ("title='{0}' text='{1}'" -f $badTitle, $badMsg)
Assert 'S5b' 'step S' 'the refused save leaves settings.json untouched' $unchangedOk 'file bytes identical' ("before={0} bytes after={1} bytes" -f $jsonBeforeBad.Length, $jsonAfterBad.Length)

# dismiss the warning, restore the real path, save for real
if ($badDialogs.Count -gt 0) {
    $ok = $null
    foreach ($n in @(Get-Nodes -Root $badDialogs[0].El -Max 40 -Deep | Where-Object { $_.Type -eq 'Button' })) { $ok = $n; break }
    if ($null -ne $ok) { [void](Invoke-El $ok); Log ("  dismissed the warning via '{0}'" -f $ok.Name) }
    $null = Wait-Until { if (@(Get-DialogWindows).Count -eq 0) { $true } else { $null } } 6000 300
}
$setNodes7 = @(Get-SettingsNodes)
$musicBox2 = $null
foreach ($n in $setNodes7) { if ($n.Type -eq 'Edit' -and (Get-RowLabelNear $setNodes7 $n) -match 'Music folder') { $musicBox2 = $n; break } }
if ($null -eq $musicBox2) { foreach ($n in $setNodes7) { if ($n.Type -eq 'Edit' -and (Get-ElValue $n) -eq 'C:\NoSuchFolderXyz') { $musicBox2 = $n; break } } }
$restored = Set-ElValue $musicBox2 $musicPath
[void](Invoke-El (Get-SettingsButton 'Save'))
$closed5 = Wait-Until { if (Get-SettingsWindow) { $null } else { $true } } 10000 300
Start-Sleep -Milliseconds 800
$jsonRestored = Get-SettingsJson
Assert 'S5c' 'step S' 'restoring the real path saves cleanly again' `
    ($restored -and ($null -ne $closed5) -and $jsonRestored.MusicDirectory -eq $musicPath -and $jsonRestored.KaraokeDirectory -eq $musicPath) `
    ("window closes and both roots read '{0}'" -f $musicPath) `
    ("restored={0} closed={1} MusicDirectory='{2}' KaraokeDirectory='{3}'" -f $restored, ($null -ne $closed5), $jsonRestored.MusicDirectory, $jsonRestored.KaraokeDirectory)

# leave the app exactly as a user would: settings closed, main window up
Close-SettingsWindow
foreach ($d in @(Get-DialogWindows)) { Log ("  ! window still open at the end of step S: '{0}'" -f $d.Name) }

# Plain strings only: the report is serialised with ConvertTo-Json at the end.
$script:Reads['settings'] = @(
    "file=$settingsFile",
    "music=$($jsonRestored.MusicDirectory)",
    "karaoke=$($jsonRestored.KaraokeDirectory)",
    ("combos: " + (($comboSelections | ForEach-Object { "$($_.label)='$($_.value)' ($($_.items) item(s))" }) -join ' | ')),
    ("warning: " + $(if ($badMsg) { $badMsg } else { '(none)' }))
)
Log ("  step S done: singleRoot={0} save={1} roundTrip={2} combos={3}" -f $sameRoots, ($dialogs.Count -eq 0), ($audioDiff.Count -eq 0), $routingCombos.Count)
