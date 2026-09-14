$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = (Get-Item (Join-Path $PSScriptRoot '..')).FullName
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/CoreTests.exe')))
$instanceFlags = [Reflection.BindingFlags]'NonPublic,Instance'
$staticFlags = [Reflection.BindingFlags]'NonPublic,Static'
if (-not $assembly.GetType('VencordAutoUpdate.WorkerIdentity').GetProperty('Ordinary', $staticFlags).GetValue($null, $null)) {
    Write-Output 'SKIP ordinary-user UI fixture: current token is elevated or a service identity. Production worker refusal remains required.'
    exit 0
}
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class VauAcceptanceButton {
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder n, int c);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
$sessionType = $assembly.GetType('VencordAutoUpdate.SessionState')
if (-not $sessionType.GetProperty('Interactive', $staticFlags).GetValue($null, $null)) {
    Write-Output 'SKIP actual countdown: current Windows desktop is locked or noninteractive.'
    exit 0
}
$fixtureType = $assembly.GetType('VencordAutoUpdate.Fixtures')
$fixture = [Activator]::CreateInstance($fixtureType, $true)
$base = [string]$fixtureType.GetField('Base', $instanceFlags).GetValue($fixture)
$root = [string]$fixtureType.GetField('Root', $instanceFlags).GetValue($fixture)
$resources = [string]$fixtureType.GetField('Resources', $instanceFlags).GetValue($fixture)
$dist = [string]$fixtureType.GetField('Dist', $instanceFlags).GetValue($fixture)
$data = [string]$fixtureType.GetField('Cache', $instanceFlags).GetValue($fixture)
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
function Launch-Owned([string]$Path, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Path)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    return $process
}
function Hash-Bytes([byte[]]$Bytes) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($Bytes)).Replace('-', '') } finally { $hash.Dispose() }
}
try {
    $stock = [byte[]]$fixtureType.GetMethod('Stock', $staticFlags).Invoke($null, [object[]]@([string]'new host UI acceptance'))
    $oldStock = [byte[]]$fixtureType.GetMethod('Stock', $staticFlags).Invoke($null, [object[]]@([string]'old host UI acceptance'))
    $loader = [byte[]]$fixtureType.GetMethod('Loader', $instanceFlags).Invoke($fixture, [object[]]@([string]''))
    [IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'), $stock)
    $oldResources = Join-Path $root 'app-1.0.9/resources'
    [void][IO.Directory]::CreateDirectory($oldResources)
    [IO.File]::WriteAllBytes((Join-Path $oldResources 'app.asar'), $loader)
    [IO.File]::WriteAllBytes((Join-Path $oldResources '_app.asar'), $oldStock)
    [IO.File]::WriteAllText((Join-Path $root 'app-1.0.9/Discord.exe'), 'fixture')
    $fakeDiscord = Join-Path $root 'app-1.0.10/Discord.exe'
    [IO.File]::Copy((Join-Path $repo 'artifacts/CoreTests.exe'), $fakeDiscord, $true)
    $appExe = Join-Path $base 'VencordAutoUpdate.exe'
    [IO.File]::Copy((Join-Path $repo 'artifacts/VencordAutoUpdate.exe'), $appExe)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $discordProcess = Launch-Owned $fakeDiscord @('--fixture-wait')
    $appProcess = Launch-Owned $appExe @('--worker', '--data-dir', $data, '--discord-root', $root, '--vencord-dist', $dist)
    # --worker is the bounded relay; consent belongs to its ordinary host.
    $hostDeadline=[Diagnostics.Stopwatch]::StartNew();$hostProcess=$null
    do {
        foreach($candidate in @(Get-CimInstance Win32_Process -Filter "Name = 'VencordAutoUpdate.exe'")) {
            if($candidate.ParentProcessId -eq $appProcess.Id -and $candidate.ExecutablePath -eq $appExe -and $candidate.CommandLine -match '--worker-host') {
                $hostProcess=[Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId);$owned.Add($hostProcess);break
            }
        }
        if(-not $hostProcess){Start-Sleep -Milliseconds 100}
    } while(-not $hostProcess -and $hostDeadline.Elapsed.TotalSeconds -lt 5)
    if(-not $hostProcess){throw 'Exact owned ordinary worker host did not publish.'}
    $processCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$hostProcess.Id)
    $buttonCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Not now')
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    $cancel = $null
    do {
        Start-Sleep -Milliseconds 200
        foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $processCondition)) {
            $cancel = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $buttonCondition)
            if ($cancel) { break }
        }
    } while (-not $cancel -and -not $appProcess.HasExited -and -not $discordProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if (-not $cancel) { throw 'Actual cancelable restart dialog did not appear during the young fixture session.' }
    $nativeButton = [IntPtr]$cancel.Current.NativeWindowHandle
    $buttonOwner = [uint32]0
    [void][VauAcceptanceButton]::GetWindowThreadProcessId($nativeButton, [ref]$buttonOwner)
    $buttonClass = [Text.StringBuilder]::new(256)
    [void][VauAcceptanceButton]::GetClassName($nativeButton, $buttonClass, $buttonClass.Capacity)
    if ($buttonOwner -ne $hostProcess.Id -or $buttonClass.ToString() -notmatch 'BUTTON') { throw 'Not now did not resolve to the exact owned native button.' }
    [void][VauAcceptanceButton]::SendMessage($nativeButton, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 100
        $state = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
        $deferred = @($state.Generations | Where-Object { $_.Deferred -and $_.Reserved }).Count -gt 0
    } while (-not $deferred -and [DateTime]::UtcNow -lt $deadline)
    if (-not $deferred) { throw 'Cancellation was not durably recorded.' }
    if ($discordProcess.HasExited) { throw 'Fixture Discord exited before the cancellation preservation check.' }
    if ((Hash-Bytes ([IO.File]::ReadAllBytes((Join-Path $resources 'app.asar')))) -ne (Hash-Bytes $stock)) { throw 'Cancellation changed the current stock archive.' }
    if (Test-Path -LiteralPath (Join-Path $resources '_app.asar')) { throw 'Cancellation created an original backup while Discord was running.' }
    Write-Output 'PASS real restart dialog Not now preserves the running fixture and stock archive, and persists deferral.'
    if (-not $appProcess.WaitForExit(5000) -or $appProcess.ExitCode -ne 65) { throw 'Canceled worker did not exit Deferred.' }
    Write-Output 'PASS canceled worker exits Deferred while fixture Discord remains running.'
    if (-not $discordProcess.WaitForExit(35000)) { throw 'Owned fixture process failed to exit.' }
    $appProcess = Launch-Owned $appExe @('--worker', '--data-dir', $data, '--discord-root', $root, '--vencord-dist', $dist)
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 250
        $repaired = (Test-Path -LiteralPath (Join-Path $resources '_app.asar')) -and (Test-Path -LiteralPath (Join-Path $resources 'app.asar')) -and ((Hash-Bytes ([IO.File]::ReadAllBytes((Join-Path $resources 'app.asar')))) -eq (Hash-Bytes $loader))
    } while (-not $repaired -and -not $appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if (-not $repaired) { throw 'Deferred fixture did not repair after full exit.' }
    if ((Hash-Bytes ([IO.File]::ReadAllBytes((Join-Path $resources '_app.asar')))) -ne (Hash-Bytes $stock)) { throw 'Deferred repair altered the preserved original.' }
    Write-Output 'PASS deferred repair restores the loader after full fixture exit and preserves the original byte for byte.'
    if (-not $appProcess.WaitForExit(5000) -or $appProcess.ExitCode -ne 0) { throw 'Full-exit follow-up worker did not finish Done.' }
    $stop = Launch-Owned $appExe @('--stop', '--data-dir', $data)
    if (-not $stop.WaitForExit(45000) -or $stop.ExitCode -ne 0) { throw 'Orderly fixture helper stop failed.' }
    if (-not $appProcess.WaitForExit(5000) -or $appProcess.ExitCode -ne 0) { throw 'Fixture helper did not finish successfully.' }
    Write-Output 'PASS scoped helper shutdown completes after real deferred repair.'
} finally {
    if ($appExe -and (Test-Path -LiteralPath $appExe)) {
        $cleanupStop = Launch-Owned $appExe @('--stop', '--data-dir', $data)
        if (-not $cleanupStop.WaitForExit(45000) -or $cleanupStop.ExitCode -ne 0) { throw 'Worker could not drain; fixture retained.' }
    }
    foreach ($process in $owned) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        if (-not $process.HasExited) { throw "Owned process remains: $($process.Id)" }
        $process.Dispose()
    }
    $fixture.Dispose()
    if (Test-Path -LiteralPath $base) { throw 'Owned fixture cleanup incomplete.' }
    Write-Output 'PASS all acceptance-owned processes exited and the temporary fixture was removed.'
}
