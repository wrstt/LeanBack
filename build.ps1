# Builds the portable single-file LeanBack.exe into the folder root.
#
# WinUI 3 supports PublishSingleFile only when the app is BOTH unpackaged and self-contained
# (Windows App SDK 1.5+). Those properties live in the csproj; the SDK hard-errors if any of
# them go missing, so this script stays a thin wrapper.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\make-icon.ps1')

dotnet publish (Join-Path $root 'src\LeanBack.WinUI\LeanBack.WinUI.csproj') `
    -c Release -r win-x64 `
    -o (Join-Path $root 'release')

Copy-Item (Join-Path $root 'release\LeanBack.exe') (Join-Path $root 'LeanBack.exe') -Force
$exe = Get-Item (Join-Path $root 'LeanBack.exe')
Write-Output ("Built {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
