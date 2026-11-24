# virtual-asio-probe.ps1
# Answers one question the operator cannot answer by ear: is JockeyBox putting
# non-silent audio into the virtual ASIO ring, or is the ring empty/stalled?
#
# Read-only. It never touches OBS, never registers the DLL, never starts or stops
# JockeyBox. Run it while a track is playing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File virtual-asio-probe.ps1 [bus] [dll]
#
# Writes the verdict to stdout and to attach\virtual-asio-status.txt.

param(
    [string]$Bus = 'main',
    [string]$Dll
)

$ErrorActionPreference = 'Continue'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$root   = Split-Path -Parent $here
if (-not $Dll) {
    # Build output of this repo, not a path from the machine it was written on;
    # pass -Dll to point at a different build.
    $Dll = Join-Path $root 'src\JockeyBox.Asio\out\JockeyBox.Asio.dll'
}
$attach = Join-Path $here 'attach'
$exe    = Join-Path $root 'src\JockeyBox.Asio\out\AsioHostHarness.exe'
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

if (-not (Test-Path $exe)) {
    Write-Verdict 'PROBE MISSING' "harness not found at $exe"
    exit 2
}
if (-not (Test-Path $Dll)) {
    Write-Verdict 'PROBE MISSING' "driver DLL not found at $Dll"
    exit 2
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
        'section is not installed. The driver has nothing to read and serves silence.')
    exit 1
}

$blocks = if ($out -match 'blocks published\s+:\s+(\d+)\s+by the app') { [int]$Matches[1] } else { 0 }
$ratio  = if ($out -match 'non-silent ratio\s+:\s+([0-9.]+)') { [double]$Matches[1] } else { 0.0 }
$silent = if ($out -match 'silent callbacks\s+:\s+(\d+)') { [int]$Matches[1] } else { 0 }

if ($blocks -eq 0) {
    Write-Verdict 'RING IDLE' ('the ring section exists but the app published 0 blocks ' +
        'in 3 s. The render thread is not running, or nothing is playing.')
    exit 1
}
if ($ratio -le 0.0) {
    Write-Verdict 'PUBLISHING SILENCE' ("$blocks blocks/s but every sample is zero. " +
        'Check which bus is ticked in Settings (Monitor carries deck audio only while MON ' +
        'is on) and that a deck is actually playing.')
    exit 1
}

Write-Verdict 'OK - AUDIO FLOWING' ("$blocks blocks/s, non-silent ratio $ratio, " +
    "$silent silent callbacks. JockeyBox is feeding the driver. If OBS is still silent, " +
    'the fault is in OBS''s ASIO source routing, not JockeyBox.')
exit 0
