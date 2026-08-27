# Build the MusicPlayer MSI installer end-to-end.
#
#   powershell -ExecutionPolicy Bypass -File installer/build_msi.ps1
#
# Steps: dotnet publish (self-contained, auto-trims locale folders) →
#        compile Uninstall.exe into the publish output →
#        regenerate files.wxs from the publish output →
#        wix build → installer/MusicPlayer.msi
#
# Requires on PATH: dotnet, wix (WiX v4 global tool), python.
# WiX extensions (WixToolset.UI.wixext / WixToolset.Util.wixext) are restored
# automatically by `wix build` into .wix/extensions on first run.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
Set-Location $root

$publishDir = "bin/Release/net10.0-windows10.0.26100.0/win-x64/publish"
$outMsi     = "installer/MusicPlayer.msi"

Write-Host "1/4  dotnet publish -c Release (self-contained, trims locales) ..." -ForegroundColor Cyan
dotnet publish -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

Write-Host "2/4  building standalone Uninstall.exe ..." -ForegroundColor Cyan
# Compiled with the .NET Framework csc that ships with Windows: zero
# dependencies, a few KB, and no .NET SDK needed on the target machine.
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:anycpu `
       /out:"$publishDir\Uninstall.exe" `
       "$root\installer\uninstall\Uninstall.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed (exit $LASTEXITCODE)" }

Write-Host "3/4  regenerating installer/files.wxs ..." -ForegroundColor Cyan
python installer/gen_files_wxs.py $publishDir installer/files.wxs
if ($LASTEXITCODE -ne 0) { throw "gen_files_wxs.py failed (exit $LASTEXITCODE)" }
# gen_files_wxs.py picks up everything in the publish dir (except .pdb),
# so Uninstall.exe lands in INSTALLFOLDER automatically.

Write-Host "4/4  wix build MusicPlayer.msi ..." -ForegroundColor Cyan
wix build installer/installer.wxs installer/files.wxs `
       -ext WixToolset.UI.wixext `
       -ext WixToolset.Util.wixext `
       -arch x64 `
       -o $outMsi
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "Done. MSI: $outMsi ($([math]::Round((Get-Item $outMsi).Length / 1MB, 1)) MB)" -ForegroundColor Green
