# install-agent-task.ps1
# Publishes ParentalGuard.Agent and registers a per-child scheduled task that starts it at
# logon and restarts it if it ever exits. Run elevated. Test in a VM first.
#
#   .\install-agent-task.ps1 child
#
# 'child' is the Windows account name of the child the Agent should run for.

#Requires -RunAsAdministrator

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $ChildUser
)

$ErrorActionPreference = "Stop"

$Root       = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $Root "src\ParentalGuard.Agent\ParentalGuard.Agent.csproj"
$PublishDir = Join-Path $env:ProgramFiles "ParentalGuard\Agent"
$Exe        = Join-Path $PublishDir "ParentalGuard.Agent.exe"
$TaskName   = "ParentalGuardAgent-$ChildUser"

Write-Host "Publishing Agent to $PublishDir ..." -ForegroundColor Cyan
dotnet publish $Project -c Release -r win-x64 --self-contained false -o $PublishDir

Write-Host "Registering logon task '$TaskName' ..." -ForegroundColor Cyan

$action = New-ScheduledTaskAction -Execute $Exe

# Fires when THIS child logs on.
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $ChildUser

# Runs in the child's own session as a standard user (RunLevel Limited).
$principal = New-ScheduledTaskPrincipal -UserId $ChildUser -LogonType Interactive -RunLevel Limited

# Keep it alive: restart if it exits, never time-limit it, run on battery too.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

# Replace any previous registration.
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings | Out-Null

Write-Host "Done. '$TaskName' will launch the Agent when '$ChildUser' logs on." -ForegroundColor Green
Write-Host "Note: this handles logon + crashes. Instant relaunch after a deliberate kill is a" -ForegroundColor DarkYellow
Write-Host "      hardening item (Service-side CreateProcessAsUser) planned for a later milestone." -ForegroundColor DarkYellow
