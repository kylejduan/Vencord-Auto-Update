function Assert-WtsReceipt([string]$Receipt) {
    if($Receipt -match 'WTS_USER_LAUNCH_FAILED'){throw 'Actual WTS launch failed or did not drain.'}
    if($Receipt -notmatch '(?m)^WTS_PROBE_COMPLETE\r?$' -or $Receipt -notmatch '(?m)^WTS_ELIGIBLE=(\d+)\r?$'){throw 'WTS eligibility/completion evidence is missing.'}
    $eligible=[int]$Matches[1]
    $outcomes=@([regex]::Matches($Receipt,'(?m)^WTS_ORDINARY_WORKER_EXIT=.*$'))
    foreach($outcome in $outcomes){if($outcome.Value -notmatch '^WTS_ORDINARY_WORKER_EXIT=0 SESSION=\d+\r?$'){throw "Eligible clean WTS worker did not return Done0: $($outcome.Value)"}}
    $unverified=([regex]::Matches($Receipt,'(?m)^WTS_USER_LAUNCH_UNVERIFIED: .+\r?$')).Count
    if(($eligible -eq 0 -and ($outcomes.Count -ne 0 -or $unverified -ne 1)) -or ($eligible -gt 0 -and ($outcomes.Count+$unverified) -ne $eligible)){throw 'WTS outcomes do not cover every eligible session.'}
}
