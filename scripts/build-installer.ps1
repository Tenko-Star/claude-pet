<#
.SYNOPSIS
    Publishes StatusHub.Service and DeskPet.App self-contained and compiles the Inno Setup installer.

.DESCRIPTION
    Output: artifacts\installer\ClaudePet-Setup-<Version>.exe. Requires the .NET 10 SDK and Inno Setup 6.
    Does not need administrator rights; the produced installer does.
    With -Install, the installer is then run without its wizard (UAC asks once) and the pet is started.
#>
param(
    [string]$Version = '0.1.0',
    [string]$Runtime = 'win-x64',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot 'artifacts\publish'

function Publish-App([string]$project, [string]$name) {
    $output = Join-Path $publishRoot $name
    if (Test-Path $output) { Remove-Item -Recurse -Force $output }
    Write-Host "Publishing $name to $output..."
    dotnet publish (Join-Path $repoRoot $project) --configuration Release --runtime $Runtime --self-contained true --output $output "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $name failed with exit code $LASTEXITCODE." }
}

# ISCC on PATH first, then the per-user or machine-wide Inno Setup 6 installation.
function Find-Iscc {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $uninstallKeys = @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $uninstallKeys) {
        $location = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallLocation
        if ($location) {
            $iscc = Join-Path $location 'ISCC.exe'
            if (Test-Path $iscc) { return $iscc }
        }
    }
    throw 'ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup).'
}

$iscc = Find-Iscc

Publish-App 'src\StatusHub.Service\StatusHub.Service.csproj' 'StatusHub'
Publish-App 'src\DeskPet.App\DeskPet.App.csproj' 'DeskPet'

Write-Host "Compiling installer with $iscc..."
& $iscc "/DAppVersion=$Version" (Join-Path $repoRoot 'installer\ClaudePet.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$installer = Join-Path $repoRoot "artifacts\installer\ClaudePet-Setup-$Version.exe"
Write-Host "Installer: $installer"

if ($Install) {
    Write-Host 'Installing...'
    # The installer requires administrator rights, so Windows shows the UAC prompt here.
    $process = Start-Process -FilePath $installer -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer failed with exit code $($process.ExitCode)." }

    # Uninstall key of installer\ClaudePet.iss (its AppId plus "_is1").
    $installerKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6B7E-4D2A-9C1F-3E5B7A9D0C42}_is1'
    $appDir = (Get-ItemProperty -Path $installerKey).'Inno Setup: App Path'
    # Started from this script, so the pet runs with the script's rights rather than the installer's.
    Start-Process -FilePath (Join-Path $appDir 'DeskPet\DeskPet.App.exe')
    Write-Host 'Installed. Run /reload-plugins in open Claude Code sessions.'
}
