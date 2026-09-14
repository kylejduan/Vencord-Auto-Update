[CmdletBinding()]
param([string]$SourceDirectory,[string]$FixtureRoot,[string]$Destination,[string]$ServiceName,[string]$ShortcutPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'setup-common.ps1')
Assert-SetupAdministrator
$paths=Resolve-SetupPaths $PSBoundParameters
$lock=Enter-SetupLock $paths
try {
    Assert-SetupDirectory $paths
    [VencordSetup.Paths]::Protected((Split-Path -Parent $paths.ShortcutPath))
    Restore-SetupRecovery $paths
    $sources=Get-SetupSources $SourceDirectory
    [void](New-SetupRecovery $paths $sources 'install')
    try {
        Stop-SetupService $paths
        Wait-SetupExecutables $paths
        $r=Read-SetupRecovery $paths
        foreach ($file in $r.New.Files) {
            $oldHash=if ($r.Old) { ($r.Old.Files | Where-Object Name -CEQ $file.Name).Sha256 }
            Set-SetupFile (Join-Path $paths.Recovery "new\$($file.Name)") (Join-Path $paths.Destination $file.Name) $paths.Recovery @($oldHash,$file.Sha256)
        }
        Set-SetupFile (Join-Path $paths.Recovery 'new\installation.json') $paths.Manifest $paths.Recovery @($r.Record.OldManifestHash,$r.Record.NewManifestHash)
        Set-SetupFile (Join-Path $paths.Recovery 'new.lnk') $paths.ShortcutPath $paths.Recovery @($r.Record.OldShortcutHash,$r.Record.NewShortcutHash)
        [void](Read-SetupManifest $paths); [void](Assert-SetupShortcut $paths)
        Assert-SetupDirectory $paths
        if (-not (Assert-SetupService $paths)) { [VencordSetup.Scm]::Create($paths.ServiceName,('"'+$paths.ServiceExecutable+'"'),(Get-SetupDescription $paths)) }
        Start-SetupService $paths
        Write-SetupDurable (Join-Path $paths.Recovery 'committed') (Get-SetupHash (Join-Path $paths.Recovery 'record.json'))
        Complete-SetupRecovery $paths
    } catch {
        $failure=$_
        try { Restore-SetupRecovery $paths } catch { throw "Setup failed: $failure Recovery retained at $($paths.Recovery): $_" }
        throw $failure
    }
    Write-Output 'Installed and started the protected Windows service. Open the Start menu shortcut for Status / Setup. Closing Status exits the UI; service protection continues.'
} finally { Exit-SetupLock $lock }
