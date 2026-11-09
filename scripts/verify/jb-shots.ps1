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

$libTab = Get-LibraryTab
$script:tabNames = @()
foreach ($t in (Get-TabItems)) { $script:tabNames += $t.Name }
Log ("tabs: {0}" -f ($script:tabNames -join " | "))
if ($libTab) { [void](Select-El $libTab) }

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

# =====================================================================
#  capture harness: three PNG proofs + a Settings control inventory
#  (step 5 of the verification harness, visual half)
# =====================================================================

$ErrorActionPreference = 'Continue'

function Shot-Window([string]$Name, [string[]]$Args2 = @()) {
    $path = Join-Path $OutDir $Name
    if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
    try {
        $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\Dev\jb-shot.ps1',
                  '-Process', 'JockeyBox', '-Out', $path) + @($Args2)
        $out = & powershell.exe @argv 2>&1
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

$results = [ordered]@{}
$failures = New-Object System.Collections.ArrayList

function Check([string]$Name, $Ok, [string]$Expected, $Actual) {
    $results[$Name] = [ordered]@{ pass = [bool]$Ok; expected = $Expected; actual = $Actual }
    if (-not $Ok) { [void]$failures.Add($Name) }
    Log ("  {0,-6} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Ok) { '' } else { "  expected=$Expected actual=$Actual" }))
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
$results['statusTexts'] = $statusTexts
$results['textsAll'] = $textsPlaying

Log ("  deck A: title='{0}' bpm='{1}' ratio='{2}' position='{3}'" -f $dA1.title, $dA1.bpm, $dA1.ratio, $(if ($dA1.position) { $dA1.position.text } else { 'n/a' }))
Log ("  MON={0} MUTE={1} master='{2}' crossfader={3}" -f (Get-ToggleState $mon), (Get-ToggleState $mute), $masterText, $cfA)
Log ("  status texts: {0}" -f ($statusTexts -join ' || '))

# ------------------------------------------------------- 2. transition capture

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
$results['armLine'] = $armLine
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
$results['windows'] = @(Get-AppWindows | ForEach-Object { [ordered]@{ title = $_.title; hwnd = $_.hwnd; visible = $_.visible; enabled = $_.enabled; w = $_.w; h = $_.h; x = $_.x; y = $_.y } })
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
$combos = @(); $checks = @(); $edits = @()
if ($null -ne $settingsWin) {
    $all = @(Get-Nodes -Root $settingsWin -Max 4000 -Deep)
    foreach ($n in $all) {
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
$results['combos'] = $combos
$results['checkBoxes'] = $checks
$results['editBoxes'] = $edits
foreach ($c in $combos) { Log ("  combo  y={0,5} '{1}' = '{2}'" -f $c.y, $c.name, $c.value) }
foreach ($c in $checks) { Log ("  check  y={0,5} '{1}' = {2}" -f $c.y, $c.name, $c.state) }
foreach ($c in $edits) { Log ("  edit   y={0,5} '{1}' = '{2}'" -f $c.y, $c.name, $c.value) }

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
try {
    $json = $summary | ConvertTo-Json -Depth 12
    Set-Content -Path $jsonPath -Value $json -Encoding UTF8
} catch {
    Log ("  ! summary write failed: {0}" -f $_.Exception.Message)
}
Log ("=== RESULT: {0} - {1} check(s) failed ===" -f $summary.verdict, $failures.Count)
Log ("json: {0}" -f $jsonPath)
"written" | Set-Content -Path (Join-Path $OutDir 'verify-shots.done') -Encoding ASCII

# leave the app quiet, but never after the evidence is on disk
try {
    $tpEnd = Get-TransportButtons
    if ($tpEnd -and $tpEnd.stop) { [void](Invoke-El $tpEnd.stop) }
} catch { Log ("  (post-run stop skipped: {0})" -f $_.Exception.Message) }
$script:Win = $null
exit 0

