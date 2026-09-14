[CmdletBinding()]
param([string]$FixtureRoot,[string]$Destination,[string]$ServiceName,[string]$ShortcutPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'setup-common.ps1')
Assert-SetupAdministrator
$paths=Resolve-SetupPaths $PSBoundParameters
$lock=Enter-SetupLock $paths
try {
    Assert-SetupDirectory $paths
    Restore-SetupRecovery $paths
    if (-not (New-SetupRecovery $paths $null 'uninstall')) { Write-Output 'No owned machine installation remains.'; return }
    # On any failure leave the journal and verified snapshots for the next invocation.
    Stop-SetupService $paths
    Wait-SetupExecutables $paths
    $r=Read-SetupRecovery $paths
    Remove-SetupService $paths
    if (Assert-SetupShortcut $paths $r.Record.OldShortcutHash) { Remove-Item -LiteralPath $paths.ShortcutPath }
    foreach ($file in $r.Old.Files) { $path=Join-Path $paths.Destination $file.Name; Assert-SetupHash $path @($file.Sha256); Remove-Item -LiteralPath $path }
    Assert-SetupHash $paths.Manifest @($r.Record.OldManifestHash)
    Remove-Item -LiteralPath $paths.Manifest
    Write-SetupDurable (Join-Path $paths.Recovery 'committed') (Get-SetupHash (Join-Path $paths.Recovery 'record.json'))
    Complete-SetupRecovery $paths
    if (@(Get-ChildItem -LiteralPath $paths.Destination -Force).Count -eq 0) { [IO.Directory]::Delete($paths.Destination) }
    Write-Output 'Removed only the owned machine service, shortcut and files. Every user cache/state/log/recovery file is preserved; Discord and Vencord were untouched. The protected setup lock directory remains for serialized future setup.'
} finally { Exit-SetupLock $lock }
