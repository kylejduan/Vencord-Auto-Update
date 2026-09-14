# Test-only gate and ownership helpers. Never distributed or sourced by setup.
$ErrorActionPreference='Stop'
$script:Repo=Split-Path -Parent $PSScriptRoot
. (Join-Path $script:Repo 'scripts/setup-common.ps1')
function Test-ScmPermission([bool]$RequireAdmin) {
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        if ($RequireAdmin) { throw 'REQUIRED SCM COVERAGE FAILED: runner is not administrator; skipping is forbidden.' }
        Write-Output 'SKIP actual SCM integration: ordinary local account. This is not passing SCM/WTS coverage.'
        return $false
    }
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') { throw 'Actual SCM fixtures are restricted to isolated administrator GitHub Windows CI. Do not elevate locally.' }
    return $true
}
function New-MachineFixture {
    $native=if ($env:ProgramW6432) { $env:ProgramW6432 } else { [Environment]::GetFolderPath('ProgramFiles') }
    $id=[Guid]::NewGuid().ToString('N'); $root=Join-Path $native "VencordAutoUpdate.Tests\$id"
    $args=@{FixtureRoot=$root; Destination=(Join-Path $root 'Application With Spaces'); ServiceName="VencordAutoUpdate-Test-$id"; ShortcutPath=(Join-Path $root 'Status.lnk')}
    @{ Args=$args; Paths=(Resolve-SetupPaths $args); Root=$root }
}
function Check($Condition,[string]$Message) { if (-not $Condition) { throw "FAIL $Message" }; Write-Output "PASS $Message" }
function Refuses([scriptblock]$Action,[string]$Message) { $refused=$false; try { & $Action | Out-Null } catch { $refused=$true; Write-Output ("Expected refusal: "+$_.Exception.Message) }; Check $refused $Message }
function Install-Fixture($Fixture) { $args=$Fixture.Args; & (Join-Path $script:Repo 'scripts/install.ps1') @args }
function Uninstall-Fixture($Fixture) { $args=$Fixture.Args; & (Join-Path $script:Repo 'scripts/uninstall.ps1') @args }
function Remove-MachineFixture($Fixture) {
    Uninstall-Fixture $Fixture
    Check (-not [VencordSetup.Scm]::Read($Fixture.Paths.ServiceName)) 'actual SCM unregister readback'
    Check (-not (Test-Path -LiteralPath $Fixture.Paths.ShortcutPath)) 'fixture shortcut absent'
    Check (-not (Get-ScheduledTask -TaskName $Fixture.Paths.ServiceName -ErrorAction SilentlyContinue)) 'fixture task absent'
    # Only this test's freshly generated root is in cleanup authority. Never a default root.
    Check ($Fixture.Root -cmatch '\\VencordAutoUpdate.Tests\\[0-9a-f]{32}$') 'cleanup root is GUID scoped'
    $running=@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Fixture.Root+'\',[StringComparison]::OrdinalIgnoreCase) })
    Check ($running.Count -eq 0) 'no fixture executable remains running'
    if (Test-Path -LiteralPath $Fixture.Root) { Remove-Item -LiteralPath $Fixture.Root -Recurse -Force }
    Check (-not (Test-Path -LiteralPath $Fixture.Root)) 'owned fixture files cleaned'
}
