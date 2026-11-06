param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if ($repo.StartsWith('\\')) { throw 'Run ordinary-user acceptance from a native Windows checkout, not a network or WSL UNC path.' }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $administrator=([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($administrator -or $identity.IsSystem -or $identity.User.IsWellKnown('LocalServiceSid') -or $identity.User.IsWellKnown('NetworkServiceSid')) {
        throw 'Run ordinary-user acceptance without elevation, on an unlocked Windows desktop.'
    }
} finally { $identity.Dispose() }
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $repo 'artifacts/user-acceptance' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
function Get-SourceFiles {
    $files=@(foreach($name in @('src','scripts','tests','docs','.github')) { Get-ChildItem -LiteralPath (Join-Path $repo $name) -File -Recurse })
    $files+=@(Get-ChildItem -LiteralPath $repo -File | Where-Object Name -NE '.git')
    $files | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{Path=$_.FullName.Substring($repo.Length+1).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}
function Get-SourceFingerprint($Files) {
    $records=@($Files | ForEach-Object { $_.Path+':'+$_.Sha256 })
    $sha=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($records -join "`n")))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$started=[DateTime]::UtcNow.ToString('o')
$sourceFiles=@(Get-SourceFiles)
$fingerprint=Get-SourceFingerprint $sourceFiles
$results=[Collections.Generic.List[object]]::new()
$passed=$false
$scripts=@('scripts/test.ps1','scripts/build.ps1','scripts/test-service.ps1',
    'tests/PowerShell.Parse.Tests.ps1','tests/Setup.Contracts.Tests.ps1','tests/Setup.Ui.Tests.ps1','tests/Package.Tests.ps1',
    'tests/Service.Wts.Tests.ps1','tests/Countdown.Clock.Tests.ps1','tests/Worker.Startup.Tests.ps1',
    'tests/Worker.Status.Tests.ps1','tests/Status.Handoff.Tests.ps1','tests/App.Smoke.Tests.ps1',
    'tests/Worker.Smoke.Tests.ps1','tests/Countdown.Smoke.Tests.ps1','tests/Restart.Smoke.Tests.ps1','tests/Shutdown.Smoke.Tests.ps1')
try {
    foreach($script in $scripts) {
        $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'))
        $info.UseShellExecute=$false; $info.CreateNoWindow=$true
        $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
        $info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $repo $script)+'"'
        if($script -eq 'tests/Package.Tests.ps1'){$info.Arguments+=' -LayoutOnly'}
        $info.WorkingDirectory=$repo
        $timer=[Diagnostics.Stopwatch]::StartNew()
        $process=[Diagnostics.Process]::Start($info)
        try {
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(600000)) { throw "Acceptance timed out in $script; process $($process.Id) and its evidence were retained. Drain its owned fixtures before retrying." }
            $text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
            $log=Join-Path $OutputDirectory (($script -replace '/','-')+'.log')
            [IO.File]::WriteAllText($log,$text)
            $skip=$text -match '(?m)\bSKIP\b'
            $results.Add([pscustomobject]@{Script=$script;ExitCode=$process.ExitCode;Skipped=$skip;Seconds=$timer.Elapsed.TotalSeconds})
            if ($process.ExitCode -ne 0 -or $skip) { throw "Acceptance failed or skipped coverage in $script. See $log" }
            Write-Output "PASS $script"
        } finally { $process.Dispose() }
    }
    if ((Get-SourceFingerprint @(Get-SourceFiles)) -ne $fingerprint) { throw 'Source changed during acceptance; rerun on one unchanged source tree.' }
    $passed=$true
} finally {
    [pscustomobject]@{Passed=$passed;StartedUtc=$started;FinishedUtc=[DateTime]::UtcNow.ToString('o');SourceSha256=$fingerprint;SourceFiles=$sourceFiles;Results=@($results.ToArray())} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
}
