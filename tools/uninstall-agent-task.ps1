# uninstall-agent-task.ps1
# Removes the per-child Agent logon task. Does not delete the published files. Run elevated.
#
#   .\uninstall-agent-task.ps1 child

#Requires -RunAsAdministrator

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $ChildUser
)

$ErrorActionPreference = "Stop"
$TaskName = "ParentalGuardAgent-$ChildUser"

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $task)
{
    Write-Host "Task '$TaskName' is not registered." -ForegroundColor Yellow
    return
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "Removed scheduled task '$TaskName'." -ForegroundColor Green
