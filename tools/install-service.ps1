# install-service.ps1
# Publishes and installs ParentalGuard.Service as a Windows Service running as LocalSystem.
# Run from an ELEVATED PowerShell (Administrator). Test in a VM first.

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

$ServiceName = "ParentalGuard"
$DisplayName = "ParentalGuard"
$Description = "Tracks usage and enforces screen-time limits and bedtime for child accounts."

# Resolve repo root (this script lives in <root>\tools).
$Root      = Split-Path -Parent $PSScriptRoot
$Project   = Join-Path $Root "src\ParentalGuard.Service\ParentalGuard.Service.csproj"
$PublishDir= Join-Path $env:ProgramFiles "ParentalGuard\Service"
$ExePath   = Join-Path $PublishDir "ParentalGuard.Service.exe"

Write-Host "Publishing service to $PublishDir ..." -ForegroundColor Cyan
dotnet publish $Project -c Release -r win-x64 --self-contained false -o $PublishDir

# Remove any previous instance so we can re-register cleanly.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing existing service..." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Registering service..." -ForegroundColor Cyan
New-Service -Name $ServiceName `
            -BinaryPathName "`"$ExePath`"" `
            -DisplayName $DisplayName `
            -Description $Description `
            -StartupType Automatic | Out-Null

# Run as LocalSystem (the default for New-Service is already LocalSystem; make it explicit
# and set recovery so Windows restarts it if it ever crashes).
sc.exe config $ServiceName obj= "LocalSystem" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service $ServiceName

Get-Service $ServiceName | Format-List Name, Status, StartType
Write-Host "Done. Data lives in $env:ProgramData\ParentalGuard" -ForegroundColor Green
