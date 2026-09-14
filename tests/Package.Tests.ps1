param([switch]$RequireAdmin)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Setup.Fixture.ps1')
$fixture=Join-Path $env:LOCALAPPDATA ("VencordAutoUpdate package test's "+[Guid]::NewGuid().ToString('N'))
$unpack=Join-Path $fixture 'artifacts'
$machine=$null
try {
    & (Join-Path $repo 'scripts/package.ps1') -OutputDirectory $fixture
    $zip=Join-Path $fixture 'VencordAutoUpdate-0.1.0-windows.zip'
    $checksum=(Get-Content -LiteralPath (Join-Path $fixture 'SHA256SUMS') -Raw).Trim()
    Check ($checksum -ceq ((Get-SetupHash $zip).ToLowerInvariant()+'  VencordAutoUpdate-0.1.0-windows.zip')) 'ZIP checksum matches actual bytes'
    Expand-Archive -LiteralPath $zip -DestinationPath $unpack
    $expected=@(Get-OwnedFileNames)+'SOURCE.txt'
    $actual=@(Get-ChildItem -LiteralPath $unpack -Recurse -File | ForEach-Object { $_.FullName.Substring($unpack.Length+1) })
    Check (-not (Compare-Object ($expected | Sort-Object) ($actual | Sort-Object))) 'ZIP has exact two EXEs/setup modules/docs/provenance inventory and no test or user data'
    foreach ($name in @('VencordAutoUpdate.exe','VencordAutoUpdate.Service.exe')) { Check ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $unpack $name)).FileVersion -eq '0.1.0.0') "ZIP $name version" }
    $source=Get-Content (Join-Path $unpack 'SOURCE.txt') -Raw
    Check ($source -match '^Version: 0\.1\.0\r?\nCommit: [0-9a-f]{40}\r?\nDirty: (true|false)\r?\n$') 'SOURCE exact version/commit/dirty provenance without machine paths'
    # Read-only packaged CLI is permitted elevated and never opens a setup UI.
    $data=Join-Path $fixture 'data'; $discord=Join-Path $fixture 'Discord'
    [IO.Directory]::CreateDirectory($discord) | Out-Null
    $p=Start-Process -FilePath (Join-Path $unpack 'VencordAutoUpdate.exe') -ArgumentList ('--status --data-dir "'+$data+'" --discord-root "'+$discord+'"') -PassThru
    try { if (-not $p.WaitForExit(15000) -or $p.ExitCode -ne 0) { throw 'Extracted read-only status failed.' } } finally { $p.Dispose() }
    Check (-not (Test-Path -LiteralPath $data)) 'extracted read-only status creates no runtime data'
    if ($RequireAdmin) {
        $permission=@(Test-ScmPermission $true)
        if ($permission[-1] -ne $true) { throw 'Required package SCM acceptance did not run.' }
        $machine=New-MachineFixture; $args=$machine.Args
        & (Join-Path $unpack 'install.ps1') -SourceDirectory $unpack @args
        Check ((Assert-SetupService $machine.Paths).State -eq 4) 'extracted ZIP installs/starts protected production service with quoted path'
        & (Join-Path $unpack 'uninstall.ps1') @args
        Check (-not [VencordSetup.Scm]::Read($machine.Paths.ServiceName)) 'extracted ZIP unregister readback'
    } else { Write-Output 'SKIP package SCM install: layout/status verified only. Administrator CI requires -RequireAdmin.' }
} finally {
    if ($machine) { Remove-MachineFixture $machine }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
Check (-not (Test-Path -LiteralPath $fixture)) 'package fixture cleaned'
