#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes StatusHub.Service and registers it as an auto-start Windows Service.

.DESCRIPTION
    Re-running the script updates an existing installation: the service is stopped,
    removed, republished and registered again. Hook logs in DataDirectory are kept.
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ClaudePet\StatusHub'),
    [string]$DataDirectory = (Join-Path $env:ProgramData 'ClaudePet\hooks'),
    [ValidateRange(1, 65535)]
    [int]$Port = 47821
)

$ErrorActionPreference = 'Stop'

# Must match ServiceHost.ServiceName.
$serviceName = 'ClaudePetStatusHub'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\StatusHub.Service\StatusHub.Service.csproj'

function Remove-ExistingService {
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $existing) { return }

    Write-Host "Removing existing service '$serviceName'..."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }

    # The SCM removes the entry asynchronously.
    for ($i = 0; $i -lt 20 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Milliseconds 500
    }
}

Remove-ExistingService

Write-Host "Publishing to $InstallDir..."
dotnet publish $project --configuration Release --output $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $InstallDir 'StatusHub.Service.exe'
$binaryPath = "`"$exe`" --HookIngest:DataDirectory=`"$DataDirectory`" --HookIngest:Port=$Port"

Write-Host "Registering service '$serviceName'..."
New-Service -Name $serviceName `
    -BinaryPathName $binaryPath `
    -DisplayName 'Claude Pet StatusHub' `
    -Description 'Receives Claude Code hook events on localhost and broadcasts the aggregated status.' `
    -StartupType Automatic | Out-Null

# Restart after a crash: 5 s delay, failure count resets after one day.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE." }

Start-Service -Name $serviceName
Get-Service -Name $serviceName | Format-Table -AutoSize Name, Status, StartType

Write-Host "Listening on http://127.0.0.1:$Port"
Write-Host "Hook log: $(Join-Path $DataDirectory 'hook-events.jsonl')"
