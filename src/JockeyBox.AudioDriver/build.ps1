# Builds, catalogs and test-signs the JockeyBox phase 1 virtual audio driver.
# Must run on the Windows host. Run from this directory:
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#
# Output lands in .\out: JockeyBox.Line.sys, JockeyBox.Line.cat and a self-signed
# code-signing certificate exported to files (JockeyBoxTest.pfx/.cer).
#
# Nothing is installed here: no devcon, no INF install, no certificate in
# Trusted Root or Trusted Publishers, no TESTSIGNING change, no reboot.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'out'
$wdkVersion = '10.0.26100.0'

$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $inst = & $vswhere -latest -products * -property installationPath
        if ($inst) { $msbuild = Join-Path $inst 'MSBuild\Current\Bin\MSBuild.exe' }
    }
}
if (-not (Test-Path $msbuild)) { throw 'MSBuild.exe not found; Visual Studio 2022 with WDK is required' }
Write-Host "msbuild: $msbuild"

$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$inf2cat = Join-Path $kits "bin\$wdkVersion\x86\Inf2Cat.exe"   # x86 only on this kit
$signtool = Join-Path $kits "bin\$wdkVersion\x64\signtool.exe"
if (-not (Test-Path $inf2cat)) { throw "Inf2Cat.exe not found at $inf2cat" }
if (-not (Test-Path $signtool)) { throw "signtool.exe not found at $signtool" }

New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host '--- JockeyBox.Line.sys ---'
& $msbuild (Join-Path $root 'JockeyBox.Line.vcxproj') /nologo /m `
    /p:Configuration=Release `
    /p:Platform=x64 `
    /p:PlatformToolset=WindowsKernelModeDriver10.0 `
    /p:WindowsTargetPlatformVersion=$wdkVersion `
    /p:TargetVersion=Windows10 `
    /p:SignMode=Off `
    /p:SpectreMitigation=false
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed ($LASTEXITCODE)" }

$sys = Join-Path $out 'JockeyBox.Line.sys'
if (-not (Test-Path $sys)) { throw "Driver not produced: $sys" }
Copy-Item -Force (Join-Path $root 'JockeyBox.Line.inf') $out
Copy-Item -Force (Join-Path $root 'JockeyBox.Line.pdb') $out -ErrorAction SilentlyContinue

Write-Host '--- catalog ---'
& $inf2cat /driver:$out /os:10_X64
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed ($LASTEXITCODE)" }

# File-only test certificate: exported to a .pfx, then removed from the user
# store again so this lane leaves no certificate installed anywhere.
$pfx = Join-Path $out 'JockeyBoxTest.pfx'
$cer = Join-Path $out 'JockeyBoxTest.cer'
$password = 'JockeyBoxTest!'
if (-not (Test-Path $pfx)) {
    Write-Host '--- test certificate ---'
    $cert = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject 'CN=JockeyBox Virtual Audio Test' `
        -KeyExportPolicy Exportable `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(2) `
        -CertStoreLocation 'Cert:\CurrentUser\My'
    $secure = ConvertTo-SecureString -String $password -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Remove-Item -Path ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force
}

Write-Host '--- sign ---'
& $signtool sign /fd SHA256 /f $pfx /p $password $sys
if ($LASTEXITCODE -ne 0) { throw "signtool failed on .sys ($LASTEXITCODE)" }
& $signtool sign /fd SHA256 /f $pfx /p $password (Join-Path $out 'JockeyBox.Line.cat')
if ($LASTEXITCODE -ne 0) { throw "signtool failed on .cat ($LASTEXITCODE)" }

Write-Host '--- result ---'
Get-ChildItem -Path $out -File |
    Where-Object { $_.Name -in @('JockeyBox.Line.sys', 'JockeyBox.Line.cat') } |
    ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash
        Write-Host ("{0}  {1} bytes  sha256 {2}" -f $_.Name, $_.Length, $hash)
    }
