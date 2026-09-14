param([switch]$RequireAdmin)
. (Join-Path $PSScriptRoot 'Setup.Fixture.ps1')
$permission=@(Test-ScmPermission $RequireAdmin); $permission | Where-Object { $_ -is [string] } | Write-Output
if ($permission[-1] -ne $true) { return }
$f=New-MachineFixture
try {
    Install-Fixture $f
    $oldHash=Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')
    $sources=Get-SetupSources (Join-Path $script:Repo 'artifacts')
    $newReadme=Join-Path $f.Root 'updated-readme.txt'; Write-SetupDurable $newReadme 'test-only next release README'
    $sources['README.md']=$newReadme
    $lock=Enter-SetupLock $f.Paths
    try {
        [void](New-SetupRecovery $f.Paths $sources 'install')
        Stop-SetupService $f.Paths; Wait-SetupExecutables $f.Paths
        $r=Read-SetupRecovery $f.Paths
        Set-SetupFile (Join-Path $f.Paths.Recovery 'new\README.md') (Join-Path $f.Paths.Destination 'README.md') $f.Paths.Recovery @($oldHash,(Get-SetupHash $newReadme))
        Check ((Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')) -ne $oldHash) 'interrupted upgrade actually contains mixed old/new files'
        Restore-SetupRecovery $f.Paths
        Check ((Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')) -eq $oldHash) 'rollback restores exact previous bytes'
        Check ((Assert-SetupService $f.Paths).State -eq 4) 'rollback restores prior running state'
        Check (-not (Test-Path -LiteralPath $f.Paths.Recovery)) 'recovery retired only after verified rollback'
        [void](New-SetupRecovery $f.Paths $sources 'install')
        $target=Join-Path $f.Paths.Destination 'README.md'
        $bytes=[IO.File]::ReadAllBytes($target)
        try { [IO.File]::WriteAllText($target,'unknown concurrent bytes'); Refuses { Restore-SetupRecovery $f.Paths } 'unknown active content blocks rollback without overwrite'; Check ((Get-Content $target -Raw) -ceq 'unknown concurrent bytes') 'ambiguous evidence remains' }
        finally { [IO.File]::WriteAllBytes($target,$bytes) }
        # Simulate a crash during transfer copy: never authoritative, retained inert.
        $partial=Join-Path $f.Paths.Recovery ('transfer-'+[Guid]::NewGuid().ToString('N'))
        Write-SetupDurable $partial 'partial copy'
        Restore-SetupRecovery $f.Paths
        $retained=@(Get-ChildItem -LiteralPath $f.Paths.ControlRoot -Directory -Filter 'completed-*')
        Check ($retained.Count -eq 1) 'incomplete transfer cleanup retains explicit completed evidence'
        Check (@(Get-ChildItem $retained[0].FullName -File).Count -eq 1) 'retained partial transfer never substituted for verified old snapshot'
        [void](New-SetupRecovery $f.Paths $null 'uninstall')
        Stop-SetupService $f.Paths; Wait-SetupExecutables $f.Paths
        Remove-SetupService $f.Paths
        Remove-Item -LiteralPath (Join-Path $f.Paths.Destination 'README.md')
        Restore-SetupRecovery $f.Paths
        Check ((Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')) -eq $oldHash) 'interrupted uninstall restores removed owned file'
        Check ((Assert-SetupService $f.Paths).State -eq 4) 'interrupted uninstall recreates exact owned service and prior state'
    } finally { Exit-SetupLock $lock }
    Uninstall-Fixture $f
} finally { Remove-MachineFixture $f }
