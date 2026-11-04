# uninstall-service.ps1
# Stops and removes the ParentalGuard Windows Service. Does NOT delete usage data in
# %ProgramData%\ParentalGuard (delete that by hand if you want a clean slate).
# Run elevated.

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$ServiceName = "ParentalGuard"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
    return
}

if ($svc.Status -ne 'Stopped') {
    Write-Host "Stopping service..." -ForegroundColor Cyan
    Stop-Service $ServiceName -Force
}

Write-Host "Removing service..." -ForegroundColor Cyan
sc.exe delete $ServiceName | Out-Null
Write-Host "Removed. Data retained at $env:ProgramData\ParentalGuard" -ForegroundColor Green
