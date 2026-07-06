param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "Cleaning old build folders..." -ForegroundColor Cyan
Remove-Item -Recurse -Force .\bin, .\obj -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\publish -ErrorAction SilentlyContinue

Write-Host "Restoring..." -ForegroundColor Cyan
dotnet restore .\StrafeLab.csproj

Write-Host "Publishing StrafeLab for $Runtime..." -ForegroundColor Cyan
dotnet publish .\StrafeLab.csproj -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\StrafeLab-$Runtime

Write-Host "Published to $root\publish\StrafeLab-$Runtime" -ForegroundColor Green
Write-Host "Run .\publish\StrafeLab-$Runtime\StrafeLab.exe" -ForegroundColor Green
