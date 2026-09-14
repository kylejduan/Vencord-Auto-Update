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
$sessionType = $assembly.GetType('VencordAutoUpdate.SessionState')
if (-not $sessionType.GetProperty('Interactive', $staticFlags).GetValue($null, $null)) {
    Write-Output 'SKIP accepted countdown: current Windows desktop is locked or noninteractive.'
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
$fixtureStarted = [DateTime]::UtcNow
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
    $fixtureSource = Join-Path $base 'FixtureHost.cs'
    [IO.File]::WriteAllText($fixtureSource, @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
class FixtureHost {
    static int Main(string[] args) {
        string self = Process.GetCurrentProcess().MainModule.FileName;
        if (Path.GetFileName(self) == "Update.exe") {
            string root = Path.GetDirectoryName(self);
            string marker = Path.Combine(Path.GetDirectoryName(root), "relaunch.txt");
            if (args.Length != 2 || args[0] != "--processStart" || args[1] != "Discord.exe") return 2;
            string app = Path.Combine(root, "app-1.0.10", "Discord.exe");
            using (Process child = Process.Start(new ProcessStartInfo(app, "--fixture-wait") { UseShellExecute = false, CreateNoWindow = true })) {
                File.WriteAllText(marker, child.Id + "|" + child.StartTime.ToUniversalTime().Ticks + "|" + app);
            }
            return 0;
        }
        if (args.Length != 1 || args[0] != "--fixture-wait") return 2;
        Thread.Sleep(120000);
        return 0;
    }
}
'@)
    $fakeDiscord = Join-Path $root 'app-1.0.10/Discord.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /warnaserror+ /target:winexe /langversion:5 "/out:$fakeDiscord" $fixtureSource
    if ($LASTEXITCODE -ne 0) { throw 'Owned fixture host compilation failed.' }
    [IO.File]::Copy($fakeDiscord, (Join-Path $root 'Update.exe'), $true)
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
    Write-Output 'PASS actual automatic restart countdown appears for the young fixture session.'
    if ($discordProcess.HasExited) { throw 'Fixture exited before the visible countdown.' }
    if ((Hash-Bytes ([IO.File]::ReadAllBytes((Join-Path $resources 'app.asar')))) -ne (Hash-Bytes $stock)) { throw 'Stock archive changed before countdown completion.' }
    $receipt = Join-Path $base 'relaunch.txt'
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        Start-Sleep -Milliseconds 200
    } while (-not (Test-Path -LiteralPath $receipt) -and -not $appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if (-not (Test-Path -LiteralPath $receipt)) { throw 'Accepted countdown did not relaunch through the owned Update.exe.' }
    $identity = ([IO.File]::ReadAllText($receipt)).Split('|')
    $reopened = [Diagnostics.Process]::GetProcessById([int]$identity[0])
    if ($reopened.StartTime.ToUniversalTime().Ticks -ne [long]$identity[1] -or $reopened.MainModule.FileName -ne $fakeDiscord) { throw 'Relaunch receipt does not identify the exact owned fixture process.' }
    $owned.Add($reopened)
    if (-not $discordProcess.HasExited) { throw 'Old fixture cohort survived the accepted restart.' }
    if ($reopened.Id -eq $discordProcess.Id) { throw 'Relaunch did not create a new process.' }
    if ((Hash-Bytes ([IO.File]::ReadAllBytes((Join-Path $resources 'app.asar')))) -ne (Hash-Bytes $loader)) { throw 'Accepted restart did not restore the validated loader.' }
    if ((Hash-Bytes ([IO.File]::ReadAllBytes((Join-Path $resources '_app.asar')))) -ne (Hash-Bytes $stock)) { throw 'Accepted restart changed original bytes.' }
    Write-Output 'PASS accepted countdown stops the old cohort, repairs safely, and relaunches through Update.exe.'
    if ($appProcess.HasExited) { throw 'Worker exited before delayed verification.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    do {
        Start-Sleep -Milliseconds 250
        $state = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
        $generation = @($state.Generations | Where-Object { $_.Reserved })
        $verified = $generation.Count -eq 1 -and $generation[0].VerifyAfter -eq 0 -and -not $generation[0].VerificationFailed
    } while (-not $verified -and -not $appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if (-not $verified -or $generation[0].Attempts -ne 1) { throw 'Delayed verification or persisted single-attempt restart reservation failed.' }
    if ($reopened.HasExited) { throw 'Reopened fixture exited during verification.' }
    if (-not $appProcess.WaitForExit(5000) -or $appProcess.ExitCode -ne 0) { throw 'Verified worker did not exit Done.' }
    Write-Output 'PASS delayed file verification succeeds while the restarted fixture remains running, with one automatic attempt.'
    $stop = Launch-Owned $appExe @('--stop', '--data-dir', $data)
    if (-not $stop.WaitForExit(45000) -or $stop.ExitCode -ne 0) { throw 'Orderly fixture helper stop failed.' }
    if (-not $appProcess.WaitForExit(5000) -or $appProcess.ExitCode -ne 0) { throw 'Fixture helper did not finish successfully.' }
    Write-Output 'PASS scoped helper shutdown completes after actual automatic repair and relaunch.'
} finally {
    if ($appExe -and (Test-Path -LiteralPath $appExe)) {
        $cleanupStop = Launch-Owned $appExe @('--stop', '--data-dir', $data)
        if (-not $cleanupStop.WaitForExit(45000) -or $cleanupStop.ExitCode -ne 0) { throw 'Worker could not drain; fixture retained.' }
    }
    if (Test-Path -LiteralPath (Join-Path $base 'relaunch.txt')) {
        try {
            $identity = ([IO.File]::ReadAllText((Join-Path $base 'relaunch.txt'))).Split('|')
            $child = [Diagnostics.Process]::GetProcessById([int]$identity[0])
            if ($child.StartTime.ToUniversalTime().Ticks -eq [long]$identity[1] -and $child.MainModule.FileName -eq (Join-Path $root 'app-1.0.10/Discord.exe')) {
                if (-not $child.HasExited) { $child.Kill(); [void]$child.WaitForExit(5000) }
            }
            $child.Dispose()
        } catch [ArgumentException] { }
    }
    foreach ($process in $owned) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        if (-not $process.HasExited) { throw "Owned process remains: $($process.Id)" }
        $process.Dispose()
    }
    # A launcher can spawn its child before writing the receipt. In that narrow
    # failure window, find only executables inside this unique fixture root.
    foreach ($candidate in @(Get-CimInstance Win32_Process -Filter "Name = 'Discord.exe' OR Name = 'Update.exe'")) {
        $exactDiscord = Join-Path $root 'app-1.0.10/Discord.exe'
        $exactUpdater = Join-Path $root 'Update.exe'
        if ($candidate.ExecutablePath -ne $exactDiscord -and $candidate.ExecutablePath -ne $exactUpdater) { continue }
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId)
            if ($process.StartTime.ToUniversalTime() -ge $fixtureStarted -and $process.MainModule.FileName -eq $candidate.ExecutablePath) {
                if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
            }
            $process.Dispose()
        } catch [ArgumentException] { }
    }
    $fixture.Dispose()
    if (Test-Path -LiteralPath $base) { throw 'Owned fixture cleanup incomplete.' }
    Write-Output 'PASS all acceptance-owned processes exited and the temporary fixture was removed.'
}
