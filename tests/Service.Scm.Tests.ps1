param([switch]$RequireAdmin)
. (Join-Path $PSScriptRoot 'Setup.Fixture.ps1')
. (Join-Path $PSScriptRoot 'Service.Wts.Assertions.ps1')
$permission=@(Test-ScmPermission $RequireAdmin); $permission | Where-Object { $_ -is [string] } | Write-Output
if ($permission[-1] -ne $true) { return }
. (Join-Path $PSScriptRoot 'Service.Pending.Assertions.ps1')
$f=New-MachineFixture
function Wait-Receipt([string]$Path,[string]$Pattern,[int]$Seconds=40) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        if ((Test-Path -LiteralPath $Path) -and (Get-Content -LiteralPath $Path -Raw) -match $Pattern) { return }
        if ($timer.Elapsed.TotalSeconds -gt $Seconds) { throw "SCM fixture receipt missing: $Pattern" }
        Start-Sleep -Milliseconds 200
    } while ($true)
}
try {
    $lock=Enter-SetupLock $f.Paths; Exit-SetupLock $lock
    $source=Join-Path $f.Root 'HarnessSource'; [VencordSetup.Paths]::CreateDirectory($source)
    $sources=Get-SetupSources (Join-Path $script:Repo 'artifacts')
    foreach ($name in Get-OwnedFileNames) { Copy-SetupDurable $sources[$name] (Join-Path $source $name) }
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $cs=@(Get-ChildItem (Join-Path $script:Repo 'src/Service') -Filter *.cs | ForEach-Object FullName)
    $cs += @(Get-ChildItem -LiteralPath (Join-Path $script:Repo 'src/Protocol') -Filter *.cs | ForEach-Object { $_.FullName })
    $cs+=Join-Path $PSScriptRoot 'Service/ScmFixtureHost.cs'
    & $compiler /nologo /warn:4 /warnaserror+ /langversion:5 /optimize+ /target:exe /main:VencordAutoUpdate.ScmFixtureHost "/out:$(Join-Path $source 'VencordAutoUpdate.Service.exe')" /r:System.dll /r:System.Core.dll /r:System.ServiceProcess.dll $cs
    if ($LASTEXITCODE -ne 0) { throw 'SCM test-only host compilation failed.' }
    $args=$f.Args
    & (Join-Path $source 'install.ps1') -SourceDirectory $source @args
    $receipt=Join-Path $f.Root 'Probe\receipt.txt'
    Wait-Receipt $receipt 'NOTIFICATION WORK'
    Wait-Receipt $receipt 'WTS_PROBE_COMPLETE' 230
    $wts=Get-Content $receipt -Raw
    Write-Output $wts
    Assert-WtsReceipt $wts
    if ($wts -match 'WTS_USER_LAUNCH_UNVERIFIED') { Write-Warning 'Actual WTS ordinary-user launch is UNVERIFIED on this runner; SCM/test-host coverage does not prove it.' }
    $count=([regex]::Matches($wts,'NOTIFICATION WORK')).Count
    [IO.File]::WriteAllText((Join-Path $f.Root 'Probe\Profile\Discord\app-change'),'owned fixture event')
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $next=([regex]::Matches((Get-Content $receipt -Raw),'NOTIFICATION WORK')).Count
        if ($next -gt $count) { break }
        if ($timer.Elapsed.TotalSeconds -gt 20) { throw 'Actual SCM-hosted UserEnrollment did not wake for filesystem event.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    Write-Output 'PASS actual SCM test host wakes real UserEnrollment on owned filesystem notification; launcher here is test-only'
    Start-Sleep -Seconds 2
    $baseline=(Get-Content $receipt -Raw)
    Start-Sleep -Seconds 2
    Check ((Get-Content $receipt -Raw) -ceq $baseline) 'SCM fixture enrollment remains idle without notification work'
    # Only the disposable test host is crashed, after its WTS work has exited or
    # was unavailable. Production broker and ordinary repair workers are never killed.
    for ($attempt=1; $attempt -le 4; $attempt++) {
        Wait-Receipt $receipt 'WTS_PROBE_COMPLETE' 230
        Assert-WtsReceipt (Get-Content $receipt -Raw)
        $s=Assert-SetupService $f.Paths
        $owned=[Diagnostics.Process]::GetProcessById([int]$s.Pid)
        try {
            Check ($owned.MainModule.FileName -eq $f.Paths.ServiceExecutable) 'crash probe targets exact fixture service image'
            $owned.Kill(); Check ($owned.WaitForExit(10000)) 'owned test-only host process exited'
        } finally { $owned.Dispose() }
        $timer=[Diagnostics.Stopwatch]::StartNew(); $restarted=$false
        do {
            $now=Assert-SetupService $f.Paths
            if ($now.State -eq 4 -and $now.Pid -ne $s.Pid) { $restarted=$true; break }
            Start-Sleep -Milliseconds 500
        } while ($timer.Elapsed.TotalSeconds -lt 75)
        if ($attempt -le 3) { Check $restarted "actual SCM crash restart $attempt" }
        else { Check (-not $restarted -and $now.State -eq 1) 'fourth crash remains stopped beyond restart delay: final NONE is effective' }
    }
    Start-SetupService $f.Paths
    Wait-Receipt $receipt 'WTS_PROBE_COMPLETE' 230
    $failure=Join-Path $f.Root 'Probe\fail-stop'; Write-SetupDurable $failure 'owned pending work injection'
    $servicePid=(Assert-SetupService $f.Paths).Pid
    $userHash=(Get-FileHash -LiteralPath $f.Paths.Executable -Algorithm SHA256).Hash
    $serviceHash=(Get-FileHash -LiteralPath $f.Paths.ServiceExecutable -Algorithm SHA256).Hash
    function Assert-PendingDrainRefusal([scriptblock]$Action,[string]$Label) {
        $timer=[Diagnostics.Stopwatch]::StartNew(); $message=$null
        try { & $Action | Out-Null } catch { $message=$_.Exception.Message }
        Check ($message -like '*Service did not drain within 240 seconds*' -and $timer.Elapsed.TotalSeconds -ge 240 -and $timer.Elapsed.TotalSeconds -lt 300) "$Label uses unchanged bounded 240-second refusal"
        $state=Assert-SetupService $f.Paths
        Check ($state.State -eq 3 -and $state.Pid -eq $servicePid) "$Label retains the same service process in STOP_PENDING"
        Check ((Get-FileHash -LiteralPath $f.Paths.Executable -Algorithm SHA256).Hash -ceq $userHash) "$Label retains identical user executable"
        Check ((Get-FileHash -LiteralPath $f.Paths.ServiceExecutable -Algorithm SHA256).Hash -ceq $serviceHash) "$Label retains identical service executable"
    }
    try {
        [IO.File]::WriteAllText((Join-Path $f.Root 'Probe\Profile\Discord\app-change'),'hold owned fixture worker')
        Wait-Receipt $receipt 'HELD WORKER PID='
        $heldPid=[int]([regex]::Match((Get-Content $receipt -Raw),'HELD WORKER PID=(\d+)').Groups[1].Value)
        $held=[Diagnostics.Process]::GetProcessById($heldPid); try { Check ($held.MainModule.FileName -eq $f.Paths.ServiceExecutable) 'held child is the exact test-only service image' } finally { $held.Dispose() }
        [VencordSetup.Scm]::Stop($f.Paths.ServiceName)
        $pendingTimer=[Diagnostics.Stopwatch]::StartNew()
        do { $early=[PendingScmProbe]::Read($f.Paths.ServiceName); if ($early.State -eq 3) { break }; Start-Sleep -Milliseconds 100 } while ($pendingTimer.Elapsed.TotalSeconds -lt 10)
        Check ($early.State -eq 3 -and $early.Pid -eq $servicePid) 'owned stop promptly enters STOP_PENDING on the original process'
        Start-Sleep -Seconds 6
        $later=[PendingScmProbe]::Read($f.Paths.ServiceName)
        Check ($later.State -eq 3 -and $later.Pid -eq $servicePid -and $later.Checkpoint -gt $early.Checkpoint -and $later.WaitHint -eq 10000) 'actual SCM pending checkpoints advance with ten-second hints'
        $launches=([regex]::Matches((Get-Content $receipt -Raw),'NOTIFICATION WORK')).Count
        [IO.File]::WriteAllText((Join-Path $f.Root 'Probe\Profile\Discord\app-change'),'event after stop dispatch closed')
        Assert-PendingDrainRefusal { Stop-SetupService $f.Paths } 'actual SCM installer stop'
        Assert-PendingDrainRefusal { Uninstall-Fixture $f } 'actual SCM uninstall'
        $pendingReceipt=Get-Content $receipt -Raw
        Check (([regex]::Matches($pendingReceipt,'NOTIFICATION WORK')).Count -eq $launches) 'filesystem event after stop cannot launch new work'
        $held=[Diagnostics.Process]::GetProcessById($heldPid); try { Check (-not $held.HasExited) 'real owned child remains alive through both refusals' } finally { $held.Dispose() }
        Check (([regex]::Matches($pendingReceipt,'HOST STOP OVERRUN')).Count -eq 1) 'one soft overrun report across both bounded installer refusals'
        Check ($pendingReceipt -notmatch 'HELD WORKER EXITED|HELD WORKER DISPOSED|HOST STOPPED') 'active owned work cannot be released or reported stopped'
    } finally {
        Remove-Item -LiteralPath $failure
    }
    Wait-Receipt $receipt 'HELD WORKER EXITED' 10
    Stop-SetupService $f.Paths
    $stopped=Assert-SetupService $f.Paths
    Check ($stopped.State -eq 1 -and $stopped.Exit -eq 0 -and $stopped.SpecificExit -eq 0) 'explicit owned work release completes the original pending stop successfully'
    Wait-Receipt $receipt 'HOST STOPPED' 10
    Check ((Get-Content $receipt -Raw) -match 'HELD WORKER DISPOSED') 'released worker handle ownership is disposed before successful stop'
    Check (-not (Get-Process -Id $heldPid -ErrorAction SilentlyContinue)) 'released held child actually exits'
    Start-Sleep -Seconds 65
    $stillStopped=Assert-SetupService $f.Paths
    Check ($stillStopped.State -eq 1 -and $stillStopped.Exit -eq 0 -and $stillStopped.SpecificExit -eq 0) 'successful pending drain causes no failure-action restart after sixty seconds'
} finally { Remove-MachineFixture $f }
