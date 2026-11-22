# Builds the virtual ASIO driver DLL and the host harness. Must run on the
# Windows host (MSVC), not in WSL. Run from this directory:
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#
# Output lands in .\out.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'out'

$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $inst = & $vswhere -latest -products * -property installationPath
        if ($inst) { $vcvars = Join-Path $inst 'VC\Auxiliary\Build\vcvars64.bat' }
    }
}
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found; MSVC 14.x is required" }
Write-Host "vcvars: $vcvars"

New-Item -ItemType Directory -Force -Path $out | Out-Null
$flags = '/nologo /O2 /EHsc /std:c++17 /W3 /I"' + (Join-Path $root 'include') + '"'

Push-Location $out
try {
    Write-Host '--- JockeyBox.Asio.dll ---'
    $dll = 'call "' + $vcvars + '" >nul && cl ' + $flags + ' /LD' +
           ' /Fe:JockeyBox.Asio.dll' +
           ' "' + (Join-Path $root 'src\dllmain.cpp') + '"' +
           ' "' + (Join-Path $root 'src\VirtualAsioDriver.cpp') + '"' +
           ' /link /DEF:"' + (Join-Path $root 'JockeyBox.Asio.def') + '"'
    cmd /c $dll
    if ($LASTEXITCODE -ne 0) { throw "DLL build failed ($LASTEXITCODE)" }

    Write-Host '--- AsioHostHarness.exe ---'
    $exe = 'call "' + $vcvars + '" >nul && cl ' + $flags +
           ' /Fe:AsioHostHarness.exe' +
           ' "' + (Join-Path $root 'tools\Harness.cpp') + '" ole32.lib advapi32.lib'
    cmd /c $exe
    if ($LASTEXITCODE -ne 0) { throw "Harness build failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

Get-ChildItem $out | Where-Object { $_.Extension -in '.dll', '.exe' } |
    Select-Object Name, Length
