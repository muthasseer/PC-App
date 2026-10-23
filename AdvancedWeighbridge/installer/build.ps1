<#
.SYNOPSIS
  Builds AdvancedWeighbridgeSetup.exe on a Windows PC with the .NET 10 SDK and Inno Setup 6.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "out"
$desktop = Join-Path $out "desktop"

Write-Host "Publishing the desktop application ($Configuration, win-x64, self-contained)..."
dotnet publish (Join-Path $root "src\Weighbridge.Desktop\Weighbridge.Desktop.csproj") -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -o $desktop
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
$iscc = if ($cmd) { $cmd.Source } else { $null }
if (-not $iscc) {
    foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $candidate) { $iscc = $candidate; break }
    }
}
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isinfo.php" }

Write-Host "Building the installer with $iscc ..."
& $iscc "/DAppVersion=$Version" "/DSourceDir=$desktop" "/DOutputDir=$(Join-Path $out 'installer')" (Join-Path $PSScriptRoot "AdvancedWeighbridge.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Get-Item (Join-Path $out "installer\AdvancedWeighbridgeSetup.exe") | Format-List Name, Length, LastWriteTime
