function Assert-NoSetupTask($Paths) {
    # There were no released Task Scheduler installations. Never run or migrate a
    # user-writable legacy helper elevated. An unexpected task is a collision.
    $names=if ($Paths.Fixture) { @($Paths.ServiceName) } else { @('VencordAutoUpdate','VencordAutoUpdate-*') }
    foreach ($name in $names) { if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { throw 'Unexpected helper scheduled task exists. Setup refuses legacy/foreign ownership; no task was changed.' } }
}
function Get-SetupDescription($Paths) { 'VencordAutoUpdate machine broker: '+$Paths.Destination }
# -AllowPartial is used only while an uncommitted recovery record proves setup was mid-operation.
# It recognizes exactly the never-started intermediate states that Scm.Create passes through
# (create, ACL, failure actions, description, then automatic start) so recovery can remove and
# recreate that service. Any other difference remains foreign or modified ownership.
# True when no principal other than SYSTEM or Administrators may reconfigure, delete or re-own the service.
function Test-SetupServiceSecurityProtected([string]$Sddl) {
    $sd=[Security.AccessControl.RawSecurityDescriptor]::new($Sddl)
    foreach ($sid in @($sd.Owner)) { if (-not ($sid.IsWellKnown('LocalSystemSid') -or $sid.IsWellKnown('BuiltinAdministratorsSid'))) { return $false } }
    if (-not $sd.DiscretionaryAcl) { return $false }
    foreach ($ace in $sd.DiscretionaryAcl) {
        if ($ace -isnot [Security.AccessControl.CommonAce]) { return $false }
        if ($ace.AceQualifier -ne 'AccessAllowed') { continue }
        if ($ace.SecurityIdentifier.IsWellKnown('LocalSystemSid') -or $ace.SecurityIdentifier.IsWellKnown('BuiltinAdministratorsSid')) { continue }
        # SERVICE_CHANGE_CONFIG, DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE.
        if ($ace.AccessMask -band 0x500D0002) { return $false }
    }
    return $true
}
function Assert-SetupService($Paths, [switch]$AllowPartial) {
    $s=[VencordSetup.Scm]::Read($Paths.ServiceName)
    if (-not $s) { return $null }
    if ($s.Type -ne 16 -or $s.Start -notin @(2,3) -or $s.Error -ne 1 -or $s.Image -cne ('"'+$Paths.ServiceExecutable+'"') -or $s.Account -ne 'LocalSystem' -or $s.Display -cne $Paths.ServiceName -or $s.Group -ne '' -or $s.Dependencies -ne '') { throw 'Service configuration/ImagePath has foreign or modified ownership; refused.' }
    $description=$s.Description -ceq (Get-SetupDescription $Paths)
    $security=$s.Dacl -ceq [VencordSetup.Scm]::CanonicalSecurity([VencordSetup.Scm]::Security)
    $failure=-not ($s.Reset -ne 86400 -or ($s.Actions -join ',') -ne '1,1,1,0' -or ($s.Delays -join ',') -ne '60000,60000,60000,0' -or $s.FailureFlag -ne 0 -or $s.Delayed -ne 0 -or $s.FailureCommand -ne '' -or $s.RebootMessage -ne '')
    if ($s.Start -eq 2 -and $description -and $security -and $failure) { return $s }
    if ($AllowPartial -and $s.Start -eq 3 -and $s.State -eq 1 -and $s.Exit -eq 1077 -and $s.SpecificExit -eq 0) {
        $noFailure=$s.Reset -eq 0 -and ($s.Actions -join ',') -eq '' -and $s.FailureFlag -eq 0 -and $s.Delayed -eq 0 -and $s.FailureCommand -eq '' -and $s.RebootMessage -eq ''
        $noDescription=-not $s.Description
        # Stages: created (default protected ACL), ACL set, failure actions set, description set.
        $stage=($noFailure -and $noDescription -and ($security -or (Test-SetupServiceSecurityProtected $s.Dacl))) -or ($security -and $failure -and $noDescription) -or ($security -and $failure -and $description)
        if ($stage) { return ($s | Add-Member -NotePropertyName Partial -NotePropertyValue $true -PassThru) }
    }
    if ($s.Start -ne 2 -or -not $description) { throw 'Service configuration/ImagePath has foreign or modified ownership; refused.' }
    if (-not $security) { throw 'Service security descriptor changed; refused.' }
    throw 'Service crash restart configuration changed; refused.'
}
function Wait-SetupServiceRemoved($Paths) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        if (-not [VencordSetup.Scm]::Read($Paths.ServiceName)) { return }
        if ($timer.Elapsed.TotalSeconds -gt 15) { throw 'Service unregister pending; files retained. Close service consoles and retry.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
}
function Remove-SetupPartialService($Paths) {
    $s=Assert-SetupService $Paths -AllowPartial
    if (-not $s -or -not $s.Partial) { return }
    [VencordSetup.Scm]::Delete($Paths.ServiceName)
    Wait-SetupServiceRemoved $Paths
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
            # 1077 is ERROR_SERVICE_NEVER_STARTED: no service process ever ran, so nothing can be draining.
            if ($s.Exit -notin @(0,1077) -or $s.SpecificExit -ne 0) { throw 'Service reports failed/incomplete orderly shutdown. Binary replacement prohibited; retain recovery evidence.' }
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
    if ($s.State -ne 1 -or $s.Exit -notin @(0,1077) -or $s.SpecificExit -ne 0) { throw 'Only a verified successfully drained or never-started service may be removed.' }
    [VencordSetup.Scm]::Delete($Paths.ServiceName)
    Wait-SetupServiceRemoved $Paths
}
