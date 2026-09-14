param([string]$AppExecutable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $AppExecutable) { $AppExecutable = Join-Path $repo 'artifacts/VencordAutoUpdate.exe' }
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/CoreTests.exe')))
$instanceFlags = [Reflection.BindingFlags]'NonPublic,Instance'
$staticFlags = [Reflection.BindingFlags]'NonPublic,Static'
if (-not $assembly.GetType('VencordAutoUpdate.WorkerIdentity').GetProperty('Ordinary', $staticFlags).GetValue($null, $null)) {
    Write-Output 'SKIP ordinary-user UI fixture: current token is elevated or a service identity. Production worker refusal remains required.'
    exit 0
}
if (-not $assembly.GetType('VencordAutoUpdate.SessionState').GetProperty('Interactive', $staticFlags).GetValue($null, $null)) {
    throw 'An unlocked interactive desktop is required for the manual-confirmation shutdown smoke.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class VauShutdownButton {
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder n, int c);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
$fixtureType = $assembly.GetType('VencordAutoUpdate.Fixtures')
$fixture = [Activator]::CreateInstance($fixtureType, $true)
$base = [string]$fixtureType.GetField('Base', $instanceFlags).GetValue($fixture)
$root = [string]$fixtureType.GetField('Root', $instanceFlags).GetValue($fixture)
$data = [string]$fixtureType.GetField('Cache', $instanceFlags).GetValue($fixture)
$resources = [string]$fixtureType.GetField('Resources', $instanceFlags).GetValue($fixture)
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
try {
    [void]$assembly.GetType('VencordAutoUpdate.SupervisorTests').GetMethod('Seed', $staticFlags).Invoke($null, [object[]]@($fixture))
    $archive = Join-Path $resources 'app.asar'
    $stockHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $data 'state.json'), '{"Schema":1,"Paused":false,"Automatic":false,"LastRestart":0,"Generations":[]}')
    $discordExe = Join-Path $root 'app-1.0.10/Discord.exe'
    [IO.File]::Copy((Join-Path $repo 'artifacts/CoreTests.exe'), $discordExe, $true)
    $appExe = Join-Path $base 'VencordAutoUpdate.exe'
    [IO.File]::Copy($AppExecutable, $appExe)
    $discord = Launch-Owned $discordExe @('--fixture-wait')
    $app = Launch-Owned $appExe @('--data-dir', $data, '--discord-root', $root)
    $owner = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$app.Id)
    $buttonName = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Repair & Restart')
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $button = $null
    do {
        Start-Sleep -Milliseconds 200
        foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $owner)) {
            $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $buttonName)
            if ($button) { break }
        }
    } while (-not $button -and -not $app.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if (-not $button) { throw 'Owned status action did not appear.' }
    Start-Sleep -Seconds 12
    $handle = [IntPtr]$button.Current.NativeWindowHandle
    $buttonOwner = [uint32]0
    [void][VauShutdownButton]::GetWindowThreadProcessId($handle, [ref]$buttonOwner)
    $buttonClass = [Text.StringBuilder]::new(256)
    [void][VauShutdownButton]::GetClassName($handle, $buttonClass, $buttonClass.Capacity)
    if ($buttonOwner -ne $app.Id -or $buttonClass.ToString() -notmatch 'BUTTON') { throw 'Action is not an exact owned native button.' }
    if (-not [VauShutdownButton]::PostMessage($handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) { throw 'Could not post owned action.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $confirmation = $null
    do {
        Start-Sleep -Milliseconds 100
        foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $owner)) {
            foreach ($candidate in $window.FindAll([Windows.Automation.TreeScope]::Subtree, [Windows.Automation.Condition]::TrueCondition)) {
                if ($candidate.Current.ProcessId -eq $app.Id -and $candidate.Current.ControlType -eq [Windows.Automation.ControlType]::Window -and $candidate.Current.Name -like 'Confirm Repair*Restart') { $confirmation = $candidate; break }
            }
            if ($confirmation) { break }
        }
    } while (-not $confirmation -and -not $app.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if (-not $confirmation) {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $owner)
        $details = @($windows | ForEach-Object { $_.Current.Name + ': ' + (($_.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ', ') }) -join '; '
        throw ("Manual repair confirmation did not appear. Owned UI: " + $details)
    }
    Write-Output 'PASS actual manual confirmation is open for a running stock fixture'
    $stop = Launch-Owned $appExe @('--stop', '--data-dir', $data)
    if (-not $stop.WaitForExit(5000)) { throw 'Stop is blocked by pending manual confirmation.' }
    if ($stop.ExitCode -ne 0 -or -not $app.WaitForExit(5000) -or $app.ExitCode -ne 0) { throw 'Orderly confirmation shutdown failed.' }
    if ($discord.HasExited -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $stockHash -or (Test-Path -LiteralPath (Join-Path $resources '_app.asar'))) { throw 'Shutdown altered the running Discord fixture or stock archive.' }
    $state = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
    if (@($state.Generations | Where-Object { $_.Attempts -ne 0 }).Count -ne 0) { throw 'Unaccepted confirmation consumed a repair attempt.' }
    Write-Output 'PASS scoped stop dismisses confirmation and exits without stopping Discord or changing archives'
} finally {
    if ($appExe -and (Test-Path -LiteralPath $appExe)) {
        $cleanupStop = Launch-Owned $appExe @('--stop', '--data-dir', $data)
        if (-not $cleanupStop.WaitForExit(45000) -or $cleanupStop.ExitCode -ne 0) { throw 'Worker could not drain; fixture retained.' }
    }
    foreach ($process in $owned) {
        if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        if (-not $process.HasExited) { throw "Owned process remains: $($process.Id)" }
        $process.Dispose()
    }
    $fixture.Dispose()
    if (Test-Path -LiteralPath $base) { throw 'Owned fixture directory remains.' }
    Write-Output 'PASS all shutdown-smoke processes and fixture files cleaned'
}
