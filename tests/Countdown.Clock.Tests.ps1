$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/VencordAutoUpdate.exe')))
Add-Type -AssemblyName System.Windows.Forms
$type=$assembly.GetType('VencordAutoUpdate.RestartDialog',$true)
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
$script:accepted=$null
$check=[Func[bool]]{return $true}
$done=[Action[bool]]{param($value) $script:accepted=$value}
$dialog=[Activator]::CreateInstance($type,$flags,$null,[object[]]@('Fixture',$check,$done),$null)
try {
    # Creation time must not consume the user's visible cancellation opportunity.
    Start-Sleep -Seconds 21
    $dialog.Show(); [Windows.Forms.Application]::DoEvents()
    if ($script:accepted -eq $true) { throw 'Countdown accepted before any visible elapsed cancellation opportunity.' }
    $dialog.Close(); [Windows.Forms.Application]::DoEvents()
    if ($script:accepted -ne $false) { throw 'Closing countdown must cancel.' }
    Write-Output 'PASS countdown starts when shown; creation delay cannot consume consent window; close cancels'
} finally { $dialog.Dispose() }
# Use the real supervisor's validity callback with its injected UTC host, plus
# the dialog's injected elapsed boundary. No actual host clock is modified.
$core=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/CoreTests.exe')))
$static=[Reflection.BindingFlags]'NonPublic,Static'
$fixtureType=$core.GetType('VencordAutoUpdate.Fixtures',$true)
$hostType=$core.GetType('VencordAutoUpdate.FakeHost',$true)
$supervisorTests=$core.GetType('VencordAutoUpdate.SupervisorTests',$true)
foreach($scenario in @('forward UTC correction','backward UTC correction','invalidation at boundary','cancel at boundary')) {
    $fixture=[Activator]::CreateInstance($fixtureType,$true);$dialog=$null
    try {
        $hostBoundary=[Activator]::CreateInstance($hostType,$true)
        $supervisorTests.GetMethod('Seed',$static).Invoke($null,@($fixture))
        $fixtureRoot=[string]$fixtureType.GetField('Root',$flags).GetValue($fixture)
        $hostType.GetMethod('Running',$flags).Invoke($hostBoundary,[object[]]@([string]$fixtureRoot,[int]7,[double]1))
        $supervisor=$supervisorTests.GetMethod('Make',$static).Invoke($null,@($fixture,$hostBoundary))
        $tick=$supervisor.GetType().GetMethod('Tick',$flags);$tick.Invoke($supervisor,@())
        $utc=$hostType.GetField('Time',$flags);$utc.SetValue($hostBoundary,([DateTime]$utc.GetValue($hostBoundary)).AddSeconds(10));$tick.Invoke($supervisor,@())
        $script:cohortValid=[Func[bool]]$hostType.GetField('Valid',$flags).GetValue($hostBoundary)
        if(-not $script:cohortValid.Invoke()){throw 'Actual supervisor fixture is not eligible'}
        $script:elapsed=[TimeSpan]::FromSeconds(100);$script:valid=$true;$script:accepted=$null
        $clock=[Func[TimeSpan]]{return $script:elapsed};$check=[Func[bool]]{return $script:valid -and $script:cohortValid.Invoke()}
        $dialog=[Activator]::CreateInstance($type,$flags,$null,[object[]]@('Fixture',$check,$done,$clock),$null)
        $dialog.Show();[Windows.Forms.Application]::DoEvents()
        $pulse=$type.GetMethod('UpdateCountdown',$flags)
        $script:elapsed=[TimeSpan]::FromSeconds(102)
        if($scenario -eq 'forward UTC correction'){$utc.SetValue($hostBoundary,([DateTime]$utc.GetValue($hostBoundary)).AddSeconds(30))}
        if($scenario -eq 'backward UTC correction'){$utc.SetValue($hostBoundary,([DateTime]$utc.GetValue($hostBoundary)).AddSeconds(-30))}
        if(-not $script:cohortValid.Invoke()){throw 'UTC correction unexpectedly changed fixture eligibility'}
        $pulse.Invoke($dialog,@('Fixture'))
        if($script:accepted -eq $true){throw 'Accepted at two elapsed seconds'}
        $script:elapsed=[TimeSpan]::FromMilliseconds(119999)
        $pulse.Invoke($dialog,@('Fixture'))
        if($script:accepted -eq $true){throw 'Accepted before full twenty elapsed seconds'}
        $script:elapsed=[TimeSpan]::FromSeconds(120)
        if($scenario -eq 'invalidation at boundary'){$script:valid=$false}
        if($scenario -eq 'cancel at boundary'){$dialog.Close()}else{$pulse.Invoke($dialog,@('Fixture'))}
        [Windows.Forms.Application]::DoEvents()
        $want=$scenario -notin @('invalidation at boundary','cancel at boundary')
        if($script:accepted -ne $want){throw "Wrong countdown outcome for $scenario"}
        Write-Output "PASS elapsed countdown $scenario; accepts only at 20 seconds; cancel/invalidation wins"
    } finally {if($dialog){$dialog.Dispose()};$fixture.Dispose()}
}
