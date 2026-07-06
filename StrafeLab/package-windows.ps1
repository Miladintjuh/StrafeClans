param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

.\publish-windows.ps1 -Configuration $Configuration -Runtime $Runtime

$publishDir = Join-Path $root "publish\StrafeLab-$Runtime"
$zipPath = Join-Path $root "publish\StrafeLab-$Runtime.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
Write-Host "Packaged $zipPath" -ForegroundColor Green

if (Get-Command iscc -ErrorAction SilentlyContinue) {
    Write-Host "Inno Setup detected; building installer..." -ForegroundColor Cyan
    iscc .\installer\StrafeLab.iss
} else {
    Write-Host "Inno Setup compiler not found. ZIP package is ready; installer skipped." -ForegroundColor Yellow
}
