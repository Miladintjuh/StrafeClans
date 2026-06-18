$ErrorActionPreference = "Stop"

Write-Host "Cleaning bin/obj..." -ForegroundColor Cyan
Remove-Item -Recurse -Force .\bin, .\obj -ErrorAction SilentlyContinue

Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore

Write-Host "Building Release..." -ForegroundColor Cyan
dotnet build -c Release

Write-Host "Starting StrafeLab..." -ForegroundColor Cyan
dotnet run -c Release