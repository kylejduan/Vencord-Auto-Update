$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'artifacts/VencordAutoUpdate.exe'
$fixture = Join-Path $env:LOCALAPPDATA ("VencordAutoUpdate-app-test's space-" + [Guid]::NewGuid().ToString('N'))
$discord = Join-Path $fixture 'Discord'
$data = Join-Path $fixture 'helper'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-Owned([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    return $process
}
function Wait-Code($Process, [int]$Expected) {
    if (-not $Process.WaitForExit(15000)) { throw 'Owned command timed out.' }
    $output = $Process.StandardOutput.ReadToEnd()
    $errors = $Process.StandardError.ReadToEnd()
    if ($Process.ExitCode -ne $Expected) { throw "Expected exit $Expected, got $($Process.ExitCode): $errors" }
    return $output
}
try {
    [void][IO.Directory]::CreateDirectory($discord)
    $status = Start-Owned @('--status', '--data-dir', $data, '--discord-root', $discord)
    $output = Wait-Code $status 0
    if ($output -notmatch 'Discord.exe: Unsettled') { throw "Read-only status output missing: $output" }
    if (Test-Path -LiteralPath $data) { throw 'Read-only status created helper data.' }
    Write-Output 'PASS read-only status emits console output and creates no state/cache/logs'
    $bad = Start-Owned @('--unknown')
    [void](Wait-Code $bad 2)
    Write-Output 'PASS command error returns exit 2'
    $tokenAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe))
    $tokenFlags = [Reflection.BindingFlags]'Static,NonPublic'
    if (-not $tokenAssembly.GetType('VencordAutoUpdate.WorkerIdentity').GetProperty('Ordinary',$tokenFlags).GetValue($null,$null)) {
        Write-Output 'SKIP ordinary-user status UI fixture on elevated/service token; read-only CLI passed'
        exit 0
    }
    $app = Start-Owned @('--data-dir', $data, '--discord-root', $discord)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $app.Refresh()
    } while ($app.MainWindowHandle -eq [IntPtr]::Zero -and -not $app.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($app.HasExited) { throw "UI exited early: $($app.StandardError.ReadToEnd())" }
    if ($app.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Status UI window did not appear.' }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Start-Sleep -Milliseconds 1000
    $window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    foreach ($label in @('Discord channel repair status', 'Install', 'Uninstall', 'Exit helper', 'Allow visible automatic restart countdowns')) {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $label)
        if (-not $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)) { throw ("Accessible UI action missing: $label; Window: " + $window.Current.Name + "; Elements: " + (($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ", ")) }
    }
    foreach ($label in @('Install', 'Uninstall')) {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $label)
        if ($window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition).Current.IsEnabled) { throw 'Portable setup action was enabled.' }
    }
    Write-Output 'PASS fixture-only status/setup UI and accessible actions appear'
    $transform = $window.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern)
    $transform.Resize(640, 440)
    Start-Sleep -Milliseconds 300
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LayoutBounds {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
}
'@
    $bounds = $window.Current.BoundingRectangle
    foreach ($label in @('Repair & Restart', 'Open logs', 'Official Vencord', 'Install', 'Uninstall', 'About / License', 'Exit helper', 'Pause repairs', 'Allow visible automatic restart countdowns')) {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $label)
        $control = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $control) { throw "Missing layout control: $label" }
        $rect = New-Object LayoutBounds+Rect
        if (-not [LayoutBounds]::GetWindowRect([IntPtr]$control.Current.NativeWindowHandle, [ref]$rect)) { throw "Cannot inspect native bounds: $label" }
        if ($rect.Right -gt $bounds.Right -or $rect.Bottom -gt $bounds.Bottom -or $control.Current.IsOffscreen) {
            throw "Status control clipped at minimum size: $label"
        }
    }
    Write-Output 'PASS minimum status window size contains every button and checkbox'

    $duplicate = Start-Owned @('--worker', '--data-dir', $data, '--discord-root', $discord)
    [void](Wait-Code $duplicate 21)
    $show = Start-Owned @('--data-dir', $data, '--discord-root', $discord)
    [void](Wait-Code $show 0)
    Write-Output 'PASS duplicate worker receives actual Refused result and interactive launch shows existing UI'
    $stop = Start-Owned @('--stop', '--data-dir', $data)
    [void](Wait-Code $stop 0)
    [void](Wait-Code $app 0)
    Write-Output 'PASS scoped stop waits for orderly UI/process exit'
    $again = Start-Owned @('--stop', '--data-dir', $data)
    [void](Wait-Code $again 0)
    Write-Output 'PASS stopping absent fixture helper is idempotent'
    # Construct the real form with an empty, non-running supervisor to exercise
    # font autoscaling without changing Windows display settings or real app roots.
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe))
    $channelType = $assembly.GetType('VencordAutoUpdate.Channel', $true)
    $listType = [Collections.Generic.List`1].MakeGenericType(@($channelType))
    $channels = [Activator]::CreateInstance($listType)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $engineType = $assembly.GetType('VencordAutoUpdate.Supervisor', $true)
    $engine = $engineType.GetConstructors($flags)[0].Invoke(@($channels, [string]$discord, [string](Join-Path $fixture 'layout-data'), $null))
    $formType = $assembly.GetType('VencordAutoUpdate.StatusForm', $true)
    $noop = [Action]{}
    $form = $formType.GetConstructors($flags)[0].Invoke(@($engine, $noop, $noop, $noop, $noop, $noop, $false))
    $largeFont = [Drawing.Font]::new($form.Font.FontFamily, 14)
    try {
        $form.Show()
        $form.Font = $largeFont
        [Windows.Forms.Application]::DoEvents()
        $form.Size = $form.MinimumSize
        foreach ($control in $form.Controls) {
            if ($control -is [Windows.Forms.Button] -or $control -is [Windows.Forms.CheckBox]) {
                if ($control.Right -gt $form.ClientSize.Width -or $control.Bottom -gt $form.ClientSize.Height) { throw "Scaled minimum clips $($control.Text)" }
            }
        }
        Write-Output 'PASS enlarged-font minimum client size contains real status controls'
    } finally {
        $form.Close()
        $form.Dispose()
        $largeFont.Dispose()
    }

} finally {
    $cleanupStop = Start-Owned @('--stop', '--data-dir', $data)
    if (-not $cleanupStop.WaitForExit(45000) -or $cleanupStop.ExitCode -ne 0) { throw 'Owned UI could not drain; fixture retained.' }
    foreach ($process in $owned) {
        if (-not $process.HasExited) { [void]$process.WaitForExit(5000) }
        if (-not $process.HasExited) { throw "Owned process did not exit: $($process.Id)" }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
Write-Output 'PASS all owned processes exited and isolated fixture directory removed'
