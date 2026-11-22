# Registers JockeyBox Virtual ASIO for real ASIO hosts.
#
# REQUIRES ELEVATION. This writes HKLM, which hosts such as BASS/obs-asio and
# NAudio enumerate: they list HKLM\SOFTWARE\ASIO subkeys and open the CLSID each
# one names. It is not run by the build or by the harness.
#
#   powershell -ExecutionPolicy Bypass -File .\register.ps1 -DllPath .\out\JockeyBox.Asio.dll

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DllPath
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'register.ps1 must run elevated (it writes HKLM\SOFTWARE\ASIO).'
}

$dll = (Resolve-Path -LiteralPath $DllPath).Path
if (-not (Test-Path -LiteralPath $dll)) { throw "DLL not found: $dll" }

$driverName = 'JockeyBox Virtual ASIO'
$clsid = '{7E1B5C2A-9F4D-4B3A-8E6C-5D2A1F0B9C31}'

# 1. What hosts enumerate: HKLM\SOFTWARE\ASIO\<name> -> {CLSID, Description}
$asioKey = "HKLM:\SOFTWARE\ASIO\$driverName"
New-Item -Path $asioKey -Force | Out-Null
New-ItemProperty -Path $asioKey -Name 'CLSID' -Value $clsid -PropertyType String -Force | Out-Null
New-ItemProperty -Path $asioKey -Name 'Description' -Value $driverName -PropertyType String -Force | Out-Null

# 2. Where in-proc COM finds the DLL.
$inproc = "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32"
New-Item -Path $inproc -Force | Out-Null
New-ItemProperty -Path $inproc -Name '(Default)' -Value $dll -PropertyType String -Force | Out-Null
New-ItemProperty -Path $inproc -Name 'ThreadingModel' -Value 'Both' -PropertyType String -Force | Out-Null

Write-Host "Registered '$driverName'"
Write-Host "  $asioKey"
Write-Host "  $inproc -> $dll"
