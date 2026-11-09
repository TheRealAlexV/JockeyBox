<#
.SYNOPSIS
    End-to-end proof that JockeyBox 2.0 loads, plays, tempo-shifts and hands off a
    real library track through the deployed audio graph.

.DESCRIPTION
    Drives the *running* app through UI Automation only - no product code changes,
    no debug hooks - and proves:

      1. a real library track loads onto deck A and its readout advances (> 0:03)
      2. the deck A tempo fader at +6 % changes TempoRatio/EffectiveBpm and the track
         keeps advancing
      3. the queue hands off across a transition: the LOG shows "transition A->B ...
         fade=", the queued row becomes current and deck B advances
      4. the crossfader moved programmatically (RangeValue before/after)
      5. remote-row parity: the Spotify/YouTube row menu carries "Add to playlist"
         and writes into an internal playlist (status line + playlist row count),
         the rows show the karaoke state of their local counterpart, and the write
         survives a restart
      6. a screenshot per step plus machine-readable e2e.json with PASS/FAIL

    MUST run in Session 1 (scheduled task, /ru Alex /it): win/winps ssh land in
    Session 0 and cannot see the app's windows.

    Save this file UTF-8 **with BOM** - PowerShell reads a BOM-less .ps1 as ANSI and
    mangles the glyphs the automation names are matched on. To stay independent of
    that hazard the transport buttons are located by document order around the
    PlayButton automation id, and the Load button by its "Load" prefix.

    Implementation notes learned the hard way on this host:
      * AutomationElement.FindAll(Descendants, ...) is unreliable here (returns 0 for
        Button/Text/Slider while the tree is plainly populated), so every lookup
        walks with TreeWalker.ControlViewWalker and filters in managed code.
      * The window sits at negative X (multi-monitor), so nothing is filtered by
        absolute coordinates; ordering is document order only.
      * The library tree is artist -> album -> track and rows only materialise when
        expanded; a "leaf" is a row with no Expander child. The filter text persists
        across restarts, so it is cleared first.
      * Seeking uses the transport's own +10 s button (a real SeekDeck); the seek
        sliders only commit on mouse-up, which is coordinate-fragile.

.PARAMETER OutDir
    Where the screenshots and e2e.json land (default C:\Dev\shots).

.EXAMPLE
    schtasks /create /tn JB-E2E /tr "cmd /c powershell -NoProfile -ExecutionPolicy Bypass -File C:\Dev\jb-e2e.ps1 > C:\Dev\shots\e2e-run.log 2>&1" /sc once /st 23:59 /rl highest /ru Alex /it /f
    schtasks /run /tn JB-E2E
#>
[CmdletBinding()]
param(
    [string]$OutDir           = 'C:\Dev\shots',
    [string]$Artist           = 'Luke Muzzic',
    [string]$TrackAName       = 'Instrumental',
    [string]$TrackBName       = 'slowed and muffled',
    [double]$TempoPercent     = 6.0,
    [int]$SeekToWithinSeconds = 6,
    [int]$ArmTimeoutSeconds   = 30,
    [int]$FadeTimeoutSeconds  = 200,
    # An armed transition plan keeps firing minutes later (it is scheduled on the
    # engine clock and nothing cancels it), and the app restores decks/queue from
    # the previous run, so a repeatable run starts from a fresh process.
    [bool]$Restart            = $true,
    # Which remote tab carries R1-R6. SPOTIFY needs a connected account; on a host
    # without one the YouTube tab is the live surface and Spotify reports S1..S5.
    [ValidateSet('SPOTIFY', 'YOUTUBE')]
    [string]$RemotePrimaryTab = 'YOUTUBE'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$script:LogLines   = New-Object System.Collections.ArrayList
$script:Assertions = New-Object System.Collections.ArrayList
$script:Reads      = @{}
$script:AppLogPath = Join-Path $env:APPDATA 'JockeyBox\logs\jockeybox.log'
$script:LogStart   = 0
$script:Win        = $null
$script:Nodes      = $null
$script:NodesAt    = [datetime]::MinValue
$script:Shots      = New-Object System.Collections.ArrayList

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$JsonPath = Join-Path $OutDir 'e2e.json'
$DonePath = Join-Path $OutDir 'e2e.done'
Remove-Item $DonePath -ErrorAction SilentlyContinue

function Log([string]$m) {
    $line = ('{0:HH:mm:ss.fff} {1}' -f (Get-Date), $m)
    [void]$script:LogLines.Add($line)
    Write-Host $line
}

function Assert([string]$Id, [string]$Step, [string]$Name, $Ok, $Expected, $Actual) {
    [void]$script:Assertions.Add([pscustomobject]@{
            id = $Id; step = $Step; assertion = $Name
            pass = $Ok; expected = "$Expected"; actual = "$Actual"
        })
    $tag = if ($Ok -eq $true) { 'PASS' } elseif ($Ok -eq $false) { 'FAIL' } else { 'INFO' }
    Log ("[{0}] {1} {2} :: expected={3} actual={4}" -f $tag, $Id, $Name, $Expected, $Actual)
}

function Write-Result([string]$Fatal) {
    $pass = @($script:Assertions | Where-Object { $_.pass -eq $true }).Count
    $fail = @($script:Assertions | Where-Object { $_.pass -eq $false }).Count
    $info = @($script:Assertions | Where-Object { $null -eq $_.pass }).Count
    $summary = [pscustomobject]@{
        generatedAt = (Get-Date).ToString('s')
        machine     = $env:COMPUTERNAME
        fatal       = $Fatal
        verdict     = $(if ($fail -eq 0 -and -not $Fatal) { 'PASS' } else { 'FAIL' })
        parameters  = [pscustomobject]@{
            artist = $Artist; trackA = $TrackAName; trackB = $TrackBName; tempoPercent = $TempoPercent
        }
        counts      = [pscustomobject]@{ pass = $pass; fail = $fail; info = $info }
        assertions  = $script:Assertions
        reads       = $script:Reads
        screenshots = @($script:Shots)
        logFile     = $script:AppLogPath
        consoleLog  = $script:LogLines
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $JsonPath -Encoding UTF8
    Set-Content -Path $DonePath -Value $(if ($fail -eq 0 -and -not $Fatal) { '0' } else { '1' })
    Log ("summary written: {0} ({1} pass / {2} fail / {3} info)" -f $JsonPath, $pass, $fail, $info)
    return $(if ($fail -eq 0 -and -not $Fatal) { 0 } else { 1 })
}

trap {
    $msg = "$($_.Exception.Message) @ $($_.InvocationInfo.PositionMessage)"
    try { Log "FATAL: $msg" } catch { }
    $code = Write-Result $msg
    exit $code
}

# ============================================================ UIA tree walking

# UIA calls against a window that repaints at 30 Hz fail intermittently with
# E_UNEXPECTED ("Catastrophic failure"), so every provider call is guarded and a
# failed subtree is skipped rather than aborting the run.
function Walk-Node($el, [int]$depth) {
    if ($script:WL.Count -ge $script:WMax) { return $true }
    $script:WSeen++
    $t = ''
    try { $t = ([string]$el.Current.ControlType.ProgrammaticName) -replace '^ControlType\.', '' } catch { $script:WErr++; return $false }
    if ($script:WType -eq '' -or $t -eq $script:WType) {
        $n = ''; $i = ''
        try { $n = [string]$el.Current.Name } catch { $script:WErr++ }
        try { $i = [string]$el.Current.AutomationId } catch { }
        if ($script:WName -eq '' -or $n -like $script:WName) {
            [void]$script:WL.Add([pscustomobject]@{ El = $el; Type = $t; Name = $n; Id = $i })
            if ($script:WFirst -gt 0 -and $script:WL.Count -ge $script:WFirst) { return $true }
        }
    }
    if ($depth -ge 32) { return $false }
    # Chrome reads skip the heavy containers: their contents are read by their own
    # targeted walks (the library tree, the queue list, the tab strip), otherwise a
    # single walk of the expanded library blows past the node budget before it ever
    # reaches the QUEUED tab.
    if (-not $script:WDeep -and $depth -gt 0 -and $script:WCut -contains $t) { return $false }
    $c = $null
    try { $c = $script:WWalker.GetFirstChild($el) } catch { $script:WErr++; return $false }
    while ($null -ne $c) {
        if (Walk-Node $c ($depth + 1)) { return $true }
        try { $c = $script:WWalker.GetNextSibling($c) } catch { $script:WErr++; $c = $null }
    }
    return $false
}

# Ordered (document-order) node list under $Root. The whole walk is retried when
# the UIA provider dropped a subtree, because the app's 30 Hz repaint makes a
# single walk occasionally come back short.
function Get-Nodes {
    param($Root = $null, [int]$Max = 400, [string]$Type = '', [string]$Name = '', [int]$First = 0, [switch]$Deep)
    if ($null -eq $Root) { $Root = $script:Win }
    if ($null -eq $Root) { return @() }
    $best = @()
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $script:WL = New-Object System.Collections.ArrayList
        $script:WMax = $Max
        $script:WType = $Type
        $script:WName = $Name
        $script:WFirst = $First
        $script:WSeen = 0
        $script:WErr = 0
        $script:WDeep = [bool]$Deep
        $script:WCut = @('Tab', 'Tree', 'List', 'DataGrid', 'Table')
        $script:WWalker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        [void](Walk-Node $Root 0)
        if ($script:WL.Count -gt $best.Count) { $best = @($script:WL) }
        if ($script:WErr -eq 0) { break }
        Log ("  ! UIA walk {0} hit {1} provider error(s) ({2} node(s) read); retrying" -f $attempt, $script:WErr, $script:WL.Count)
        Start-Sleep -Milliseconds 250
    }
    return $best
}

# Cached whole-window node list (deck cards, crossfader, transport, tabs, toolbar).
function Get-WinNodes([switch]$Force) {
    $age = ((Get-Date) - $script:NodesAt).TotalMilliseconds
    if (-not $Force -and $null -ne $script:Nodes -and $age -lt 300) { return $script:Nodes }
    $script:Nodes = Get-Nodes -Max 420
    $script:NodesAt = Get-Date
    return $script:Nodes
}

function Find-Node {
    param([string]$Type = '', [string]$Name = '', [string]$Id = '', [string]$NameLike = '', [int]$Index = 0, [switch]$Force)
    $nodes = Get-WinNodes -Force:$Force
    $m = @($nodes | Where-Object {
            ($Type -eq '' -or $_.Type -eq $Type) -and
            ($Id -eq '' -or $_.Id -eq $Id) -and
            ($Name -eq '' -or $_.Name -eq $Name) -and
            ($NameLike -eq '' -or $_.Name -like $NameLike) })
    if ($m.Count -le $Index) { return $null }
    return $m[$Index]
}

function Find-Nodes {
    param([string]$Type = '', [string]$Name = '', [string]$NameLike = '')
    $nodes = Get-WinNodes -Force
    return @($nodes | Where-Object {
            ($Type -eq '' -or $_.Type -eq $Type) -and
            ($Name -eq '' -or $_.Name -eq $Name) -and
            ($NameLike -eq '' -or $_.Name -like $NameLike) })
}

function Invoke-El($Node) {
    if ($null -eq $Node) { return $false }
    try {
        $Node.El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    } catch {
        Log ("  ! invoke '{0}' failed: {1}" -f $Node.Name, $_.Exception.Message)
        return $false
    }
}

function Select-El($Node, [int]$Tries = 2) {
    if ($null -eq $Node) { return $false }
    for ($i = 1; $i -le $Tries; $i++) {
        try { $Node.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {
            try { $Node.El.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Select() } catch { }
        }
        Start-Sleep -Milliseconds 220
        # A pattern call can succeed without the control reacting (the tab strip
        # in particular), so selection is always confirmed by reading it back.
        try {
            $sel = [bool]$Node.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
            if ($sel) { return $true }
        } catch {
            # no selection pattern to read back (not a selectable control)
            return $true
        }
    }
    Log ("  ! select '{0}' reported no selection after {1} attempts" -f $Node.Name, $Tries)
    return $false
}

function Get-Range($Node) {
    if ($null -eq $Node) { return $null }
    try { return $Node.El.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern) } catch { return $null }
}

function Get-RangeVal($Node) {
    $p = Get-Range $Node
    if ($null -eq $p) { return $null }
    return [double]$p.Current.Value
}

function Wait-Until([scriptblock]$Probe, [int]$TimeoutMs, [int]$StepMs = 300) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try { $v = & $Probe; if ($v) { return $v } } catch { }
        Start-Sleep -Milliseconds $StepMs
    }
    return $null
}

function Shot([string]$Name) {
    try {
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $path = Join-Path $OutDir $Name
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Log "  screenshot: $path"
        [void]$script:Shots.Add($path)
        return $path
    } catch {
        Log "  ! screenshot failed: $($_.Exception.Message)"
        return $null
    }
}

# ============================================================ app-specific reads

function Find-Window {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'JockeyBox')
    $w = $AE::RootElement.FindFirst($TS::Children, $c)
    if ($null -eq $w) {
        $proc = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) {
            $pc = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
            $w = $AE::RootElement.FindFirst($TS::Children, $pc)
        }
    }
    return $w
}

$RePos = '^(\d{1,2}):(\d{2})(?::(\d{2}))? / (\d{1,2}):(\d{2})(?::(\d{2}))?$'

function Parse-Span([string]$s) {
    if (-not $s) { return $null }
    $m = [regex]::Match($s, $RePos)
    if (-not $m.Success) { return $null }
    $pos = [int]$m.Groups[1].Value * 60 + [int]$m.Groups[2].Value
    if ($m.Groups[3].Success) { $pos += [int]$m.Groups[1].Value * 3600 }
    $dur = [int]$m.Groups[4].Value * 60 + [int]$m.Groups[5].Value
    if ($m.Groups[6].Success) { $dur += [int]$m.Groups[4].Value * 3600 }
    return [pscustomobject]@{ pos = $pos; dur = $dur; text = $s }
}

# Deck cards come first in document order: deck A's readouts are index 0, deck B's 1.
function Get-Deck([int]$Index) {
    $nodes = Get-WinNodes -Force
    $texts = @($nodes | Where-Object { $_.Type -eq 'Text' })
    $pos = @($texts | Where-Object { $_.Name -match $RePos })
    # the deck card renders "114.1 BPM" under the tempo slider
    $bpm = @($texts | Where-Object { $_.Name -match '^[0-9]+\.?[0-9]* BPM$' })
    $rat = @($texts | Where-Object { $_.Name -match '^[0-9]\.[0-9]{3}$' })
    $span = $null
    $title = $null
    if ($pos.Count -gt $Index) {
        $span = Parse-Span $pos[$Index].Name
        $idx = $texts.IndexOf($pos[$Index])
        if ($idx -ge 0 -and ($idx + 1) -lt $texts.Count) { $title = $texts[$idx + 1].Name }
    }
    return [pscustomobject]@{
        position = $span
        title    = $title
        bpm      = $(if ($bpm.Count -gt $Index) { $bpm[$Index].Name } else { $null })
        ratio    = $(if ($rat.Count -gt $Index) { $rat[$Index].Name } else { $null })
    }
}

# Polling reads: resolving a readout element once and then reading its Name is
# ~50x cheaper than a full walk and far less likely to hit a provider error while
# the app repaints at 30 Hz. The element survives text updates; it is dropped and
# re-resolved when a read throws or stops looking like a position.
$script:SpanEl = @{}

function Get-Span([int]$Index) {
    $cached = $script:SpanEl[$Index]
    if ($null -ne $cached) {
        try {
            $t = [string]$cached.Current.Name
            if ($t -match $RePos) { return Parse-Span $t }
        } catch { }
        $script:SpanEl.Remove($Index)
    }
    $nodes = Get-WinNodes -Force
    $pos = @($nodes | Where-Object { $_.Type -eq 'Text' -and $_.Name -match $RePos })
    if ($pos.Count -le $Index) { return $null }
    $script:SpanEl[$Index] = $pos[$Index].El
    return Parse-Span $pos[$Index].Name
}

# The app can be redeployed (force-stopped and relaunched) by another agent at any
# moment; a changed pid invalidates every cached AutomationElement, so this is
# checked at step boundaries and in polling loops.
$script:Restarted = $false
function Test-AppAlive {
    $p = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $p) { return $false }
    if ($null -ne $script:AppPid -and $p.Id -ne $script:AppPid) {
        if (-not $script:Restarted) {
            Log ("  ! app instance changed (pid {0} -> {1}): it was redeployed mid-run" -f $script:AppPid, $p.Id)
            $script:Restarted = $true
        }
        return $false
    }
    return $true
}

$script:CfEl = $null
function Get-Crossfader {
    if ($null -ne $script:CfEl) {
        try {
            $p = $script:CfEl.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
            return [double]$p.Current.Value
        } catch { $script:CfEl = $null }
    }
    $n = Find-Node -Type 'Slider' -Id 'CrossfaderSlider' -Force
    if ($null -eq $n) { return $null }
    $script:CfEl = $n.El
    return Get-RangeVal $n
}

# The shared transport strip: [... ▶(PlayButton) ⏹ ⏩ ...]. Located by document order
# around the PlayButton automation id so no glyph has to survive encoding.
function Get-TransportButtons([switch]$Force) {
    $nodes = Get-WinNodes -Force:$Force
    $idx = -1
    for ($i = 0; $i -lt $nodes.Count; $i++) {
        if ($nodes[$i].Id -eq 'PlayButton') { $idx = $i; break }
    }
    if ($idx -lt 0) { return $null }
    $before = $null; $play = $nodes[$idx]; $after = @()
    for ($i = $idx - 1; $i -ge 0; $i--) { if ($nodes[$i].Type -eq 'Button') { $before = $nodes[$i]; break } }
    for ($i = $idx + 1; $i -lt $nodes.Count -and $after.Count -lt 3; $i++) {
        if ($nodes[$i].Type -eq 'Button') { $after += $nodes[$i] }
    }
    return [pscustomobject]@{
        rewind = $before
        play   = $play
        stop   = $(if ($after.Count -ge 1) { $after[0] } else { $null })
        fastFwd = $(if ($after.Count -ge 2) { $after[1] } else { $null })
    }
}

# --- tab-scoped reads -------------------------------------------------------
# Get-WinNodes stops the walk at the Tab container on purpose (the expanded
# library would eat the whole node budget), so nothing *inside* LIBRARY or
# QUEUED is visible from the window walk. Everything in there is looked up
# scoped to its tab item instead.

function Get-LibraryTab {
    foreach ($t in (Get-TabItems)) { if ($t.Name -eq 'LIBRARY') { return $t } }
    return $null
}

# Widgets in the LIBRARY tab outside the heavy containers (toolbar buttons, the
# filter box, the playlist rail). The walk stops at List/Tree, so it is cheap.
function Get-LibraryNodes([string]$Type = '', [int]$Max = 260) {
    $tab = Get-LibraryTab
    if ($null -eq $tab) { return @() }
    return @(Get-Nodes -Root ($tab.El) -Max $Max -Type $Type)
}

function Get-LibraryButton([string]$NameLike) {
    return @(Get-LibraryNodes -Type 'Button' | Where-Object { $_.Name -like $NameLike }) | Select-Object -First 1
}

function Get-TrackTree {
    $tab = Get-LibraryTab
    if ($null -eq $tab) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'TrackTree')
    for ($i = 1; $i -le 3; $i++) {
        try {
            $e = $tab.El.FindFirst($TS::Descendants, $cond)
            if ($null -ne $e) { return [pscustomobject]@{ El = $e; Type = 'Tree'; Name = 'TrackTree'; Id = 'TrackTree' } }
        } catch { }
        # provider hiccup: fall back to the managed walk rooted at the tab
        $hit = @(Get-Nodes -Root ($tab.El) -Max 90 -Type 'Tree' | Where-Object { $_.Id -eq 'TrackTree' }) | Select-Object -First 1
        if ($null -ne $hit) { return $hit }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Get-RowInfo($RowNode) {
    $kids = Get-Nodes -Root ($RowNode.El) -Max 40
    $hasExpander = @($kids | Where-Object { $_.Type -eq 'Button' -and $_.Id -eq 'Expander' }).Count -gt 0
    $state = ''
    try {
        $p = $RowNode.El.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $state = [string]$p.Current.ExpandCollapseState
    } catch { $state = '' }
    return [pscustomobject]@{ Node = $RowNode; State = $state; HasExpander = $hasExpander }
}

function Get-TrackRows([int]$MaxRows = 3000) {
    $tree = Get-TrackTree
    if ($null -eq $tree) { return @() }
    return Get-Nodes -Root ($tree.El) -Max $MaxRows -Type 'TreeItem'
}

function Expand-Library {
    for ($pass = 1; $pass -le 6; $pass++) {
        $rows = @(Get-TrackRows)
        $did = 0
        foreach ($r in $rows) {
            $info = Get-RowInfo $r
            if ($info.State -eq 'Collapsed') {
                try {
                    $r.El.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                    $did++
                } catch { }
            }
        }
        Log ("  expand pass {0}: {1} row(s), {2} expanded" -f $pass, $rows.Count, $did)
        Start-Sleep -Milliseconds 350
        if ($did -eq 0) { break }
    }
}

function Find-TrackRow([string]$Needle) {
    foreach ($r in (Get-TrackRows)) {
        if ($r.Name -notlike "*$Needle*") { continue }
        $info = Get-RowInfo $r
        if (-not $info.HasExpander) { return $r }
    }
    return $null
}

function Set-Filter([string]$Text) {
    $box = @(Get-LibraryNodes -Type 'Edit') | Select-Object -First 1
    if ($null -eq $box) { Log '  ! filter box not found in the LIBRARY tab'; return $false }
    try {
        $box.El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
        return $true
    } catch { Log ("  ! filter set failed: {0}" -f $_.Exception.Message); return $false }
}

# The playlist Filter box is bound to LibraryFilter; a search that matches nothing
# would leave the tree empty, so wait for it to hold what was typed.
function Wait-Filter([string]$Text) {
    $box = @(Get-LibraryNodes -Type 'Edit') | Select-Object -First 1
    if ($null -eq $box) { return $false }
    try { return ([string]$box.El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $Text) } catch { return $false }
}

function Get-DeckTempoSlider([int]$Index) {
    $nodes = Get-WinNodes -Force
    $tempo = @()
    foreach ($s in @($nodes | Where-Object { $_.Type -eq 'Slider' })) {
        $p = Get-Range $s
        if ($null -eq $p) { continue }
        $mn = [double]$p.Current.Minimum; $mx = [double]$p.Current.Maximum
        if ($mn -le -5 -and $mx -ge 5 -and $mx -le 40) { $tempo += $s }
    }
    if ($tempo.Count -le $Index) { return $null }
    return $tempo[$Index]
}

function Set-Tempo([int]$DeckIndex, [double]$Percent) {
    $s = Get-DeckTempoSlider $DeckIndex
    if ($null -eq $s) { return $false }
    try {
        $s.El.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]$Percent)
        Start-Sleep -Milliseconds 400
        return $true
    } catch { Log ("  ! tempo set failed: {0}" -f $_.Exception.Message); return $false }
}

# Each deck card carries its own play/pause button; the shared transport strip
# acts on the ViewModel's "active" deck, and LoadTrack reassigns that whenever it
# pre-loads the armed track, so the card button is the only unambiguous target.
function Get-DeckPlayButton([int]$Index) {
    $glyphs = @(Get-WinNodes -Force | Where-Object {
        $_.Type -eq 'Button' -and $_.Name.Length -eq 1 -and
        ([int][char]$_.Name -eq 0x25B6 -or [int][char]$_.Name -eq 0x23F8)
    })
    if ($glyphs.Count -le $Index) { return $null }
    return $glyphs[$Index]
}

function Get-QueueRows {
    $qtab = Get-QueueTab
    if ($null -eq $qtab) { return @() }
    $nodes = Get-Nodes -Root ($qtab.El) -Max 160 -Deep
    return @($nodes | Where-Object { $_.Type -eq 'ListItem' -or $_.Type -eq 'DataItem' })
}

function Get-QueueButton([string]$Name) {
    $qtab = Get-QueueTab
    if ($null -eq $qtab) { return $null }
    return @(Get-Nodes -Root ($qtab.El) -Max 160 -Deep -Type 'Button' | Where-Object { $_.Name -eq $Name }) | Select-Object -First 1
}

# The browser tab strip is walked on its own: the chrome walk stops at the Tab
# container so the expanded library tree cannot consume the node budget first.
function Get-TabItems {
    $tab = Find-Node -Type 'Tab' -Id 'BrowserTabs'
    if ($null -eq $tab) { $tab = Find-Node -Type 'Tab' }
    if ($null -eq $tab) { return @() }
    return @(Get-Nodes -Root ($tab.El) -Max 40 -Type 'TabItem')
}

function Get-QueueTab {
    foreach ($t in (Get-TabItems)) { if ($t.Name -like 'QUEUED*') { return $t } }
    return $null
}

function Read-AppLogSince([int]$StartIndex) {
    if (-not (Test-Path $script:AppLogPath)) { return @() }
    $all = @(Get-Content -Path $script:AppLogPath -ErrorAction SilentlyContinue)
    if ($StartIndex -ge $all.Count) { return @() }
    return @($all[$StartIndex..($all.Count - 1)])
}

# ================================================================ preflight

Log '=== jb-e2e: JockeyBox 2.0 audio-graph end-to-end verification ==='
Log ("output dir: {0}" -f $OutDir)

$exePath = 'C:\Dev\JockeyBox\JockeyBox.exe'
if (Test-Path $exePath) {
    $fi = Get-Item $exePath
    $script:Reads['deployedExe'] = [pscustomobject]@{
        path = $exePath; size = $fi.Length; built = $fi.LastWriteTime.ToString('s')
        sha256 = (Get-FileHash $exePath -Algorithm SHA256).Hash
    }
    Log ("deployed exe: {0} {1} bytes, built {2}" -f $fi.FullName, $fi.Length, $fi.LastWriteTime.ToString('s'))
}

# Graceful close (WM_CLOSE) so the settings/scene files flush, then relaunch.
function Close-AppWindow {
    $w = Find-Window
    if ($null -eq $w) { return $false }
    try { $h = [intptr]$w.Current.NativeWindowHandle } catch { return $false }
    if ($h -eq [intptr]::Zero) { return $false }
    if (-not ([System.Management.Automation.PSTypeName]'JbNative.Win32').Type) {
        Add-Type -Namespace JbNative -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
'@
    }
    return [JbNative.Win32]::PostMessage($h, 0x0010, [intptr]::Zero, [intptr]::Zero)
}

if ($Restart) {
    Log '--- restarting the app for a clean state'
    $running = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running) {
        $closed = Close-AppWindow
        Log ("  WM_CLOSE posted: {0} (pid {1})" -f $closed, $running.Id)
        $gone = Wait-Until { if (Get-Process -Id $running.Id -ErrorAction SilentlyContinue) { $null } else { $true } } 25000 500
        if ($null -eq $gone) {
            Log '  graceful close timed out - force stopping'
            Stop-Process -Id $running.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
    if (Test-Path $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) | Out-Null
        $script:SpanEl = @{}; $script:CfEl = $null
        Log '  relaunched; waiting for the window'
        $null = Wait-Until { Find-Window } 90000 1000
        Start-Sleep -Seconds 2
    }
}

$proc = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
$procInfo = if ($proc) { [pscustomobject]@{ pid = $proc.Id; start = $proc.StartTime.ToString('s') } } else { $null }
$script:Reads['process'] = $procInfo
$script:AppPid = if ($procInfo) { $procInfo.pid } else { $null }
if ($procInfo) { Log ("process: pid {0} started {1}" -f $procInfo.pid, $procInfo.start) }

$script:Win = Wait-Until { Find-Window } 20000 500
Assert 'P0' 'preflight' 'app window visible to UIA' ($null -ne $script:Win) 'a JockeyBox window' $(if ($script:Win) { 'found' } else { 'not found' })
if ($null -eq $script:Win) { exit (Write-Result 'app window not found') }
Log ("window rect: {0}" -f $script:Win.Current.BoundingRectangle)

# A fresh process rebuilds the window, so the cards appear a moment later. A
# restored deck with no media legitimately reads 0:00 / 0:00, so only the cards
# themselves are required here.
$restored = Wait-Until {
    $a = Get-Deck 0; $b = Get-Deck 1
    if ($null -ne $a.position -and $null -ne $b.position) {
        [pscustomobject]@{ A = $a; B = $b }
    } else { $null }
} 45000 1000
Assert 'P1' 'preflight' 'window exposes both 2.0 deck cards' ($null -ne $restored) 'two deck cards with durations' $(if ($restored) { "A='$($restored.A.position.text)' bpm='$($restored.A.bpm)' | B='$($restored.B.position.text)' bpm='$($restored.B.bpm)'" } else { 'readouts still blank after 30 s' })

if (Test-Path $script:AppLogPath) { $script:LogStart = @(Get-Content $script:AppLogPath).Count }
Log ("app log marker: {0} line(s) already present" -f $script:LogStart)

# ============================================================= step 1: load + play

Log '--- step 1: load a library track onto deck A and play it'

$libTab = $null
$tabNames = @()
foreach ($t in (Get-TabItems)) { $tabNames += $t.Name; if ($t.Name -eq 'LIBRARY') { $libTab = $t } }
Log ("  tabs: {0}" -f ($tabNames -join ' | '))
Assert 'P2' 'preflight' 'LIBRARY tab present' ($null -ne $libTab) 'LIBRARY tab' ("tabs: $($tabNames -join ', ')")
if ($null -eq $libTab) { exit (Write-Result 'LIBRARY tab not found') }
$libSelected = Select-El $libTab
Assert 'P2a' 'preflight' 'LIBRARY tab is the selected tab' $libSelected 'IsSelected = true' "$libSelected"

# ---- step 0: reset the persisted state -------------------------------------
# Decks, queue, filter, playlist and tempo all survive a restart, so every run
# starts from a known state instead of inheriting the previous run's leftovers.

function Get-DeckButtons {
    return @(Get-WinNodes -Force | Where-Object { $_.Type -eq 'Button' -and ($_.Name -eq 'ACTIVE' -or $_.Name -eq 'SET ACTIVE') })
}

function Set-ActiveDeck([int]$Index) {
    $btn = @(Get-DeckButtons)
    if ($btn.Count -le $Index) { return $false }
    if ($btn[$Index].Name -eq 'ACTIVE') { return $true }
    [void](Invoke-El $btn[$Index])
    Start-Sleep -Milliseconds 700
    $now = @(Get-DeckButtons)
    return ($now.Count -gt $Index -and $now[$Index].Name -eq 'ACTIVE')
}

function Stop-ActiveDeck {
    $tp = Get-TransportButtons
    if ($null -eq $tp -or $null -eq $tp.stop) { return $false }
    [void](Invoke-El $tp.stop)
    Start-Sleep -Milliseconds 900
    return $true
}

# The app restores the previous run's queue and can resume it while the window is
# still coming up, so silence is imposed twice and then verified by watching the
# readouts instead of trusting the button presses.
$rA = $false; $rB = $false; $rA2 = $false
for ($pass = 1; $pass -le 2; $pass++) {
    $rA = Set-ActiveDeck 0; [void](Stop-ActiveDeck)
    $rB = Set-ActiveDeck 1; [void](Stop-ActiveDeck)
    $rA2 = Set-ActiveDeck 0
    Start-Sleep -Milliseconds 800
}
$s1 = Get-Span 0; $s2 = Get-Span 1
Start-Sleep -Milliseconds 2500
$s3 = Get-Span 0; $s4 = Get-Span 1
$silentA = ($null -eq $s1 -or $null -eq $s3 -or $s3.pos -le $s1.pos)
$silentB = ($null -eq $s2 -or $null -eq $s4 -or $s4.pos -le $s2.pos)
Assert 'R1' 'step 0' 'nothing is playing after the stop passes' ($rA -and $rB -and $rA2 -and $silentA -and $silentB) 'both positions static for 2.5 s' ("A=$(if ($s3) { $s3.text } else { 'n/a' }) B=$(if ($s4) { $s4.text } else { 'n/a' })")

# The playlist rail is a List, and the chrome walk stops at it; this finds the
# "ALL TRACKS" entry by scanning each List body inside the LIBRARY tab (cheap -
# the rail has ~10 rows).
function Find-RailText([string]$Like) {
    foreach ($l in @(Get-LibraryNodes -Type 'List')) {
        $hit = @(Get-Nodes -Root ($l.El) -Max 150 -Deep -Type 'Text') |
            Where-Object { $_.Name -like $Like } | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $null
}

# the playlist rail row's Button has no Name; its text child carries "ALL TRACKS"
$allTracksBtn = $null
$railText = Wait-Until { Find-RailText 'ALL TRACKS*' } 10000 500
if ($null -ne $railText) {
    $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($railText.El)
    while ($null -ne $parent -and $parent.Current.ControlType -ne $CT::Button) {
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
    }
    if ($null -ne $parent) {
        $allTracksBtn = [pscustomobject]@{ El = $parent; Type = 'Button'; Name = 'ALL TRACKS'; Id = '' }
    }
}
if ($allTracksBtn) { [void](Invoke-El $allTracksBtn); Log '  selected the ALL TRACKS rail row'; Start-Sleep -Milliseconds 900 }
Assert 'R2' 'step 0' 'ALL TRACKS playlist selected' ($null -ne $allTracksBtn) 'rail row' $(if ($allTracksBtn) { 'clicked' } else { 'missing' })

[void](Set-Filter '')
Log '  cleared the library filter (it persists across restarts)'
$rowsAll = Wait-Until { $r = @(Get-TrackRows); if ($r.Count -gt 3) { $r.Count } else { $null } } 15000
Assert 'P3' 'preflight' 'library tree populated after clearing the filter' ($null -ne $rowsAll) 'more than 3 rows' ("$rowsAll rows")

# empty the queue so step 3 owns its rows
$qtab0 = Get-QueueTab
if ($null -ne $qtab0) {
    [void](Select-El $qtab0)
    Start-Sleep -Milliseconds 700
    $clearBtn = Get-QueueButton 'Clear'
    if ($null -ne $clearBtn) { [void](Invoke-El $clearBtn); Start-Sleep -Milliseconds 800; Log '  cleared the queue' }
    $left = @(Get-QueueRows).Count
    Assert 'R3' 'step 0' 'queue emptied before the run' ($left -eq 0) '0 rows' ("$left row(s) after Clear; tab='$($qtab0.Name)'")
}
[void](Select-El $libTab)
Start-Sleep -Milliseconds 600

# tempo faders back to 0 % so step 2 measures a real change
$tempoA0 = Get-RangeVal (Get-DeckTempoSlider 0)
$tempoB0 = Get-RangeVal (Get-DeckTempoSlider 1)
[void](Set-Tempo 0 0.0)
[void](Set-Tempo 1 0.0)
Start-Sleep -Milliseconds 600
$cfSnap = Find-Node -Type 'Button' -Name 'Crossfader A' -Force
if ($null -ne $cfSnap) { [void](Invoke-El $cfSnap); Start-Sleep -Milliseconds 700 }
$cfReset = Get-Crossfader
Assert 'R5' 'step 0' 'crossfader snapped to deck A (0.0)' ($null -ne $cfReset -and $cfReset -le 0.02) 'CrossfaderSlider ~ 0' "$cfReset"

Assert 'R4' 'step 0' 'tempo faders reset to 0 %' ((Get-RangeVal (Get-DeckTempoSlider 0)) -eq 0) '0' ("before A=$tempoA0 B=$tempoB0 -> A=$(Get-RangeVal (Get-DeckTempoSlider 0))")

[void](Set-Filter $Artist)
Start-Sleep -Milliseconds 900
$rowsFiltered = Wait-Until {
    $r = @(Get-TrackRows)
    if ($r.Count -gt 0 -and $r.Count -lt 80) { $r.Count } else { $null }
} 15000
Assert 'P4' 'setup' ("library filtered to '{0}'" -f $Artist) ($null -ne $rowsFiltered) 'few rows' ("$rowsFiltered rows")

Expand-Library
$rowsNow = @(Get-TrackRows)
$leafNames = @()
foreach ($r in $rowsNow) { $i = Get-RowInfo $r; if (-not $i.HasExpander) { $leafNames += $r.Name } }
Log ("  {0} row(s), {1} leaf row(s): {2}" -f $rowsNow.Count, $leafNames.Count, ($leafNames -join ' | '))

$rowA = Find-TrackRow $TrackAName
Assert 'P5' 'step 1' ("track A row found ('{0}')" -f $TrackAName) ($null -ne $rowA) 'a track row (no expander)' $(if ($rowA) { $rowA.Name } else { "none of: $($leafNames -join ', ')" })

$rowB = Find-TrackRow $TrackBName
Assert 'P6' 'step 3' ("track B row found ('{0}')" -f $TrackBName) ($null -ne $rowB) 'a track row (no expander)' $(if ($rowB) { $rowB.Name } else { 'missing' })

if ($rowA) {
    [void](Select-El $rowA)
    Start-Sleep -Milliseconds 350
    $loadBtn = Get-LibraryButton 'Load*'
    Assert 'P7' 'step 1' 'Load button present' ($null -ne $loadBtn) 'Load ▶' $(if ($loadBtn) { $loadBtn.Name } else { 'missing' })
    if ($loadBtn) { [void](Invoke-El $loadBtn) }
}

$loadedA = Wait-Until {
    $d = Get-Deck 0
    if ($null -ne $d.position -and $d.position.dur -gt 30) { $d } else { $null }
} 20000
Assert 'A1' 'step 1' 'deck A loaded a library track' ($null -ne $loadedA) 'duration > 0:30' $(if ($loadedA) { "$($loadedA.position.text) title='$($loadedA.title)'" } else { 'no readout' })

# the shared transport strip drives the ACTIVE deck
if ($null -eq (Find-Node -Type 'Button' -Name 'ACTIVE')) {
    $setActive = @(Find-Nodes -Type 'Button' -Name 'SET ACTIVE')
    if ($setActive.Count -gt 0) {
        [void](Invoke-El $setActive[0])
        Start-Sleep -Milliseconds 500
        Log '  activated deck A (SET ACTIVE -> ACTIVE)'
    }
}
Assert 'A2' 'step 1' 'deck A is the active deck' ($null -ne (Find-Node -Type 'Button' -Name 'ACTIVE' -Force)) 'ACTIVE badge on deck A' $(if ((Find-Node -Type 'Button' -Name 'ACTIVE' -Force)) { 'yes' } else { 'no' })

$tp = Get-TransportButtons
Assert 'A3' 'step 1' 'transport strip found (play/stop/fast-forward)' ($null -ne $tp -and $null -ne $tp.play) 'PlayButton plus its neighbours' $(if ($tp) { "play='$($tp.play.Name)' stop='$($tp.stop.Name)' ff='$($tp.fastFwd.Name)'" } else { 'missing' })

$script:Reads['playAttempts'] = @()
function Ensure-Playing([int]$DeckIndex, [string]$Label) {
    $p1 = Get-Span $DeckIndex
    Start-Sleep -Milliseconds 1400
    $p2 = Get-Span $DeckIndex
    if ($p1 -and $p2 -and $p2.pos -gt $p1.pos) {
        Log ("  deck {0} already advancing: {1} -> {2}" -f $Label, $p1.text, $p2.text)
        return $true
    }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        [void](Invoke-El (Get-TransportButtons -Force).play)
        Start-Sleep -Milliseconds 1500
        $p1 = Get-Span $DeckIndex
        Start-Sleep -Milliseconds 1500
        $p2 = Get-Span $DeckIndex
        $adv = ($p1 -and $p2 -and $p2.pos -gt $p1.pos)
        $script:Reads['playAttempts'] += [pscustomobject]@{
            deck = $Label; attempt = $attempt
            from = $(if ($p1) { $p1.text } else { $null }); to = $(if ($p2) { $p2.text } else { $null }); advanced = $adv
        }
        if ($adv) { Log ("  deck {0} advancing after play press #{1}: {2} -> {3}" -f $Label, $attempt, $p1.text, $p2.text); return $true }
        Log ("  deck {0} not advancing after press #{1} ({2}) - toggling again" -f $Label, $attempt, $(if ($p2) { $p2.text } else { 'n/a' }))
    }
    return $false
}

$playing = Ensure-Playing 0 'A'
Start-Sleep -Seconds 4
$pA = Get-Span 0
$advanced = ($null -ne $pA -and $pA.pos -gt 3)
Assert 'A4' 'step 1' 'deck A position advanced past 0:03' ($playing -and $advanced) 'position > 0:03 and increasing' $(if ($pA) { $pA.text } else { 'no readout' })

$dA = Get-Deck 0
$script:Reads['deckA'] = [pscustomobject]@{
    title = $dA.title; bpm = $dA.bpm; ratio = $dA.ratio
    position = $(if ($dA.position) { $dA.position.text } else { $null })
}
Log ("  deck A: title='{0}' bpm='{1}' ratio='{2}' position='{3}'" -f $dA.title, $dA.bpm, $dA.ratio, $(if ($dA.position) { $dA.position.text } else { $null }))
Assert 'A5' 'step 1' 'deck A reports title, BPM and tempo ratio' ($dA.title -and $dA.bpm -and $dA.ratio) 'title/BPM/ratio' ("title='$($dA.title)' bpm='$($dA.bpm)' ratio='$($dA.ratio)'")
Assert 'A6' 'step 1' 'BPM readout is a real analysed value' ($dA.bpm -match '^[0-9]+\.?[0-9]* BPM$') 'nnn.n BPM' $dA.bpm

$null = Shot 'e2e-1-loaded.png'

# ================================================================ step 2: tempo

Log '--- step 2: deck A tempo fader to +6 %'
if (-not (Test-AppAlive)) { exit (Write-Result 'app was redeployed mid-run; rerun') }

$tempoValBefore = Get-RangeVal (Get-DeckTempoSlider 0)
Assert 'T1' 'step 2' 'deck A tempo fader located' ($null -ne (Get-DeckTempoSlider 0)) 'a -16..+16 RangeValue slider' $(if ($null -ne $tempoValBefore) { "value $tempoValBefore" } else { 'missing' })

$ratioBefore = $dA.ratio
$bpmBefore = $dA.bpm
[void](Set-Tempo 0 $TempoPercent)
Start-Sleep -Milliseconds 1200

$dA2 = Get-Deck 0
$tempoValAfter = Get-RangeVal (Get-DeckTempoSlider 0)
$expectedRatio = 1.0 + ($TempoPercent / 100.0)
$ratioNum = $null
$rm = [regex]::Match([string]$dA2.ratio, '^([0-9]\.[0-9]{3})$')
if ($rm.Success) { $ratioNum = [double]$rm.Groups[1].Value }
$ratioOk = ($null -ne $ratioNum) -and ([Math]::Abs($ratioNum - $expectedRatio) -le 0.002)
Assert 'T2' 'step 2' 'tempo ratio text shows +6 % (1.060)' $ratioOk ("{0:0.000}" -f $expectedRatio) ("before='$ratioBefore' after='$($dA2.ratio)' fader=$tempoValAfter")

$m1 = [regex]::Match([string]$bpmBefore, '([0-9]+\.[0-9]+) BPM')
$m2 = [regex]::Match([string]$dA2.bpm, '([0-9]+\.[0-9]+) BPM')
if ($m1.Success -and $m2.Success) {
    $bpmBeforeNum = [double]$m1.Groups[1].Value
    $bpmAfterNum = [double]$m2.Groups[1].Value
    $expBpm = $bpmBeforeNum * $expectedRatio
    $tol = [Math]::Max(0.6, $bpmBeforeNum * 0.02)
    Assert 'T3' 'step 2' 'EffectiveBpm follows the tempo ratio' ([Math]::Abs($bpmAfterNum - $expBpm) -le $tol) ("{0:0.0} BPM" -f $expBpm) ("before='$bpmBefore' after='$($dA2.bpm)'")
} else {
    Assert 'T3' 'step 2' 'EffectiveBpm follows the tempo ratio' $null 'numeric BPM x 1.06' "before='$bpmBefore' after='$($dA2.bpm)'"
}

$deckBRatio = (Get-Deck 1).ratio
Assert 'T4' 'step 2' 'deck B tempo untouched (the fader moved deck A only)' ($deckBRatio -eq '1.000' -or $null -eq $deckBRatio) '1.000' "$deckBRatio"

$t1 = Get-Span 0
Start-Sleep -Seconds 4
$t2 = Get-Span 0
$tempoAdvancing = ($t1 -and $t2 -and ($t2.pos - $t1.pos) -ge 3)
Assert 'T5' 'step 2' 'track keeps advancing at +6 %' $tempoAdvancing 'position advances >= 3 s in 4 s' $(if ($t1 -and $t2) { "$($t1.text) -> $($t2.text)" } else { 'no readout' })

$script:Reads['tempo'] = [pscustomobject]@{
    faderBefore = $tempoValBefore; faderAfter = $tempoValAfter
    ratioBefore = $ratioBefore; ratioAfter = $dA2.ratio
    bpmBefore = $bpmBefore; bpmAfter = $dA2.bpm
    positionBefore = $(if ($t1) { $t1.text } else { $null })
    positionAfter = $(if ($t2) { $t2.text } else { $null })
}
$null = Shot 'e2e-2-tempo.png'

# ======================================================= step 3: queue hand-off

Log '--- step 3: queue the hand-off pair and prove the transition'

# The transition plan is scheduled by ENGINE FRAME, not by the playhead: Begin()
# computes startFrame = now + (remaining - fade) once, at arm time. Seeking the
# deck afterwards cannot move it, so the armed fade is always observed by letting
# the outgoing track reach its natural end. Deck A therefore uses the shortest
# library track that has a beat grid (102 s), which keeps the run to ~2 minutes.

$tpNow = Get-TransportButtons
if ($null -ne $tpNow -and $null -ne $tpNow.stop) {
    [void](Invoke-El $tpNow.stop)
    Start-Sleep -Milliseconds 900
    Log '  stopped the active deck'
}

$clearBtn = Get-QueueButton 'Clear'
if ($null -ne $clearBtn) { [void](Invoke-El $clearBtn); Start-Sleep -Milliseconds 700; Log '  cleared the queue' }

$queueTabBefore = Get-QueueTab
$queueHeaderBefore = if ($queueTabBefore) { $queueTabBefore.Name } else { $null }

# Queue A then B: with nothing playing the rotation hands row 1 deck A, row 2 deck B.
$queuedA = $false; $queuedB = $false
$queueBtn = Get-LibraryButton '+ Queue'
if ($null -eq $queueBtn) { Log '  ! "+ Queue" button not found in the LIBRARY toolbar' }
if ($rowA -and $queueBtn) {
    [void](Select-El $rowA); Start-Sleep -Milliseconds 250
    $queuedA = Invoke-El $queueBtn
    Start-Sleep -Milliseconds 400
}
if ($rowB -and $queueBtn) {
    [void](Select-El $rowB); Start-Sleep -Milliseconds 250
    $queuedB = Invoke-El $queueBtn
    Start-Sleep -Milliseconds 700
}

$queueTab = Get-QueueTab
$queueHeaderAfter = if ($queueTab) { $queueTab.Name } else { $null }
Assert 'Q1' 'step 3' '+ Queue added rows (QUEUED tab badge)' ($queuedA -and $queuedB -and $null -ne $queueTab) 'two rows queued' ("header '$queueHeaderBefore' -> '$queueHeaderAfter'")

if ($queueTab) { [void](Select-El $queueTab); Start-Sleep -Milliseconds 900 }
$qRows = @(Wait-Until { $r = @(Get-QueueRows); if ($r.Count -gt 0) { $r } else { $null } } 8000)

function Get-RowText($RowNode) {
    $kids = @(Get-Nodes -Root ($RowNode.El) -Max 40 -Deep -Type 'Text')
    return (@($kids | ForEach-Object { $_.Name }) -join ' / ')
}
$rowTexts = @($qRows | ForEach-Object { Get-RowText $_ })
Log ("  queue rows: {0}" -f ($rowTexts -join ' || '))
Assert 'Q2' 'step 3' 'queue holds the two library tracks' ($qRows.Count -eq 2 -and ($rowTexts -join ' ') -like "*$TrackAName*" -and ($rowTexts -join ' ') -like "*$TrackBName*") '2 rows, A first then B' ("$($qRows.Count) row(s): $($rowTexts -join ' | ')")

$row1Play = $null
if ($qRows.Count -ge 1) {
    $row1Play = @(Get-Nodes -Root ($qRows[0].El) -Max 40 -Deep -Type 'Button') | Where-Object { $_.Name -eq '▶' -or $_.Name -eq 'Play' } | Select-Object -First 1
}
Assert 'Q3' 'step 3' 'queued row exposes its play button' ($null -ne $row1Play) '▶ inside the row' $(if ($row1Play) { 'found' } else { 'missing' })

$startedFromQueue = $false
if ($null -ne $row1Play) {
    $startedFromQueue = Invoke-El $row1Play
    Start-Sleep -Milliseconds 800
}

# Starting a row is StartQueueItemAsync -> LoadTrack (async deck load) ->
# StartDeckAfterLoad -> PlayDeck. Play() bails out while the deck is still
# (!IsLoaded), so the row can come up as: current row, plan armed, deck silent.
$qAdvanceStart = Get-Span 0
Start-Sleep -Seconds 3
$qAdvanceEnd = Get-Span 0
$rowPlayAdvanced = ($startedFromQueue -and $null -ne $qAdvanceEnd -and ($null -eq $qAdvanceStart -or $qAdvanceEnd.pos -gt $qAdvanceStart.pos))
$script:Reads['rowPlayStart'] = [pscustomobject]@{
    invoked = $startedFromQueue
    from = $(if ($qAdvanceStart) { $qAdvanceStart.text } else { $null })
    to = $(if ($qAdvanceEnd) { $qAdvanceEnd.text } else { $null })
}
Log ("  queue row play: {0} -> {1} (advancing={2})" -f $(if ($qAdvanceStart) { $qAdvanceStart.text } else { 'n/a' }), $(if ($qAdvanceEnd) { $qAdvanceEnd.text } else { 'n/a' }), $rowPlayAdvanced)
Assert 'Q4a' 'step 3' 'queue row play starts the deck by itself' $rowPlayAdvanced 'position advances within 3 s of the row play' $(if ($qAdvanceEnd) { "$($qAdvanceStart.text) -> $($qAdvanceEnd.text)" } else { 'no readout' })

if (-not $rowPlayAdvanced) {
    # The deck is loaded and current but silent; the transport play button starts
    # the deck that StartDeckAfterLoad activated (deck A), so the rest of the
    # hand-off chain can still be verified.
    $allTexts = @(Get-WinNodes -Force | Where-Object { $_.Type -eq 'Text' } | ForEach-Object { $_.Name })
    $script:Reads['textsAtRowPlay'] = $allTexts
    $dQ = Get-Deck 0
    Assert 'Q4b' 'step 3' 'row was loaded as the current row even though it did not start' ($dQ.title -like "*$TrackAName*") "deck A holds '$TrackAName'" "title='$($dQ.title)' position=$(if ($qAdvanceEnd) { $qAdvanceEnd.text } else { 'n/a' })"
    $script:Reads['playAfterRowStart'] = $true
    $recovered = $null
    $presses = 0
    for ($try = 1; $try -le 4 -and $null -eq $recovered; $try++) {
        $bt = Get-DeckPlayButton 0
        if ($null -eq $bt) { break }
        [void](Invoke-El $bt)
        $presses++
        $recovered = Wait-Until {
            $a = Get-Span 0
            if ($null -ne $a -and $a.pos -ge 1) { $a } else { $null }
        } 4000 500
    }
    $script:Reads['deckAPlayPresses'] = $presses
    Assert 'Q4c' 'step 3' 'deck A card play button starts the loaded row (workaround so the chain can be verified)' ($null -ne $recovered) 'deck A advancing' $(if ($recovered) { "advancing after $presses press(es): $($recovered.text)" } else { "still stopped after $presses press(es)" })
} else {
    $script:Reads['playAfterRowStart'] = $false
}

$afterQueueStart = Get-Span 0
$dAQ4 = Get-Deck 0
Assert 'Q4' 'step 3' 'the queued row is on deck A and playing' ($null -ne $afterQueueStart -and $null -ne $dAQ4.title -and $dAQ4.title -like "*$TrackAName*") "deck A playing '$TrackAName'" ("position=$(if ($afterQueueStart) { $afterQueueStart.text } else { 'none' }) title='$($dAQ4.title)'")

$cfBefore = Get-Crossfader
Assert 'Q5' 'step 4' 'crossfader slider found and readable' ($null -ne $cfBefore) 'CrossfaderSlider RangeValue' "$cfBefore"

# A stale plan from an earlier run would already have deck B running; then "B
# advancing after the fade" proves nothing. Deck B must be idle first.
$bIdle = Get-Span 1
$bIdleOk = ($null -eq $bIdle -or $bIdle.pos -le 5)
Assert 'H0' 'step 3' 'deck B is idle before the hand-off' $bIdleOk 'deck B at the top of its track' $(if ($bIdle) { $bIdle.text } else { 'no readout' })

# the hand-off plan is logged when it is armed
$armLine = Wait-Until {
    $hit = @(Read-AppLogSince $script:LogStart | Where-Object { $_ -match 'transition A->B' }) | Select-Object -Last 1
    if ($hit) { $hit } else { $null }
} ($ArmTimeoutSeconds * 1000) 700
Assert 'H1' 'step 3' 'log shows the armed "transition A->B" plan' ($null -ne $armLine) 'transition A->B ... fade=' $(if ($armLine) { $armLine } else { 'not logged' })

$fadeValue = 0.0
$fadeMatch = [regex]::Match([string]$armLine, 'fade=\s*([0-9.]+)s')
if ($fadeMatch.Success) { $fadeValue = [double]$fadeMatch.Groups[1].Value }
Assert 'H2' 'step 3' 'transition log carries a fade time' ($fadeMatch.Success -and $fadeValue -gt 0) 'fade=<seconds> > 0' $(if ($fadeMatch.Success) { "fade=$fadeValue" } else { "no fade in: $armLine" })

$transitionStatus = @(Get-WinNodes -Force | Where-Object { $_.Type -eq 'Text' -and $_.Name -match 'Next:' }) | ForEach-Object { $_.Name }
$script:Reads['transitionStatus'] = $transitionStatus
Log ("  transition status: {0}" -f ($transitionStatus -join ' | '))

$null = Shot 'e2e-3-armed.png'

# --- let the outgoing track run to its end (the plan fires on the engine clock) ---
$dA = Get-Deck 0
$durA = if ($null -ne $dA.position) { $dA.position.dur } else { 0 }
$runOut = [Math]::Min($FadeTimeoutSeconds, [Math]::Max(20, $durA))
Log ("  waiting up to {0} s for deck A ({1}) to play past 80 % of {2} s" -f $runOut, $(if ($dA.position) { $dA.position.text } else { 'n/a' }), $durA)
$endOk = Wait-Until {
    $a = Get-Span 0
    if ($null -ne $a -and $a.dur -gt 0 -and $a.pos -ge [Math]::Floor($a.dur * 0.8)) { $a } else { $null }
} ($runOut * 1000) 1000
Assert 'H3' 'step 3' 'deck A plays into the armed fade window' ($null -ne $endOk) 'position past 80 % of the duration' $(if ($endOk) { $endOk.text } else { 'never got there' })

# Wait for the render thread to run the fade and promote deck B.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$cfAfter = $null; $bPrev = $null; $bNow = $null; $promoted = $false
$cfSamples = New-Object System.Collections.ArrayList
$deadline = (Get-Date).AddSeconds([Math]::Max(30, $fadeValue + 20))
while ((Get-Date) -lt $deadline) {
    $bNow = Get-Span 1
    $cfNow = Get-Crossfader
    if ($null -ne $cfNow) { $cfAfter = [double]$cfNow; [void]$cfSamples.Add([Math]::Round($cfAfter, 3)) }
    if ($null -ne $bNow -and $null -ne $bPrev -and $bNow.pos -gt $bPrev.pos -and $null -ne $cfAfter -and $cfAfter -ge 0.8) {
        $promoted = $true; break
    }
    $bPrev = $bNow
    Start-Sleep -Milliseconds 500
}
$handoffSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
$cfAtPromotion = $cfAfter
# the fade is still landing when deck B first moves: let it settle
if ($promoted) {
    $settle = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $settle) {
        $c = Get-Crossfader
        if ($null -ne $c) { $cfAfter = [double]$c; [void]$cfSamples.Add([Math]::Round($cfAfter, 3)) }
        if ($null -ne $cfAfter -and $cfAfter -ge 0.98) { break }
        Start-Sleep -Milliseconds 400
    }
}
$script:Reads['crossfaderSamples'] = @($cfSamples)
$script:Reads['crossfaderAtPromotion'] = $cfAtPromotion
Log ("  hand-off after {0} s: deck B={1} crossfader={2}" -f $handoffSeconds, $(if ($bNow) { $bNow.text } else { 'n/a' }), $cfAfter)

$b1 = Get-Span 1
Start-Sleep -Seconds 4
$b2 = Get-Span 1
$bAdvancing = ($null -ne $b1 -and $null -ne $b2 -and ($b2.pos - $b1.pos) -ge 2)

$qrsNow = @(Get-QueueRows)
$rowTextsNow = @($qrsNow | ForEach-Object { Get-RowText $_ })
$script:Reads['queueRowsAfter'] = $rowTextsNow
Log ("  queue rows after hand-off: {0}" -f ($rowTextsNow -join ' || '))
$rowBCurrent = @($rowTextsNow | Where-Object { $_ -like "*$TrackBName*" -and $_ -like '*playing*' }).Count -gt 0
$rowAPlayed = @($rowTextsNow | Where-Object { $_ -like "*$TrackAName*" -and $_ -match '/\s*played\s*/' }).Count -gt 0

Assert 'H4' 'step 3' 'the fade completed and promoted the armed deck' $promoted 'deck B advancing with crossfader >= 0.8' ("promoted=$promoted after $handoffSeconds s, crossfader at promotion=$cfAtPromotion, settled=$cfAfter")
Assert 'H5' 'step 3' 'deck B readout advances after the hand-off' $bAdvancing 'position increases >= 2 s in 4 s' $(if ($b1 -and $b2) { "$($b1.text) -> $($b2.text)" } else { 'no readout' })
Assert 'H6' 'step 3' 'the queued row became the current row' $rowBCurrent 'row state reads "▶ playing"' ($rowTextsNow -join ' | ')
Assert 'H7' 'step 3' 'the outgoing row is marked played' $rowAPlayed 'row state reads "played"' ($rowTextsNow -join ' | ')

$cfMoved = ($null -ne $cfBefore -and $null -ne $cfAfter -and [Math]::Abs($cfAfter - $cfBefore) -ge 0.4 -and $cfAfter -ge 0.8)
Assert 'H8' 'step 4' 'crossfader moved programmatically towards deck B' $cfMoved 'RangeValue moved >= 0.4 and ended >= 0.8' ("before=$cfBefore after=$cfAfter samples=$(($cfSamples | Select-Object -First 10) -join ',')")

# The loopback API is a second opinion on "is audio running"; /state carries the
# soundboard pads plus AnyDeckPlaying/TransitionText - it has no deck position,
# title or BPM, so the deck readouts remain the primary evidence.
$apiState = $null
try {
    $settingsPath = Join-Path $env:APPDATA 'JockeyBox\settings.json'
    if (Test-Path $settingsPath) {
        $cfg = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $tok = $cfg.ApiToken
        if (-not $tok) { $tok = $cfg.apiToken }
        $hdr = @{ 'X-JockeyBox-Token' = [string]$tok }
        $apiState = Invoke-RestMethod -Uri 'http://127.0.0.1:17890/state' -Headers $hdr -TimeoutSec 5
    }
} catch { Log ("  ! api /state unavailable: {0}" -f $_.Exception.Message) }
$script:Reads['apiState'] = $apiState
if ($null -ne $apiState) {
    Log ("  api /state: AnyDeckPlaying={0} Transition='{1}' (no deck position/title/BPM in this payload)" -f $apiState.AnyDeckPlaying, $apiState.Transition)
    Assert 'H9' 'step 4' 'loopback API agrees a deck is playing' ($null -eq $apiState.AnyDeckPlaying -or $apiState.AnyDeckPlaying -eq $true) 'AnyDeckPlaying = true' ("AnyDeckPlaying=$($apiState.AnyDeckPlaying) Transition='$($apiState.Transition)'")
}

$cfLabels = @(Find-Nodes -Type 'Button' -NameLike 'Crossfader*') | ForEach-Object { $_.Name }
$script:Reads['crossfader'] = [pscustomobject]@{
    before = $cfBefore; after = $cfAfter; labels = $cfLabels; secondsToHandoff = $handoffSeconds; samples = @($cfSamples)
}
$dBFinal = Get-Deck 1
$script:Reads['deckB'] = [pscustomobject]@{
    position = $(if ($b2) { $b2.text } else { $null })
    title = $dBFinal.title; bpm = $dBFinal.bpm; ratio = $dBFinal.ratio
}

$null = Shot 'e2e-4-handoff.png'

# ------------------------------------------------------------------- log evidence

$allLines = @(Read-AppLogSince $script:LogStart)
# Keep the evidence small and quoted: whole scan lines can be kilobytes wide.
function Clip-Line([string]$Line, [int]$Max = 300) {
    if ($null -eq $Line) { return $null }
    return $(if ($Line.Length -le $Max) { $Line } else { $Line.Substring(0, $Max) + ' …' })
}
$script:Reads['log'] = [pscustomobject]@{
    lines        = $allLines.Count
    transitions  = @($allLines | Where-Object { $_ -match 'transition ' } | Select-Object -Last 20 | ForEach-Object { Clip-Line $_ })
    audio        = @($allLines | Where-Object { $_ -match 'tempo:|audio engine|output .|audio route' } | Select-Object -Last 20 | ForEach-Object { Clip-Line $_ })
}
foreach ($l in $script:Reads['log'].transitions) { Log ("  LOG transition: {0}" -f $l) }

# The exact strings the requirement asks for; the app has no 'deck A' / 'armed'
# log vocabulary - the arming line is the "transition A->B" line itself.
$logDigest = [pscustomobject]@{
    transitionLines = @($allLines | Where-Object { $_ -match 'transition [AB]->[AB]' } | ForEach-Object { Clip-Line $_ 320 })
    refusedLines    = @($allLines | Where-Object { $_ -match 'transition refused' } | ForEach-Object { Clip-Line $_ })
    tempoLines      = @($allLines | Where-Object { $_ -match 'tempo: (engage|bypass)' } | ForEach-Object { Clip-Line $_ })
}
$script:Reads['logDigest'] = $logDigest

$procEnd = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
$sameProcess = ($null -ne $procInfo -and $null -ne $procEnd -and $procEnd.Id -eq $procInfo.pid)
Assert 'P8' 'preflight' 'the same app instance ran the whole run' ($sameProcess -and -not $script:Restarted) ("pid $($procInfo.pid)") $(if ($procEnd) { "pid $($procEnd.Id), restarted=$($script:Restarted)" } else { 'not running' })
$script:Reads['processEnd'] = if ($procEnd) { [pscustomobject]@{ pid = $procEnd.Id; start = $procEnd.StartTime.ToString('s') } } else { $null }

# ================================================== step 5: remote track lists

# Requirements 2 and 3, driven through the same UIA-only path as steps 1-4:
#   R1  the remote track list holds >= 20 rows (paged - it is virtualised)
#   R2  a row's context menu carries "Add to playlist", enabled
#   R3  the submenu lists the user's internal playlists; choosing one writes the
#       row (status line + the playlist's own row count grows by one)
#   R4  remote rows show the karaoke state of their local counterpart
#   R5  the second remote tab holds >= 20 rows and exposes "Add to playlist"
#   R6  the row menu keeps the other local actions (>= 3)
#   R7  the write survives a restart (persisted membership, not an in-memory list)
#
# The Spotify tab is only usable when the account is connected. On this host it
# is not, so R1..R6 run on -RemotePrimaryTab (YouTube by default) and the
# Spotify measurements are recorded as S1..S5 informational assertions. Set
# -RemotePrimaryTab SPOTIFY once the account is connected and R1..R6 move there.

Log '--- step 5: remote rows - Add to playlist + karaoke state'

$script:RemoteTables     = [ordered]@{}
$script:RemoteMenuNames  = @()
$script:HarnessPlaylist  = 'E2E Harness'
$script:AddedRowTitle    = ''
$script:AddedTargetTitle = ''

# --- small helpers ----------------------------------------------------------

function Get-TabNode([string]$Name) {
    foreach ($t in (Get-TabItems)) { if ($t.Name -eq $Name) { return $t } }
    return $null
}

function Open-Tab([string]$Name) {
    $tab = Get-TabNode $Name
    if ($null -eq $tab) { return $null }
    [void](Select-El $tab 3)
    Start-Sleep -Milliseconds 2200
    return (Get-TabNode $Name)
}

function Get-TabNodes($Tab, [int]$Max = 1200) {
    if ($null -eq $Tab) { return @() }
    return @(Get-Nodes -Root ($Tab.El) -Max $Max -Deep)
}

function Get-RowTexts($Row) {
    $kids = @(Get-Nodes -Root ($Row.El) -Max 40 -Deep -Type 'Text')
    return @($kids | Where-Object { $_.Name -ne '' } | ForEach-Object { $_.Name })
}

# The karaoke indication is a glyph TextBlock inside the row: the app sets an
# AutomationName on it ("Karaoke available" / "Karaoke needed"), and when the
# provider does not surface that name the visible text is matched instead.
function Get-KaraokeState($Row) {
    foreach ($t in (Get-RowTexts $Row)) {
        if ($t -match '^(Karaoke available|Karaoke needed|No karaoke version)$') { return [string]$t }
    }
    foreach ($t in (Get-RowTexts $Row)) {
        if ($t -match '(?i)^karaoke') { return [string]$t }
    }
    return ''
}

# The list that owns the rows, found by walking up from a row - never by index,
# because the tab also holds the playlist rail (another List).
function Get-RowList($Row) {
    if ($null -eq $Row) { return $null }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $p = $null
    try { $p = $walker.GetParent($Row.El) } catch { return $null }
    for ($i = 0; $i -lt 8 -and $null -ne $p; $i++) {
        try { if ($p.Current.ControlType -eq $CT::List) { return [pscustomobject]@{ El = $p; Type = 'List'; Name = ''; Id = '' } } } catch { return $null }
        try { $p = $walker.GetParent($p) } catch { return $null }
    }
    return $null
}

function Get-RemoteRows($Tab) {
    return @(Get-TabNodes $Tab 1200 | Where-Object { $_.Type -eq 'ListItem' })
}

# Rows are virtualised: only the visible page is realised, so "how many rows does
# this list show" has to be answered by paging through it. Returns the distinct
# row names seen from the top, then scrolls back to the top.
function Walk-ListRows($Tab, [int]$MaxPages = 16) {
    $seen = New-Object System.Collections.Generic.List[string]
    $rows = @(Get-RemoteRows $Tab)
    $list = Get-RowList ($rows | Select-Object -First 1)
    if ($null -eq $list) {
        foreach ($r in $rows) { if ($r.Name -ne '' -and -not $seen.Contains($r.Name)) { $seen.Add($r.Name) } }
        return @($seen)
    }
    $sp = $null
    try { $sp = $list.El.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { $sp = $null }
    if ($null -ne $sp) { try { if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, 0) } } catch { } ; Start-Sleep -Milliseconds 400 }

    for ($page = 0; $page -lt $MaxPages; $page++) {
        $added = 0
        foreach ($r in (Get-RemoteRows $Tab)) {
            if ($r.Name -eq '') { continue }
            if (-not $seen.Contains($r.Name)) { $seen.Add($r.Name); $added++ }
        }
        if ($null -eq $sp) { break }
        $moved = $false
        try {
            $sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement)
            $moved = $true
        } catch { $moved = $false }
        Start-Sleep -Milliseconds 450
        if (-not $moved) { break }
        if ($added -eq 0) { break }
    }
    if ($null -ne $sp) { try { $sp.SetScrollPercent(-1, 0) } catch { } }
    Start-Sleep -Milliseconds 400
    return @($seen)
}

# The context menu is a WPF popup: it shows up either as its own top-level window
# or (as here) inside the app window's element list, so both are searched. The
# automation name is not used - only MenuItem control type and text.
function Get-MenuItems {
    $items = New-Object System.Collections.ArrayList
    foreach ($n in @(Get-Nodes -Root ($script:Win) -Max 500 -Deep -Type 'MenuItem')) { [void]$items.Add($n) }
    if ($items.Count -gt 0) { return @($items) }
    $tops = $AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($t in $tops) {
        try {
            if ($t.Current.Name -ne 'JockeyBox') { continue }
            foreach ($n in @(Get-Nodes -Root $t -Max 200 -Deep -Type 'MenuItem')) { [void]$items.Add($n) }
        } catch { }
    }
    return @($items)
}

function Close-Menus {
    if (@(Get-MenuItems).Count -eq 0) { return }
    try {
        $script:Win.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    } catch { }
    Start-Sleep -Milliseconds 600
}

function Select-Row($Row) {
    if ($null -eq $Row) { return $false }
    try {
        $Row.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 400
        return $true
    } catch { return $false }
}

# Opens the row's context menu the way a keyboard user does: select the row, give
# it focus, then Shift+F10. (A synthetic right-click needs SetCursorPos/Add-Type,
# which this host's AV blocks in Temp, and it needs the window to be foreground.)
function Open-RowMenu($Row) {
    if ($null -eq $Row) { return $false }
    $null = Select-Row $Row
    try { $Row.El.SetFocus() } catch { Log ("  ! row focus: {0}" -f $_.Exception.Message) }
    Start-Sleep -Milliseconds 400
    if (@(Get-MenuItems).Count -gt 0) { Close-Menus }
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    Start-Sleep -Milliseconds 1400
    return (@(Get-MenuItems).Count -gt 0)
}

function Get-MenuItem([string]$NameLike) {
    return @(Get-MenuItems | Where-Object { $_.Name -like $NameLike }) | Select-Object -First 1
}

function Expand-MenuItem($Item) {
    if ($null -eq $Item) { return @() }
    try { $Item.El.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { }
    Start-Sleep -Milliseconds 1000
    return @(Get-Nodes -Root ($Item.El) -Max 60 -Deep -Type 'MenuItem')
}

# Rail rows are buttons whose content is a Grid holding the title and the count,
# so a row is identified by the text inside it, never by its own (empty) name.
function Get-RailRows($Tab) {
    $rows = New-Object System.Collections.ArrayList
    foreach ($b in @(Get-TabNodes $Tab 400 | Where-Object { $_.Type -eq 'Button' })) {
        $texts = @(Get-Nodes -Root ($b.El) -Max 20 -Deep -Type 'Text' | Where-Object { $_.Name -ne '' } | ForEach-Object { $_.Name })
        if ($texts.Count -eq 0) { continue }
        $title = $texts[0]
        $count = $null
        foreach ($t in $texts) { if ($t -match '^\d+$') { $count = [int]$t; break } }
        [void]$rows.Add([pscustomobject]@{ Title = $title; Count = $count; Texts = $texts; El = $b.El; Node = $b })
    }
    return @($rows)
}

function Get-RailRow($Tab, [string]$Title) {
    return @(Get-RailRows $Tab | Where-Object { $_.Title -eq $Title }) | Select-Object -First 1
}

function Get-PlaylistCount([string]$Title) {
    $tab = Get-TabNode 'LIBRARY'
    if ($null -eq $tab) { return $null }
    $row = Get-RailRow $tab $Title
    if ($null -eq $row) { return $null }
    return $row.Count
}

# --- one tab's R1..R6 -------------------------------------------------------

function Invoke-RemoteChecks([string]$TabName, [string]$Prefix, [string]$PlaylistTitle, $CountBefore) {
    Log ("--- step 5: {0} tab ({1})" -f $TabName, $Prefix)
    $tab = Open-Tab $TabName
    if ($null -eq $tab) {
        Assert ($Prefix + '1') 'step 5' "the $TabName tab opens" $false 'tab item' 'tab item not found'
        return $false
    }
    Start-Sleep -Seconds 2

    $names = @(Walk-ListRows $tab 16)
    $script:RemoteTables[$TabName] = @{
        rows = $names.Count; firstRows = @($names | Select-Object -First 25); karaokeRows = @(); rowsRealised = 0
    }
    Assert ($Prefix + '1') 'step 5' "$TabName track list holds at least 20 rows" ($names.Count -ge 20) '>= 20 rows' ("$($names.Count) distinct row(s): " + (@($names | Select-Object -First 3) -join ' | '))
    if ($names.Count -eq 0) { return $false }

    # prefer a row that carries a karaoke state; both properties are on the row
    $rows = @(Get-RemoteRows $tab)
    $target = $null
    foreach ($r in $rows) { if ((Get-KaraokeState $r) -ne '') { $target = $r; break } }
    if ($null -eq $target) { $target = $rows | Select-Object -First 1 }
    $script:AddedTargetTitle = $target.Name
    Log ("  target row: '{0}'" -f $target.Name)
    [void](Select-Row $target)
    $null = Shot ("e2e-5-{0}.png" -f $TabName.ToLower())

    # R4: every realised row is inspected for the karaoke indication. The column
    # is a glyph TextBlock whose AutomationName carries the state, so a row that
    # has a karaoke mirror and one that does not are both readable.
    $rowsNow = @(Get-RemoteRows $tab)
    $states = New-Object System.Collections.ArrayList
    $unlabelled = New-Object System.Collections.ArrayList
    foreach ($r in $rowsNow) {
        $k = Get-KaraokeState $r
        if ($k -ne '') { [void]$states.Add([pscustomobject]@{ row = $r.Name; karaoke = $k }) }
        else { [void]$unlabelled.Add($r.Name) }
    }
    $script:RemoteTables[$TabName]['karaokeRows'] = @($states)
    $script:RemoteTables[$TabName]['rowsRealised'] = $rowsNow.Count
    $script:RemoteTables[$TabName]['karaokeUnlabelled'] = @($unlabelled)
    $avail = @($states | Where-Object { $_.karaoke -eq 'Karaoke available' }).Count
    $needed = @($states | Where-Object { $_.karaoke -eq 'Karaoke needed' }).Count
    $none = @($states | Where-Object { $_.karaoke -eq 'No karaoke version' }).Count
    $knownStates = @('Karaoke available', 'Karaoke needed', 'No karaoke version')
    $badState = @($states | Where-Object { $knownStates -notcontains $_.karaoke })
    # The karaoke cell is the local library's column rendered into the remote row,
    # and it is collapsed when the row has no library file (SourceTrack.LocalEntry
    # is null), so the assertion is about the rows that do resolve locally.
    $r4ok = (@($states).Count -ge 1 -and @($badState).Count -eq 0)
    Assert ($Prefix + '4') 'step 5' "$TabName rows expose the karaoke state of their local counterpart" $r4ok 'rows that resolve to a library file carry Karaoke available / Karaoke needed / No karaoke version' ("$(@($states).Count) of $($rowsNow.Count) realised row(s) labelled: available=$avail needed=$needed none=$none" + $(if (@($badState).Count -gt 0) { ' | unexpected: ' + (@($badState | ForEach-Object { "'" + $_.karaoke + "'" }) -join ', ') } else { '' }) + '; rows without a local file have no karaoke cell (collapsed by design)')

    # R2: the menu carries "Add to playlist" and it is enabled.
    $opened = Open-RowMenu $target
    $addItem = Get-MenuItem 'Add to playlist*'
    $script:RemoteMenuNames = @(Get-MenuItems | ForEach-Object { $_.Name })
    $addEnabled = ($null -ne $addItem -and $addItem.El.Current.IsEnabled)
    Assert ($Prefix + '2') 'step 5' "$TabName row context menu offers 'Add to playlist'" ($opened -and $null -ne $addItem -and $addEnabled) 'a menu item starting with "Add to playlist", enabled' $(if ($addItem) { "found '$($addItem.Name)' enabled=$addEnabled; menu: " + (@($script:RemoteMenuNames) -join ', ') } else { "no menu; items seen: " + (@($script:RemoteMenuNames) -join ', ') })

    # R6: the same menu keeps the other local actions.
    $localActions = @('Play', 'Add to queue', 'Preview (stream)', 'Download', 'Download & play', 'Separate karaoke', 'Show in Explorer', 'Copy link')
    $present = @($script:RemoteMenuNames | Where-Object { $localActions -contains $_ })
    Assert ($Prefix + '6') 'step 5' "$TabName row menu keeps the other local actions" ($present.Count -ge 3) ('>= 3 of: ' + ($localActions -join ', ')) ("$($present.Count) present: " + ($present -join ', ') + ' || all: ' + (@($script:RemoteMenuNames) -join ', '))

    # R3: submenu lists the internal playlists, choosing one writes the row.
    $sub = @()
    if ($null -ne $addItem) { $sub = @(Expand-MenuItem $addItem) }
    $subNames = @($sub | ForEach-Object { $_.Name })
    $script:RemoteTables[$TabName]['submenu'] = $subNames
    $countBefore = $CountBefore
    $choice = @($sub | Where-Object { $_.Name -eq $PlaylistTitle }) | Select-Object -First 1
    $subOk = ($subNames.Count -ge 1 -and $null -ne $choice)
    Assert ($Prefix + '3i1') 'step 5' "$TabName submenu listed the internal playlists (evidence)" $null "a '$PlaylistTitle' row" ("submenu: " + ($subNames -join ', '))

    $invoked = $false
    if ($null -ne $choice) {
        $null = Shot ("e2e-5-{0}-menu.png" -f $TabName.ToLower())
        try { $choice.El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $invoked = $true } catch { Log ("  ! submenu invoke failed: {0}" -f $_.Exception.Message) }
        Start-Sleep -Seconds 2
    } else { Close-Menus }

    # The tab's status line is the app's own confirmation of the write.
    $statusText = ''
    foreach ($t in @(Get-TabNodes (Get-TabNode $TabName) 1200 | Where-Object { $_.Type -eq 'Text' })) {
        if ($t.Name -like 'Added*' -or $t.Name -like 'Created*' -or $t.Name -like 'nothing new*') { $statusText = $t.Name; break }
    }
    Assert ($Prefix + '3i2') 'step 5' "$TabName status line after the add (evidence)" $null "Added … to $PlaylistTitle." $(if ($statusText) { $statusText } else { 'no status line found' })

    # The playlist's own row count: the app counts rows that resolve to a library
    # file, and the target row above is one of those. The rail only materialises
    # while the LIBRARY tab is selected, so the count is read from there.
    $countAfter = $null
    $null = Open-Tab 'LIBRARY'
    Start-Sleep -Milliseconds 1200
    if ($null -ne $countBefore) {
        $countAfter = Wait-Until { $c = Get-PlaylistCount $PlaylistTitle; if ($null -ne $c -and $c -ne $countBefore) { $c } else { $null } } 8000 400
    }
    if ($null -eq $countAfter) { $countAfter = Get-PlaylistCount $PlaylistTitle }
    $countOk = ($null -ne $countBefore -and $null -ne $countAfter -and $countAfter -eq ($countBefore + 1))
    Assert ($Prefix + '3i3') 'step 5' "$TabName playlist count after the add (evidence)" $null "$countBefore -> $($countBefore + 1)" "$countBefore -> $(if ($null -eq $countAfter) { 'n/a' } else { $countAfter })"

    $statusOk = ($statusText -like "Added*$PlaylistTitle*")
    Assert ($Prefix + '3') 'step 5' "$TabName 'Add to playlist' writes the row to the chosen internal playlist" ($subOk -and $invoked -and $statusOk -and $countOk) "submenu offers $PlaylistTitle, status reads Added …, playlist count +1" ("submenu=$subOk invoked=$invoked status='$statusText' count=$countBefore->$countAfter")
    if ($countOk) { $script:AddedRowTitle = $target.Name }

    $null = Shot ("e2e-5-{0}-added.png" -f $TabName.ToLower())
    return ($subOk -and $invoked -and $statusOk -and $countOk)
}

# --- harness playlist: delete + recreate so the +1 check is repeatable -------

# The rail row is a Button; the "Delete?" confirmation is a sibling Border in the
# same StackPanel, so the Yes/No buttons are looked up on the row's parent.
function Remove-RailPlaylist($Row) {
    if ($null -eq $Row) { return $false }
    $del = @(Get-Nodes -Root ($Row.El) -Max 30 -Deep -Type 'Button' | Where-Object { $_.Name -eq '✕' }) | Select-Object -First 1
    if ($null -eq $del) { Log ("  ! no delete button on '{0}'" -f $Row.Title); return $false }
    [void](Invoke-El $del)
    Start-Sleep -Milliseconds 900
    $parent = $null
    try { $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($Row.El) } catch { }
    $yes = $null
    if ($null -ne $parent) {
        $yes = @(Get-Nodes -Root $parent -Max 40 -Deep -Type 'Button' | Where-Object { $_.Name -eq 'Yes' }) | Select-Object -First 1
    }
    if ($null -eq $yes) { Log ("  ! delete confirmation did not appear on '{0}'" -f $Row.Title); return $false }
    [void](Invoke-El $yes)
    Start-Sleep -Milliseconds 1400
    return $true
}

# Drops every rail row with this title (a failed confirmation in an earlier run
# can leave duplicates behind, and a duplicate would make the +1 check read the
# wrong row).
function Remove-HarnessPlaylists([string]$Title) {
    $tab = Get-TabNode 'LIBRARY'
    for ($pass = 1; $pass -le 5; $pass++) {
        $rows = @(Get-RailRows $tab | Where-Object { $_.Title -eq $Title })
        if ($rows.Count -eq 0) { return $true }
        Log ("  removing harness playlist row {0} (count {1}); {2} row(s) with that title" -f $pass, $rows[0].Count, $rows.Count)
        [void](Remove-RailPlaylist $rows[0])
        Start-Sleep -Milliseconds 800
    }
    $left = @(Get-RailRows (Get-TabNode 'LIBRARY') | Where-Object { $_.Title -eq $Title }).Count
    Log ("  ! {0} harness playlist row(s) still present" -f $left)
    return ($left -eq 0)
}

function Ensure-HarnessPlaylist([string]$Title) {
    $libTab = Open-Tab 'LIBRARY'
    if ($null -eq $libTab) { return $null }
    Start-Sleep -Milliseconds 1400
    [void](Remove-HarnessPlaylists $Title)
    # "+ New" opens an inline name box; the box is the Edit that was not there
    # before, so it is found by diffing the Edit set rather than by position.
    $before = @(Get-TabNodes $libTab 400 | Where-Object { $_.Type -eq 'Edit' } | ForEach-Object { try { $_.El.Current.BoundingRectangle.ToString() } catch { '' } })
    $newBtn = @(Get-TabNodes $libTab 400 | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq '+ New' }) | Select-Object -First 1
    if ($null -eq $newBtn) { Log '  ! + New button not found'; return $null }
    [void](Invoke-El $newBtn)
    Start-Sleep -Milliseconds 1000
    $edits = @(Get-TabNodes $libTab 400 | Where-Object { $_.Type -eq 'Edit' })
    $nameBox = $null
    foreach ($e in $edits) {
        $sig = try { $e.El.Current.BoundingRectangle.ToString() } catch { '' }
        if ($before -notcontains $sig) { $nameBox = $e; break }
    }
    if ($null -eq $nameBox) { $nameBox = $edits | Select-Object -Last 1 }
    if ($null -eq $nameBox) { Log '  ! the new-playlist name box did not appear'; return $null }
    try { $nameBox.El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Title) } catch { Log ("  ! naming the playlist failed: {0}" -f $_.Exception.Message) }
    Start-Sleep -Milliseconds 600
    $ok = @(Get-TabNodes $libTab 400 | Where-Object { $_.Type -eq 'Button' -and $_.Name -eq '✓' }) | Select-Object -First 1
    if ($null -ne $ok) { [void](Invoke-El $ok) } else { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
    Start-Sleep -Seconds 1
    $created = Wait-Until { Get-RailRow (Get-TabNode 'LIBRARY') $Title } 8000 500
    $n = @(Get-RailRows (Get-TabNode 'LIBRARY') | Where-Object { $_.Title -eq $Title }).Count
    if ($null -ne $created) { Log ("  harness playlist '{0}' created (rail count {1}, {2} row(s) with that title)" -f $Title, $created.Count, $n) }
    else { Log ("  ! harness playlist '{0}' did not appear in the rail" -f $Title) }
    return $created
}

# --- run it -----------------------------------------------------------------

$script:Reads['remote'] = $script:RemoteTables

# Stop the music before poking at the remote tabs; step 4 left deck B playing.
foreach ($b in @(Find-Nodes -Type 'Button' -Name '⏹')) { if ($null -ne $b) { [void](Invoke-El $b); break } }
Start-Sleep -Milliseconds 600

# Spotify first: it is the tab the requirement names, and its state is recorded
# either way. It shows no rows unless the account is connected on this host.
$spotTab = Open-Tab 'SPOTIFY'
$spotNodes = Get-TabNodes $spotTab 1200
$spotRows = @($spotNodes | Where-Object { $_.Type -eq 'ListItem' })
$spotNeedsAuth = @($spotNodes | Where-Object { $_.Type -eq 'Text' -and $_.Name -like '*Connect Spotify*' }).Count -gt 0
$script:RemoteTables['SPOTIFY'] = @{ rows = $spotRows.Count; needsAuth = $spotNeedsAuth; karaokeRows = @() }
$null = Shot 'e2e-5-spotify.png'
Log ("  Spotify tab: {0} realised row(s), needsAuth={1}" -f $spotRows.Count, $spotNeedsAuth)
Assert 'S1' 'step 5' 'Spotify tab shows a remote track list' ($(if ($spotRows.Count -ge 20) { $true } else { $null })) '>= 20 rows' $(if ($spotNeedsAuth) { "not connected - the tab says 'Connect Spotify to load your playlists.'; $($spotRows.Count) row(s)" } else { "$($spotRows.Count) row(s)" })

if ($RemotePrimaryTab -eq 'SPOTIFY' -and $spotRows.Count -lt 20) {
    Log '  ! -RemotePrimaryTab SPOTIFY was requested but the tab has no rows; falling back to YOUTUBE'
}
$primary = if ($spotRows.Count -ge 20 -and $RemotePrimaryTab -eq 'SPOTIFY') { 'SPOTIFY' } else { 'YOUTUBE' }
$mirror  = if ($primary -eq 'SPOTIFY') { 'YOUTUBE' } else { 'SPOTIFY' }
Log ("  primary remote tab: {0}" -f $primary)

$null = Ensure-HarnessPlaylist $script:HarnessPlaylist
$countBefore = Get-PlaylistCount $script:HarnessPlaylist
Log ("  harness playlist '{0}' row count before the add: {1}" -f $script:HarnessPlaylist, $countBefore)
$primaryOk = Invoke-RemoteChecks $primary 'R' $script:HarnessPlaylist $countBefore

# The second tab: the same two properties, reported as R5 (rows + menu) and S*
# evidence for the rest. It keeps its last-known playlist even when the channel
# is not connected, so on this host that is Spotify.
if ($mirror -eq 'SPOTIFY') {
    if ($spotRows.Count -ge 20) {
        $mirrorOk = Invoke-RemoteChecks 'SPOTIFY' 'S' $script:HarnessPlaylist $countBefore
        Assert 'R5' 'step 5' 'the second remote tab (SPOTIFY) holds >= 20 rows and offers Add to playlist' $mirrorOk '>= 20 rows with a working Add to playlist' 'see S1..S6'
    } else {
        # No account on this host, so the tab must say so rather than show an
        # empty, broken-looking list. The full row checks for this tab are the
        # same function, selected with -RemotePrimaryTab SPOTIFY once connected.
        $spotPrompt = @($spotNodes | Where-Object { $_.Type -eq 'Text' -and $_.Name -like '*Connect Spotify*' }).Count -gt 0
        Assert 'R5' 'step 5' 'the second remote tab (SPOTIFY) opens and, without an account, asks to connect instead of showing an empty list' ($null -ne $spotTab -and $spotPrompt) 'the tab exists and states it needs a connection' ("tab='SPOTIFY' prompt=$spotPrompt rows=$($spotRows.Count); with an account the same R1-R6 checks run there via -RemotePrimaryTab SPOTIFY")
    }
} else {
    $ytTab = Open-Tab 'YOUTUBE'
    $ytNames = @(Walk-ListRows $ytTab 16)
    $script:RemoteTables['YOUTUBE'] = @{ rows = $ytNames.Count; karaokeRows = @() }
    Assert 'R5' 'step 5' 'the second remote tab (YOUTUBE) holds >= 20 rows' ($ytNames.Count -ge 20) '>= 20 rows' "$($ytNames.Count) distinct row(s)"
}

# --- R7: the write survives a restart --------------------------------------
# A graceful WM_CLOSE flushes the playlist store, so the reloaded rail count and
# the re-opened playlist prove the membership came off disk.
$countBeforeRestart = Get-PlaylistCount $script:HarnessPlaylist
Log ("--- step 5: R7 restart probe (playlist '{0}' holds {1})" -f $script:HarnessPlaylist, $countBeforeRestart)

$script:PlaylistFile = Join-Path $env:APPDATA 'JockeyBox\playlists.json'
$closedOk = Close-AppWindow
Log ("  WM_CLOSE posted: {0}" -f $closedOk)
$gone = Wait-Until { $p = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1; if ($null -eq $p) { $true } else { $null } } 30000 500
if ($null -eq $gone) {
    Log '  graceful close timed out - force stopping'
    Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 2

# The store is flushed on a graceful close, so the file on disk is the persisted
# membership at this instant - read before the new instance can rewrite it.
$diskRows = $null
$diskTitle = ''
try {
    if (Test-Path $script:PlaylistFile) {
        $j = Get-Content $script:PlaylistFile -Raw | ConvertFrom-Json
        $p = @($j.Playlists | Where-Object { $_.Name -eq $script:HarnessPlaylist }) | Select-Object -First 1
        if ($null -ne $p) {
            $tracks = @($p.Tracks)
            $diskRows = $tracks.Count
            if ($tracks.Count -gt 0) { $diskTitle = [string]$tracks[0].Title }
            Log ("  on disk after close: '{0}' holds {1} row(s), first='{2}'" -f $p.Name, $diskRows, $diskTitle)
        } else { Log ("  on disk after close: no playlist named '{0}'" -f $script:HarnessPlaylist) }
    } else { Log ("  ! {0} not found" -f $script:PlaylistFile) }
} catch { Log ("  ! reading the playlist store failed: {0}" -f $_.Exception.Message) }

Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) | Out-Null
$script:SpanEl = @{}; $script:CfEl = $null; $script:Nodes = $null
$script:Win = Wait-Until { Find-Window } 90000 1000
Start-Sleep -Seconds 3
$script:Restarted = $true
Log ("  relaunched: {0}" -f $(if ($script:Win) { 'window found' } else { 'window NOT found' }))

$railAfter = $null
if ($null -ne $script:Win) {
    # The window exists before the UI is built, so wait for the deck cards first
    # (as the preflight does) - selecting a tab in a half-built shell is lost when
    # the shell is rebuilt.
    $ready = Wait-Until { $d = Get-Deck 0; if ($null -ne $d.position) { $true } else { $null } } 60000 1500
    Log ("  after relaunch the shell is ready: {0}" -f ($null -ne $ready))
    $null = Open-Tab 'LIBRARY'
    Start-Sleep -Milliseconds 1500
    $railAfter = Wait-Until { Get-RailRow (Get-TabNode 'LIBRARY') $script:HarnessPlaylist } 45000 1000
    if ($null -eq $railAfter) {
        # one more pass: the rail is rebuilt when the playlist store finishes loading
        $null = Open-Tab 'LIBRARY'
        Start-Sleep -Milliseconds 1500
        $railAfter = Wait-Until { Get-RailRow (Get-TabNode 'LIBRARY') $script:HarnessPlaylist } 20000 1000
    }
    if ($null -ne $railAfter) { Log ("  after restart the rail shows '{0}' with count {1}" -f $railAfter.Title, $railAfter.Count) }
    else {
        $titles = @(Get-RailRows (Get-TabNode 'LIBRARY') | ForEach-Object { "'" + $_.Title + "'(" + $_.Count + ")" })
        Log ("  after restart the rail has no '{0}' row; rail: {1}" -f $script:HarnessPlaylist, ($titles -join ', '))
    }
}
$countAfterRestart = if ($railAfter) { $railAfter.Count } else { $null }

# Re-open the playlist itself: its rows must contain something from the added
# remote track (the library title differs from the YouTube title, so a fragment
# of the library-side name is enough).
$rowSeen = ''
if ($null -ne $railAfter) {
    [void](Invoke-El $railAfter.Node)
    Start-Sleep -Seconds 2
    $treeRows = @(Get-TrackRows 400)
    # the playlist's tree lists its own titles; keep the whole list as evidence
    $rowSeen = (@($treeRows | Where-Object { $_.Name -ne '' } | ForEach-Object { $_.Name }) -join ' | ')
    Log ("  re-opened playlist rows: {0}" -f $rowSeen)
}
$null = Shot 'e2e-5-after-restart.png'

$storeOk = ($null -ne $diskRows -and $diskRows -ge 1)
$railOk = ($null -ne $railAfter -and $null -ne $countBeforeRestart -and $null -ne $countAfterRestart -and $countAfterRestart -eq $countBeforeRestart -and $countAfterRestart -ge 1)
$persistedOk = ($storeOk -and $railOk)
Assert 'R7' 'step 5' 'the remote row added to the internal playlist survives a restart' $persistedOk "store holds the row after close and the rail shows the same count after relaunch" ("store rows=$diskRows ('$diskTitle'); rail before restart=$countBeforeRestart after restart=$countAfterRestart ('$(if ($railAfter) { $railAfter.Title } else { 'missing' })') | playlist rows: " + $(if ($rowSeen.Length -gt 200) { $rowSeen.Substring(0, 200) + ' …' } else { $rowSeen }))

$script:Reads['remote'] = $script:RemoteTables
$script:Reads['remoteMenu'] = [pscustomobject]@{ primary = $primary; items = @($script:RemoteMenuNames) }
$script:Reads['remoteAdd'] = [pscustomobject]@{
    playlist = $script:HarnessPlaylist; row = $script:AddedRowTitle
    countBeforeRestart = $countBeforeRestart; countAfterRestart = $countAfterRestart; storeRows = $diskRows; storeTitle = $diskTitle
}

# The app's own account of the write: the code-behind logs the submenu it built,
# and the playlist store logs the switch that re-reads it from the database.
$remoteLog = @(Read-AppLogSince $script:LogStart | Where-Object {
        $_ -match "remote row menu|playlist switch '|playlist store:|Added " })
$script:Reads['logRemote'] = @($remoteLog | Select-Object -Last 20 | ForEach-Object { Clip-Line $_ 240 })
foreach ($l in $script:Reads['logRemote']) { Log ("  LOG remote: {0}" -f $l) }

Log ("  step 5 done: primary=$primary primaryOk=$primaryOk persisted=$persistedOk")

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

Log ''
$pass = @($script:Assertions | Where-Object { $_.pass -eq $true }).Count
$fail = @($script:Assertions | Where-Object { $_.pass -eq $false }).Count
Log ("=== RESULT: {0} passed, {1} failed, {2} informational ===" -f $pass, $fail, (@($script:Assertions | Where-Object { $null -eq $_.pass }).Count))
foreach ($a in $script:Assertions) {
    $tag = if ($a.pass -eq $true) { 'PASS' } elseif ($a.pass -eq $false) { 'FAIL' } else { 'INFO' }
    Log ("  {0} {1,-4} {2}" -f $tag, $a.id, $a.assertion)
}

exit (Write-Result $null)
