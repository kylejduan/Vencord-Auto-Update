param([switch]$RequireAdmin)
. (Join-Path $PSScriptRoot 'Setup.Fixture.ps1')
# Capture the boolean separately from the explicit skip message.
$permission=@(Test-ScmPermission $RequireAdmin)
$permission | Where-Object { $_ -is [string] } | Write-Output
if ($permission[-1] -ne $true) { return }
$f=New-MachineFixture
try {
    Install-Fixture $f
    $s=Assert-SetupService $f.Paths
    Check ($s.State -eq 4 -and $s.Pid -gt 0) 'production service EXE actually starts under isolated SCM name'
    Check ($s.Image -ceq ('"'+$f.Paths.ServiceExecutable+'"')) 'effective ImagePath quoted with spaces and no arguments'
    $guidance=$false; try { Wait-SetupExecutables $f.Paths } catch { $guidance=$_.Exception.Message -match 'Close Status' }
    Check $guidance 'real mapped service EXE refusal includes close-Status guidance'
    Check (($s.Actions -join ',') -eq '1,1,1,0') 'SCM crash restart terminates with NONE, not repeated RESTART'
    foreach ($name in @('VencordAutoUpdate.exe','VencordAutoUpdate.Service.exe')) {
        $path=Join-Path $f.Paths.Destination $name
        [VencordSetup.Paths]::Protected($path)
        Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion -eq '0.1.0.0') "$name version and effective protected ACL"
    }
    $process=[Diagnostics.Process]::GetProcessById([int]$s.Pid)
    try { $cpu=$process.TotalProcessorTime; Start-Sleep -Seconds 2; $process.Refresh(); Write-Output ("OBSERVATION actual production SCM 2s CPU delta={0}ms workingSet={1} session={2}" -f ($process.TotalProcessorTime-$cpu).TotalMilliseconds,$process.WorkingSet64,$process.SessionId) }
    finally { $process.Dispose() }
    $neighbor=Join-Path $f.Paths.Destination 'foreign-neighbor.txt'
    Write-SetupDurable $neighbor 'fixture owns this simulated foreign neighbor'
    Install-Fixture $f
    Check ((Get-Content -LiteralPath $neighbor -Raw) -ceq 'fixture owns this simulated foreign neighbor') 'repeat install preserves foreign neighbor'
    Stop-SetupService $f.Paths
    $s=Assert-SetupService $f.Paths
    Check ($s.State -eq 1 -and $s.Exit -eq 0 -and $s.SpecificExit -eq 0) 'actual SCM orderly stop and exit readback'
    Start-SetupService $f.Paths
    Uninstall-Fixture $f
    Check ((Get-Content -LiteralPath $neighbor -Raw) -ceq 'fixture owns this simulated foreign neighbor') 'uninstall preserves foreign neighbor'
    Uninstall-Fixture $f
    Check (-not [VencordSetup.Scm]::Read($f.Paths.ServiceName)) 'repeat uninstall is idempotent'
} catch {
    # Cleanup can also refuse damaged recovery evidence. Preserve the primary
    # failure and its call stack before finally attempts that verified cleanup.
    Write-Output ("PRIMARY SCM lifecycle failure: "+$_.Exception.Message)
    Write-Output $_.ScriptStackTrace
    try {
        # Read only this CI fixture's service. Do not normalize or repair a
        # rejected descriptor: retain exact owner/group/DACL evidence first.
        Write-Output ("EXPECTED SCM security: "+[VencordSetup.Scm]::CanonicalSecurity([VencordSetup.Scm]::Security))
        $snapshot=[VencordSetup.Scm]::Read($f.Paths.ServiceName)
        if ($snapshot) { Write-Output ("ACTUAL SCM security: "+$snapshot.Dacl) }
        else { Write-Output 'ACTUAL SCM security: service absent' }
    } catch { Write-Output ("SCM security diagnostic read failed: "+$_.Exception.Message) }
    throw
} finally { Remove-MachineFixture $f }
