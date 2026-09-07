# Copyright (c) 2026 The White Stag Collection.
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRef,
    [string]$InstallationRoot = (Join-Path $env:LOCALAPPDATA 'WorkbenchSelfHost'),
    [Parameter(Mandatory)][string]$Confirmation
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) { throw 'This updater requires Windows and Docker Desktop Linux containers.' }
if ($Confirmation -cne 'UPDATE Workbench') { throw "Specify -Confirmation 'UPDATE Workbench' to authorize downtime, checkpoint, and migration." }
. "$PSScriptRoot/local-self-host/Configuration.ps1"
. "$PSScriptRoot/local-self-host/Common.ps1"
. "$PSScriptRoot/local-self-host/Update.ps1"
$settings = Get-LocalSetupConfiguration @{TenantName='Update';AdminEmail='update@example.test';InstallationRoot=$InstallationRoot;SourceRef=$SourceRef}
$root = $settings.InstallationRoot
if (-not (Test-Path -LiteralPath "$root/installation.json" -PathType Leaf)) { throw 'Completed local self-host installation not found.' }
# Reject linked or repository-owned paths before creating any operational artifacts.
$cursor = Get-Item -LiteralPath $root
while ($cursor) {
    if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Test-Path -LiteralPath (Join-Path $cursor.FullName '.git'))) { throw 'Installation must be outside source control and reparse points.' }
    $cursor = $cursor.Parent
}
if (Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Installation contains unsupported reparse points.' }
Assert-LocalEnvironment
$docker = Get-LocalDocker
if ((Invoke-LocalDocker $docker @('info','--format','{{.OSType}}')) -ne 'linux') { throw 'Select Docker Linux containers.' }
$lock = $null
try {
    try { $lock = [IO.File]::Open("$root/update.lock",[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) } catch { throw 'Another installation operation holds update.lock.' }
    $context = @{Root=$root;Docker=$docker;ComposeFile="$root/compose.json"}
    Assert-UpdateInstallation $context
    $repository = Split-Path -Parent $PSScriptRoot
    $context.Revision = Resolve-LocalRevision $repository $SourceRef
    & git -C $repository merge-base --is-ancestor $context.PreviousRevision $context.Revision
    if ($LASTEXITCODE -ne 0) { throw 'Select the installed revision or a descendant release; downgrades and divergent history require a reviewed transition.' }
    $release = "$root/updates/$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $release | Out-Null
    Protect-LocalDirectory $release
    $context.Release=$release; $context.Checkpoint="$release/checkpoint"; $context.CandidateFile="$release/candidate.json"
    Write-Host 'Building the selected committed release before downtime...'
    $context.CandidateImage = Build-LocalImage $docker $repository $context.Revision $release
    Copy-Item -LiteralPath $context.ComposeFile -Destination "$release/previous-compose.json"
    if (Test-Path "$root/update.json") { Copy-Item -LiteralPath "$root/update.json" -Destination "$release/previous-update.json" }
    $context.Config.services.app.image=$context.CandidateImage
    $context.Config.services.worker.image=$context.CandidateImage
    Write-UpdateJson $context.Config $context.CandidateFile
    Invoke-LocalDocker $docker @('compose','--project-name',$context.Project,'--file',$context.CandidateFile,'config','--quiet') | Out-Null
    # Revalidate running ownership after the potentially long image build.
    Assert-UpdateRunning $context
    Assert-UpdateConfigurationCurrent $context
    Write-Host 'Stopping services, checkpointing retained data, and applying the release...'
    Invoke-LocalUpdate $context
    Write-Host "Update succeeded: $($context.Origin). Checkpoint: $($context.Checkpoint)."
    Write-Host 'Verify sign-in, existing data, and sign-out in your browser. Retain the checkpoint privately.'
} finally { if ($lock) { $lock.Dispose() } }
