$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-native.ps1')
function Assert-SetupAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Machine setup requires administrator elevation (UAC). Open the extracted release Install action or an administrator PowerShell.' } }
    finally { $identity.Dispose() }
}
function Get-OwnedFileNames {
    @('VencordAutoUpdate.exe','VencordAutoUpdate.Service.exe','install.ps1','uninstall.ps1','setup-common.ps1','setup-native.ps1','setup-files.ps1','setup-service.ps1','setup-recovery.ps1','README.md','LICENSE','CHANGELOG.md','CONTRIBUTING.md','SECURITY.md')
}
function Resolve-SetupPaths($Parameters) {
    foreach ($key in $Parameters.Keys) { if ($key -notin @('SourceDirectory','FixtureRoot','Destination','ServiceName','ShortcutPath')) { throw "Unknown setup authority: $key" } }
    $native = if ($env:ProgramW6432) { $env:ProgramW6432 } else { [Environment]::GetFolderPath('ProgramFiles') }
    $p = @{ Destination=(Join-Path $native 'VencordAutoUpdate'); ServiceName='VencordAutoUpdate'; ShortcutPath=(Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Vencord Auto Update.lnk'); ControlRoot=(Join-Path $native 'VencordAutoUpdate.Setup'); Fixture=$false }
    $keys = @('FixtureRoot','Destination','ServiceName','ShortcutPath')
    if (@($keys | Where-Object { $Parameters.ContainsKey($_) }).Count -gt 0) {
        foreach ($key in $keys) { if (-not $Parameters.ContainsKey($key) -or -not $Parameters[$key]) { throw 'Fixture setup requires the complete FixtureRoot, Destination, ServiceName and ShortcutPath set.' } }
        $root = [string]$Parameters.FixtureRoot
        $id = Split-Path -Leaf $root
        if ($id -cnotmatch '^[0-9a-f]{32}$' -or $root -cne (Join-Path $native "VencordAutoUpdate.Tests\$id")) { throw 'FixtureRoot must be the exact native Program Files project test root plus a lowercase GUID.' }
        $expected = @{ Destination=(Join-Path $root 'Application With Spaces'); ServiceName="VencordAutoUpdate-Test-$id"; ShortcutPath=(Join-Path $root 'Status.lnk') }
        foreach ($key in $expected.Keys) { if ($Parameters[$key] -cne $expected[$key]) { throw "Fixture $key escapes its exact scope." }; $p[$key]=$expected[$key] }
        $p.Fixture=$true; $p.FixtureRoot=$root; $p.ControlRoot=(Join-Path $root 'Setup')
    }
    $p.Executable=Join-Path $p.Destination 'VencordAutoUpdate.exe'
    $p.ServiceExecutable=Join-Path $p.Destination 'VencordAutoUpdate.Service.exe'
    $p.Manifest=Join-Path $p.Destination 'installation.json'
    $p.Recovery=Join-Path $p.ControlRoot 'recovery'
    $p.Lock=Join-Path $p.ControlRoot 'setup.lock'
    return $p
}
function Enter-SetupLock($Paths) {
    Assert-SetupAdministrator
    if ($Paths.Fixture) {
        [VencordSetup.Paths]::CreateDirectory((Split-Path -Parent $Paths.FixtureRoot))
        [VencordSetup.Paths]::CreateDirectory($Paths.FixtureRoot)
    }
    [VencordSetup.Paths]::CreateDirectory($Paths.ControlRoot)
    [VencordSetup.Paths]::Protected($Paths.Lock)
    # One protected file, independent of administrator SID. No named-mutex squatting.
    try {
        if (Test-Path -LiteralPath $Paths.Lock) { return [IO.File]::Open($Paths.Lock,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        return [VencordSetup.Paths]::CreateFile($Paths.Lock)
    }
    catch [IO.IOException] { throw 'Another machine setup is running, or the protected setup lock is unavailable. Retry after it finishes.' }
}
function Exit-SetupLock($Lock) { if ($Lock) { $Lock.Dispose() } }
. (Join-Path $PSScriptRoot 'setup-files.ps1')
. (Join-Path $PSScriptRoot 'setup-service.ps1')
. (Join-Path $PSScriptRoot 'setup-recovery.ps1')
