$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $repo 'artifacts/VencordAutoUpdate.exe'
$base=Join-Path $env:LOCALAPPDATA ('Vencord-Startup-Test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($base)
$owned=[Collections.Generic.List[Diagnostics.Process]]::new()
function Invoke-Startup([string[]]$Arguments,[int]$Expected) {
    $info=[Diagnostics.ProcessStartInfo]::new($exe);$info.UseShellExecute=$false
    $info.Arguments=($Arguments | ForEach-Object { '"'+$_+'"' }) -join ' '
    $p=[Diagnostics.Process]::Start($info);$owned.Add($p)
    if(-not $p.WaitForExit(10000)){throw 'Malformed startup did not return promptly.'}
    if($p.ExitCode -ne $Expected){throw "Startup expected $Expected, got $($p.ExitCode)"}
}
try {
    $data=Join-Path $base 'Data';$root=Join-Path $base 'Discord'
    # A wrong-type kernel object at this exact fixture's stop-event name causes
    # a real instance-publication failure after successful argument parsing.
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/CoreTests.exe')))
    if(-not $assembly.GetType('VencordAutoUpdate.WorkerIdentity').GetProperty('Ordinary',[Reflection.BindingFlags]'NonPublic,Static').GetValue($null,$null)) {
        Invoke-Startup @('--worker','--data-dir',$data,'--discord-root',$root) 21
        Invoke-Startup @('--worker-host','--data-dir',$data,'--discord-root',$root) 21
        Write-Output 'PASS elevated relay and host refused; SKIP ordinary-token publication failure on this token'
        exit 0
    }
    $name=$assembly.GetType('VencordAutoUpdate.InstanceControl').GetMethod('Name',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,[object[]]@([string]'stop',[string]$data))
    $obstruction=[Threading.Mutex]::new($false,$name)
    Invoke-Startup @('--worker','--data-dir',$data,'--discord-root',$root) 22
    Invoke-Startup @('--worker-host','--data-dir',$data,'--discord-root',$root) 22
    Invoke-Startup @('--data-dir',$data,'--discord-root',$root) 1
    Invoke-Startup @('--worker','--unknown') 2
    $obstruction.Dispose();$obstruction=$null
    Invoke-Startup @('--worker-host','--data-dir',$data,'--discord-root',$root) 22
    Write-Output 'PASS unrequested ordinary host exits Retry within startup bound with no idle resident'
    Write-Output 'PASS parsed worker startup failure Retry22; nonworker failure1; invalid CLI2'
} finally {
    if($obstruction){$obstruction.Dispose()}
    foreach($p in $owned){if(-not $p.HasExited){throw "Owned startup process remains $($p.Id); fixture retained"};$p.Dispose()}
    [IO.Directory]::Delete($base,$true)
}
