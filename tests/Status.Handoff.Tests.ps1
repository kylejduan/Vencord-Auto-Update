$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/CoreTests.exe')))
$flags=[Reflection.BindingFlags]'NonPublic,Instance';$static=[Reflection.BindingFlags]'NonPublic,Static'
if(-not $assembly.GetType('VencordAutoUpdate.WorkerIdentity').GetProperty('Ordinary',$static).GetValue($null,$null)){Write-Output 'SKIP ordinary-token Status release handoff';exit 0}
$type=$assembly.GetType('VencordAutoUpdate.Fixtures',$true);$instanceType=$assembly.GetType('VencordAutoUpdate.InstanceControl',$true)
foreach($mode in @('release','stop')) {
$fixture=[Activator]::CreateInstance($type,$true);$data=[string]$type.GetField('Cache',$flags).GetValue($fixture);$root=[string]$type.GetField('Root',$flags).GetValue($fixture)
$exe=Join-Path $repo 'artifacts/VencordAutoUpdate.exe';$held=$null;$ui=$null
try {
    # Exact real kernel instance is held by a finishing host that ignores Show.
    $held=[Activator]::CreateInstance($instanceType,$flags,$null,[object[]]@($data),$null)
    $show=$instanceType.GetProperty('ShowSignal',$flags).GetValue($held,$null)
    $info=[Diagnostics.ProcessStartInfo]::new($exe,'--data-dir "'+$data+'" --discord-root "'+$root+'"');$info.UseShellExecute=$false
    $ui=[Diagnostics.Process]::Start($info)
    if(-not $show.WaitOne(5000)){throw 'Owned Status request did not signal the held primary'}
    if($ui.WaitForExit(300)){throw 'Status falsely acknowledged success when the exiting primary ignored Show'}
    if($mode -eq 'stop') {
        $shutdown=$instanceType.GetProperty('StopSignal',$flags).GetValue($held,$null);[void]$shutdown.Set()
        if(-not $ui.WaitForExit(5000) -or $ui.ExitCode -ne 0){throw 'Orderly stop did not cancel pending Status handoff'}
        Write-Output 'PASS observed orderly stop cancels pending Status without opening a successor'
        continue
    }
    $held.Dispose();$held=$null
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {$ui.Refresh();if($ui.MainWindowHandle -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 100}while(-not $ui.HasExited -and $timer.Elapsed.TotalSeconds -lt 5)
    if($ui.HasExited -or $ui.MainWindowHandle -eq [IntPtr]::Zero){throw 'Waiting Status did not take the released instance and open a real UI'}
    if(-not $ui.CloseMainWindow() -or -not $ui.WaitForExit(5000) -or $ui.ExitCode -ne 0){throw 'Successor Status did not close normally'}
    Write-Output 'PASS ignored Show never reports false success; Status takes released exact instance and opens/closes real UI'
} finally {
    if($held){$held.Dispose()}
    $info=[Diagnostics.ProcessStartInfo]::new($exe,'--stop --data-dir "'+$data+'"');$info.UseShellExecute=$false
    $stop=[Diagnostics.Process]::Start($info)
    if(-not $stop.WaitForExit(45000) -or $stop.ExitCode -ne 0){throw 'Owned handoff stop failed; retain fixture'};$stop.Dispose()
    if($ui){if(-not $ui.WaitForExit(5000)){throw 'Owned Status remains; retain fixture'};$ui.Dispose()}
    $fixture.Dispose()
}
}
Write-Output 'PASS Status handoff fixtures drained and cleaned'
