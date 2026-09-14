function Assert-NoSetupTask($Paths) {
    # There were no released Task Scheduler installations. Never run or migrate a
    # user-writable legacy helper elevated. An unexpected task is a collision.
    $names=if ($Paths.Fixture) { @($Paths.ServiceName) } else { @('VencordAutoUpdate','VencordAutoUpdate-*') }
    foreach ($name in $names) { if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { throw 'Unexpected helper scheduled task exists. Setup refuses legacy/foreign ownership; no task was changed.' } }
}
function Get-SetupDescription($Paths) { 'VencordAutoUpdate machine broker: '+$Paths.Destination }
function Assert-SetupService($Paths) {
    $s=[VencordSetup.Scm]::Read($Paths.ServiceName)
    if (-not $s) { return $null }
    if ($s.Type -ne 16 -or $s.Start -ne 2 -or $s.Error -ne 1 -or $s.Image -cne ('"'+$Paths.ServiceExecutable+'"') -or $s.Account -ne 'LocalSystem' -or $s.Display -cne $Paths.ServiceName -or $s.Group -ne '' -or $s.Dependencies -ne '' -or $s.Description -cne (Get-SetupDescription $Paths)) { throw 'Service configuration/ImagePath has foreign or modified ownership; refused.' }
    if ($s.Dacl -cne [VencordSetup.Scm]::CanonicalSecurity([VencordSetup.Scm]::Security)) { throw 'Service security descriptor changed; refused.' }
    if ($s.Reset -ne 86400 -or ($s.Actions -join ',') -ne '1,1,1,0' -or ($s.Delays -join ',') -ne '60000,60000,60000,0' -or $s.FailureFlag -ne 0 -or $s.Delayed -ne 0 -or $s.FailureCommand -ne '' -or $s.RebootMessage -ne '') { throw 'Service crash restart configuration changed; refused.' }
    return $s
}
function Stop-SetupService($Paths) {
    $s=Assert-SetupService $Paths
    if (-not $s) { return }
    if ($s.State -eq 4) { [VencordSetup.Scm]::Stop($Paths.ServiceName) }
    elseif ($s.State -notin @(1,3)) { throw 'Service is transitioning; wait and retry setup. Files retained.' }
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $s=Assert-SetupService $Paths
        if (-not $s) { throw 'Owned service disappeared while draining. Files retained.' }
        if ($s.State -eq 1) {
            if ($s.Exit -ne 0 -or $s.SpecificExit -ne 0) { throw 'Service reports failed/incomplete orderly shutdown. Binary replacement prohibited; retain recovery evidence.' }
            return
        }
        if ($timer.Elapsed.TotalSeconds -ge 240) { throw 'Service did not drain within 240 seconds. Binary replacement prohibited.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
}
function Start-SetupService($Paths) {
    Assert-SetupDirectory $Paths
    $s=Assert-SetupService $Paths
    if (-not $s) { throw 'Owned service is missing.' }
    if ($s.State -eq 1) { [VencordSetup.Scm]::Start($Paths.ServiceName) }
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do { $s=Assert-SetupService $Paths
        if ($s.State -eq 4) { return }
        if ($s.State -eq 1 -or $timer.Elapsed.TotalSeconds -ge 30) { throw 'Service failed to reach Running; installation retained for recovery.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
}
function Remove-SetupService($Paths) {
    $s=Assert-SetupService $Paths
    if (-not $s) { return }
    if ($s.State -ne 1 -or $s.Exit -ne 0 -or $s.SpecificExit -ne 0) { throw 'Only a verified successfully drained service may be removed.' }
    [VencordSetup.Scm]::Delete($Paths.ServiceName)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        if (-not [VencordSetup.Scm]::Read($Paths.ServiceName)) { return }
        if ($timer.Elapsed.TotalSeconds -gt 15) { throw 'Service unregister pending; files retained. Close service consoles and retry.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
}
