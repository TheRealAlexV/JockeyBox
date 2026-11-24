# virtual-asio-probe.ps1
# Answers one question the operator cannot answer by ear: is JockeyBox putting
# non-silent audio into the virtual ASIO ring, or is the ring empty/stalled?
#
# Read-only. It never touches OBS, never registers the DLL, never starts or stops
# JockeyBox. Run it while a track is playing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File virtual-asio-probe.ps1 [bus] [dll]
#
# The harness is rebuilt on demand when it is absent or older than the native
# sources, so a cached binary can never answer for the current tree. A harness
# that does not advertise --attach is refused: this probe prints no audio verdict
# unless it has first proved it can run the attach path.
#
# Writes the verdict to stdout and to attach\virtual-asio-status.txt.

param(
    [string]$Bus = 'main',
    [string]$Dll,
    [string]$Harness
)

$ErrorActionPreference = 'Continue'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$root   = Split-Path -Parent $here
$asio   = Join-Path $root 'src\JockeyBox.Asio'
$build  = Join-Path $asio 'build.ps1'
if (-not $Dll) {
    # Build output of this repo, not a path from the machine it was written on;
    # pass -Dll to point at a different build.
    $Dll = Join-Path $asio 'out\JockeyBox.Asio.dll'
}
$attach = Join-Path $here 'attach'
$customHarness = [bool]$Harness
if (-not $Harness) {
    $Harness = Join-Path $asio 'out\AsioHostHarness.exe'
}
$exe    = $Harness
$status = Join-Path $attach 'virtual-asio-status.txt'
$raw    = Join-Path $attach 'virtual-asio-probe-raw.log'

if (-not (Test-Path $attach)) {
    New-Item -ItemType Directory -Path $attach -Force | Out-Null
}

function Write-Verdict([string]$verdict, [string]$detail) {
    $lines = @(
        "virtual ASIO probe  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "bus                  $Bus",
        "verdict              $verdict",
        "detail               $detail",
        "raw output           $raw"
    )
    $lines | ForEach-Object { Write-Host $_ }
    $lines | Set-Content -Path $status -Encoding ascii
}

# Refusal is not a verdict. This prints the harness path and the missing
# capability, writes no status verdict, and exits non-zero.
function Write-Refusal([string]$detail) {
    Write-Host "virtual ASIO probe  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-Host "result               REFUSED"
    Write-Host "harness              $exe"
    Write-Host "detail               $detail"
    exit 3
}

function Test-ContainsWideToken([string]$Path, [string]$Token) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        $text  = [System.Text.Encoding]::Unicode.GetString($bytes)
        return $text.Contains($Token)
    } catch {
        return $false
    }
}

# 1. Build on demand. Staleness is the harness mtime against the newest of every
#    .cpp, .h and build.ps1 under src\JockeyBox.Asio (the sources the harness is
#    compiled from). A custom -Harness is an explicit override and is not rebuilt.
if (-not $customHarness) {
    if (-not (Test-Path $build)) {
        Write-Refusal "build script not found at $build; cannot prove the harness is current"
    }

    $sources = @(Get-ChildItem -LiteralPath $asio -Recurse -File |
        Where-Object { $_.Extension -in '.cpp', '.h' -or $_.Name -eq 'build.ps1' })
    if ($sources.Count -eq 0) {
        Write-Refusal "no .cpp/.h/build.ps1 sources found under $asio; cannot key staleness"
    }
    $newest = $sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

    $needBuild = (-not (Test-Path $exe)) -or (-not (Test-Path $Dll))
    if (-not $needBuild) {
        $sourceTime = $newest.LastWriteTimeUtc
        $needBuild = ($sourceTime -gt (Get-Item -LiteralPath $exe).LastWriteTimeUtc) -or
                     ($sourceTime -gt (Get-Item -LiteralPath $Dll).LastWriteTimeUtc)
    }

    if ($needBuild) {
        Write-Host "harness is absent or older than its sources; building $asio"
        Write-Host "newest source       $($newest.FullName)  $($newest.LastWriteTimeUtc.ToString('u'))"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build
        $rc = $LASTEXITCODE
        if ($rc -ne 0) {
            Write-Refusal "build.ps1 exited $rc; $exe was not refreshed, so no verdict can be trusted"
        }
        if (-not (Test-Path $exe)) {
            Write-Refusal "build.ps1 exited 0 but did not produce $exe"
        }
    }
}

if (-not (Test-Path $exe)) {
    Write-Refusal "harness not found at $exe"
}
if (-not (Test-Path $Dll)) {
    Write-Refusal "driver DLL not found at $Dll"
}

# 2. Capability gate. Old harnesses ignore --attach and silently run their
#    default ASIO suite, which looks exactly like an idle ring. Never let one
#    answer. Check the binary token first (so a stale build is not executed),
#    then require the harness's own --help text to advertise the flag.
if (-not (Test-ContainsWideToken $exe '--attach')) {
    Write-Refusal "$exe contains no --attach capability (stale or foreign harness); refusing to run it"
}

$helpOut = Join-Path $env:TEMP ("jb-asio-help-" + [guid]::NewGuid().ToString('N') + ".out")
$helpErr = "$helpOut.err"
$help = ''
try {
    $p = Start-Process -FilePath $exe -ArgumentList '--help' -NoNewWindow -PassThru `
        -RedirectStandardOutput $helpOut -RedirectStandardError $helpErr
    if (-not $p.WaitForExit(10000)) {
        try { $p.Kill() } catch { }
        Write-Refusal "$exe did not return from --help within 10 s; cannot confirm --attach"
    }
    $help = ((Get-Content -LiteralPath $helpOut -Raw -ErrorAction SilentlyContinue) +
             "`n" + (Get-Content -LiteralPath $helpErr -Raw -ErrorAction SilentlyContinue))
} catch {
    Write-Refusal "could not run $exe --help: $($_.Exception.Message)"
} finally {
    Remove-Item -LiteralPath $helpOut, $helpErr -Force -ErrorAction SilentlyContinue
}

if ($help -notmatch '--attach') {
    Write-Refusal "$exe does not advertise --attach in its --help/usage output; refusing to run it"
}

$out = & $exe $Dll --attach $Bus 512 2>&1 | Out-String
$out | Set-Content -Path $raw -Encoding ascii

# The app publishes Local\JockeyBox.Bus.<name>. When a kernel driver owns the
# pair, the sink attaches to the driver's Global\JockeyBox.Bus.<pair> instead.
# Either is a live ring; the harness names the one it opened.
$section = if ($out -match 'section\s+:\s+((?:Local|Global)\\JockeyBox\.Bus\.\S+)') { $Matches[1] } else { $null }

if (-not $section) {
    Write-Verdict 'NO RING' ("neither Local\JockeyBox.Bus.$Bus nor Global\JockeyBox.Bus.$Bus " +
        'exists. JockeyBox is not running, is a build that predates the ring publisher, has ' +
        'no virtual bus ticked in Settings, or the kernel driver that creates the pair ' +
        'section is not installed. A non-elevated process cannot create Global\, so that ' +
        'scope is expected to be absent here; its failure is a permissions limitation, not ' +
        'a second app fault. The driver has nothing to read and serves silence.')
    exit 1
}

$blocks = if ($out -match 'blocks published\s+:\s+(\d+)\s+by the app') { [int]$Matches[1] } else { 0 }
$ratio  = if ($out -match 'non-silent ratio\s+:\s+([0-9.]+)') { [double]$Matches[1] } else { 0.0 }
$silent = if ($out -match 'silent callbacks\s+:\s+(\d+)') { [int]$Matches[1] } else { 0 }

if ($blocks -eq 0) {
    Write-Verdict 'RING IDLE' ("$section exists but the app published 0 blocks " +
        'in 3 s. The render thread is not running, or nothing is playing.')
    exit 1
}
if ($ratio -le 0.0) {
    Write-Verdict 'PUBLISHING SILENCE' ("${section}: $blocks blocks/s but every sample is zero. " +
        'Check which bus is ticked in Settings (Monitor carries deck audio only while MON ' +
        'is on) and that a deck is actually playing.')
    exit 1
}

Write-Verdict 'OK - AUDIO FLOWING' ("${section}: $blocks blocks/s, non-silent ratio $ratio, " +
    "$silent silent callbacks. JockeyBox is feeding the driver. Global\ was not required; " +
    'creating it needs elevation. If OBS is still silent, the fault is in OBS''s ASIO ' +
    'source routing, not JockeyBox.')
exit 0
