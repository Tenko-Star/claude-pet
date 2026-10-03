#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes StatusHub.Service and registers it as an auto-start Windows Service.

.DESCRIPTION
    Re-running the script updates an existing installation: the service is stopped,
    removed, republished and registered again. Hook logs in DataDirectory are kept.
    If Claude Pet was installed with the installer, it is uninstalled first and the Claude Code
    plugin is registered from this repository instead; the pet is then started with dotnet run.
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

# Uninstall key of installer\ClaudePet.iss (its AppId plus "_is1").
$installerKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6B7E-4D2A-9C1F-3E5B7A9D0C42}_is1'

# The installer ships a self-contained runtime and its own copy of the plugin; mixing that with this
# framework-dependent publish breaks startup, so it is removed completely first.
function Remove-InstallerInstallation {
    $uninstaller = (Get-ItemProperty -Path $installerKey -ErrorAction SilentlyContinue).UninstallString
    if (-not $uninstaller) { return $false }

    Write-Host 'Removing the installation made by the installer...'
    $process = Start-Process -FilePath $uninstaller.Trim('"') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Uninstaller failed with exit code $($process.ExitCode)." }
    for ($i = 0; $i -lt 60 -and (Test-Path $installerKey); $i++) {
        Start-Sleep -Milliseconds 500
    }
    if (Test-Path $installerKey) { throw 'The installer installation is still registered after uninstalling.' }
    return $true
}

# Same commands as the installer, but pointing at the repository's plugin folder.
function Register-RepoPlugin {
    if (-not (Get-Command 'claude' -ErrorAction SilentlyContinue)) {
        Write-Warning 'claude not found; register the plugin manually from the plugin folder of this repository.'
        return
    }
    $pluginDir = Join-Path $repoRoot 'plugin'
    Write-Host "Registering the Claude Code plugin from $pluginDir..."
    # Fails harmlessly when nothing is registered; Windows PowerShell would turn its stderr into a terminating error.
    $ErrorActionPreference = 'Continue'
    claude plugin marketplace remove deskpet-local 2>$null
    claude plugin marketplace add $pluginDir
    if ($LASTEXITCODE -ne 0) { throw "claude plugin marketplace add failed with exit code $LASTEXITCODE." }
    claude plugin install deskpet-hooks@deskpet-local
    if ($LASTEXITCODE -ne 0) { throw "claude plugin install failed with exit code $LASTEXITCODE." }
}

$switchedFromInstaller = Remove-InstallerInstallation
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

if ($switchedFromInstaller) {
    Register-RepoPlugin
    Write-Host 'Run /reload-plugins in open Claude Code sessions.'
    Write-Host "Start the pet with: dotnet run --project `"$(Join-Path $repoRoot 'src\DeskPet.App')`""
}
