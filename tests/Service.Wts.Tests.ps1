$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Service.Wts.Assertions.ps1')
foreach($code in @(21,22,20,65,1,-1,256)) {
    $failed=$false
    try { Assert-WtsReceipt "WTS_ELIGIBLE=1`nWTS_ORDINARY_WORKER_EXIT=$code SESSION=1`nWTS_PROBE_COMPLETE" } catch {$failed=$true}
    if(-not $failed){throw "Eligible clean WTS outcome $code was incorrectly accepted"}
}
Assert-WtsReceipt "WTS_ELIGIBLE=1`nWTS_ORDINARY_WORKER_EXIT=0 SESSION=1`nWTS_PROBE_COMPLETE"
Assert-WtsReceipt "WTS_ELIGIBLE=0`nWTS_USER_LAUNCH_UNVERIFIED: no session`nWTS_PROBE_COMPLETE"
Assert-WtsReceipt "WTS_ELIGIBLE=1`nWTS_USER_LAUNCH_UNVERIFIED: existing roots`nWTS_PROBE_COMPLETE"
foreach($receipt in @('WTS_PROBE_COMPLETE',"WTS_ELIGIBLE=1`nWTS_PROBE_COMPLETE",'WTS_USER_LAUNCH_FAILED: undrained')) {
    $failed=$false;try {Assert-WtsReceipt $receipt} catch {$failed=$true}
    if(-not $failed){throw 'Missing or failed WTS evidence was accepted'}
}
Write-Output 'PASS WTS receipt gate requires Done0 for clean eligible launch; preserves explicit unverified guards; rejects missing/failed evidence'
