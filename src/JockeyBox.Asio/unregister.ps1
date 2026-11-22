# Removes the JockeyBox Virtual ASIO registration. REQUIRES ELEVATION.
#
#   powershell -ExecutionPolicy Bypass -File .\unregister.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'unregister.ps1 must run elevated (the registration lives in HKLM).'
}

$driverName = 'JockeyBox Virtual ASIO'
$clsid = '{7E1B5C2A-9F4D-4B3A-8E6C-5D2A1F0B9C31}'

foreach ($path in @("HKLM:\SOFTWARE\ASIO\$driverName",
                    "HKLM:\SOFTWARE\Classes\CLSID\$clsid")) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
        Write-Host "Removed $path"
    } else {
        Write-Host "Not present: $path"
    }
}
