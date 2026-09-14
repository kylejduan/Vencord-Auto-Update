$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'artifacts/VencordAutoUpdate.exe'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/CoreTests.exe')))
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$static = [Reflection.BindingFlags]'NonPublic,Static'
$type = $assembly.GetType('VencordAutoUpdate.Fixtures', $true)
$fixture = [Activator]::CreateInstance($type, $true)
$root = $type.GetField('Root',$flags).GetValue($fixture)
$data = $type.GetField('Cache',$flags).GetValue($fixture)
$resources = $type.GetField('Resources',$flags).GetValue($fixture)
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-Owned([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $p = [Diagnostics.Process]::Start($info); $owned.Add($p); return $p
}
function Wait-Code($Process,[int]$Expected) {
    if (-not $Process.WaitForExit(75000)) { throw 'Owned worker did not terminate within fixture deadline.' }
    if ($Process.ExitCode -ne $Expected) { throw "Expected worker exit $Expected, got $($Process.ExitCode)" }
}
try {
    $loader = [byte[]]$type.GetMethod('Loader',$flags).Invoke($fixture,@(''))
    $stock = [byte[]]$type.GetMethod('Stock',$static).Invoke($null,@('worker fixture'))
    [IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'),$loader)
    [IO.File]::WriteAllBytes((Join-Path $resources '_app.asar'),$stock)
    $worker = Start-Owned @('--worker','--data-dir',$data,'--discord-root',$root)
    if (-not $assembly.GetType('VencordAutoUpdate.WorkerIdentity').GetProperty('Ordinary',$static).GetValue($null,$null)) {
        Wait-Code $worker 21
        $elevatedUi = Start-Owned @('--data-dir',$data,'--discord-root',$root)
        Wait-Code $elevatedUi 21
        Write-Output 'PASS elevated/service worker and UI tokens refused; SKIP ordinary-user native worker/UI flow on this token'
        exit 0
    }
    if (-not $worker.WaitForExit(5000)) { throw 'Healthy worker remained resident.' }
    Wait-Code $worker 0
    if (@(Get-ChildItem -LiteralPath (Join-Path $data 'cache')).Count -eq 0) { throw 'Healthy worker missed cache.' }
    Write-Output 'PASS healthy native worker caches and exits Done without a resident UI'
    $ui = Start-Owned @('--data-dir',$data,'--discord-root',$root)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 100; $ui.Refresh() } while ($ui.MainWindowHandle -eq [IntPtr]::Zero -and -not $ui.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($ui.HasExited -or $ui.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Owned manual UI missing.' }
    Start-Sleep -Seconds 1
    $ui.Refresh(); $beforeCpu = $ui.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 2
    $ui.Refresh(); $idleCpu = $ui.TotalProcessorTime.TotalMilliseconds - $beforeCpu
    Write-Output "OBSERVED manual healthy UI 2s CPU delta=${idleCpu}ms; worker absent=$($worker.HasExited)"
    [IO.File]::Delete((Join-Path $resources '_app.asar'))
    [IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'),$stock)
    $forward = Start-Owned @('--worker','--data-dir',$data,'--discord-root',$root)
    $coalesced = Start-Owned @('--worker','--data-dir',$data,'--discord-root',$root)
    Wait-Code $forward 0
    Wait-Code $coalesced 0
    if ((Get-FileHash -LiteralPath (Join-Path $resources 'app.asar')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $resources '_app.asar')).Hash) { throw 'Forwarded work acknowledged without actual repair.' }
    if ($ui.HasExited) { throw 'Forwarding closed the manual UI.' }
    Write-Output 'PASS idle manual UI accepts service work and acknowledges only after actual repair'
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $window = [Windows.Automation.AutomationElement]::FromHandle($ui.MainWindowHandle)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Allow visible automatic restart countdowns')
    $checkbox = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VauWorkerButton {
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
}
'@
    $buttonHandle = [IntPtr]$checkbox.Current.NativeWindowHandle
    $buttonOwner = [uint32]0
    [void][VauWorkerButton]::GetWindowThreadProcessId($buttonHandle,[ref]$buttonOwner)
    if($buttonOwner -ne $ui.Id) { throw 'Preference checkbox is not owned by fixture UI.' }
    [void][VauWorkerButton]::SendMessage($buttonHandle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
    $preference = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
    if($preference.Automatic) { throw 'Fixture UI did not persist disabled automatic restart preference.' }
    Start-Sleep -Milliseconds 500
    $stock = [byte[]]$type.GetMethod('Stock',$static).Invoke($null,@('next generation deferred fixture'))
    # A live exact-root fixture plus disabled automatic restart must reach the
    # service caller as Deferred, so the broker can arm post-worker exit waits.
    $discordExe = Join-Path $root 'app-1.0.10/Discord.exe'
    [IO.File]::Copy((Join-Path $repo 'artifacts/CoreTests.exe'),$discordExe,$true)
    $info = [Diagnostics.ProcessStartInfo]::new($discordExe,'--fixture-wait')
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $discord = [Diagnostics.Process]::Start($info); $owned.Add($discord)
    [IO.File]::Delete((Join-Path $resources '_app.asar'))
    [IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'),$stock)
    # A distinct generation avoids the previous repair attempt cooldown.
    # The actual UI preference must durably defer this running cohort.
    $deferred = Start-Owned @('--worker','--data-dir',$data,'--discord-root',$root)
    Wait-Code $deferred 65
    if ($discord.HasExited -or (Test-Path -LiteralPath (Join-Path $resources '_app.asar'))) { throw 'Forwarded deferral did not preserve the running fixture.' }
    Write-Output 'PASS UI acknowledges actual Deferred while exact-root fixture remains running'
    if (-not $discord.WaitForExit(35000)) { throw 'Owned fixture did not exit naturally.' }
    $followup = Start-Owned @('--worker','--data-dir',$data,'--discord-root',$root)
    Wait-Code $followup 0
    Write-Output 'PASS explicit full-exit event repairs through the still-open manual UI'
    $unknown = [byte[]]$type.GetMethod('Loader',$flags).Invoke($fixture,@(';unknown()'))
    [IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'),$unknown)
    $refused = Start-Owned @('--worker','--data-dir',$data,'--discord-root',$root)
    Wait-Code $refused 21
    Write-Output 'PASS second UI batch returns actual stable Refused result'
    [IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'),$loader)
    $again = Start-Owned @('--worker','--data-dir',$data,'--discord-root',$root)
    Wait-Code $again 0
    if (-not $ui.CloseMainWindow() -or -not $ui.WaitForExit(5000) -or $ui.ExitCode -ne 0) { throw 'Closing status did not exit.' }
    Write-Output 'PASS subsequent batch remains usable and closing manual status exits'
} finally {
    $stop = Start-Owned @('--stop','--data-dir',$data)
    if (-not $stop.WaitForExit(45000) -or $stop.ExitCode -ne 0) { throw 'Owned worker could not drain; fixture retained.' }
    foreach ($p in $owned) {
        if (-not $p.WaitForExit(35000)) { throw "Owned process remains: $($p.Id); fixture retained." }
        $p.Dispose()
    }
    foreach($candidate in @(Get-CimInstance Win32_Process -Filter "Name = 'VencordAutoUpdate.exe'")) {
        if($candidate.ExecutablePath -ne $exe -or -not $candidate.CommandLine.Contains('"'+$data+'"')){continue}
        $hostProcess=[Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId)
        try {if(-not $hostProcess.WaitForExit(5000)){throw 'Exact fixture host remains after orderly stop; fixture retained.'}} finally {$hostProcess.Dispose()}
    }
    $fixture.Dispose()
}
Write-Output 'PASS worker fixtures drained and cleaned'
