#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and unregisters the StatusHub Windows Service.

.DESCRIPTION
    Published files are kept unless -RemoveFiles is given. Hook logs are never deleted.
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ClaudePet\StatusHub'),
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'

# Must match ServiceHost.ServiceName.
$serviceName = 'ClaudePetStatusHub'

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Write-Host "Stopping service '$serviceName'..."
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
    Write-Host "Service '$serviceName' removed."
}
else {
    Write-Host "Service '$serviceName' is not installed."
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Removed $InstallDir"
}
