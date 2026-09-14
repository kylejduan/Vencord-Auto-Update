$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/CoreTests.exe')))
$flags=[Reflection.BindingFlags]'NonPublic,Instance';$static=[Reflection.BindingFlags]'NonPublic,Static'
if(-not $assembly.GetType('VencordAutoUpdate.WorkerIdentity').GetProperty('Ordinary',$static).GetValue($null,$null)){Write-Output 'SKIP ordinary-token Status ordering fixture';exit 0}
$type=$assembly.GetType('VencordAutoUpdate.Fixtures',$true)
foreach($scenario in @('Done','Deferred','simultaneous startup')) {
 $expected=0;if($scenario -eq 'Deferred'){$expected=65}
 $fixture=[Activator]::CreateInstance($type,$true)
 $base=[string]$type.GetField('Base',$flags).GetValue($fixture);$root=[string]$type.GetField('Root',$flags).GetValue($fixture)
 $data=[string]$type.GetField('Cache',$flags).GetValue($fixture);$resources=[string]$type.GetField('Resources',$flags).GetValue($fixture)
 $exe=Join-Path $base 'VencordAutoUpdate.exe';[IO.File]::Copy((Join-Path $repo 'artifacts/VencordAutoUpdate.exe'),$exe)
 $owned=[Collections.Generic.List[Diagnostics.Process]]::new()
 function Start-Owned([string]$Path,[string[]]$Arguments) {
  $info=[Diagnostics.ProcessStartInfo]::new($Path);$info.UseShellExecute=$false;$info.CreateNoWindow=$true
  $info.Arguments=($Arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
  $p=[Diagnostics.Process]::Start($info);$owned.Add($p);return $p
 }
 function Wait-Code($p,[int]$code,[int]$timeout=20000) {
  if(-not $p.WaitForExit($timeout)){throw 'Service-facing worker withheld actual completion while Status was open.'}
  if($p.ExitCode -ne $code){throw "Worker expected $code, got $($p.ExitCode)"}
 }
 try {
  $loader=[byte[]]$type.GetMethod('Loader',$flags).Invoke($fixture,@(''));$stock=[byte[]]$type.GetMethod('Stock',$static).Invoke($null,@('status ordering fixture'))
  [IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'),$loader);[IO.File]::WriteAllBytes((Join-Path $resources '_app.asar'),$stock)
  $seed=Start-Owned $exe @('--worker','--data-dir',$data,'--discord-root',$root);Wait-Code $seed 0
  $stop=Start-Owned $exe @('--stop','--data-dir',$data);Wait-Code $stop 0
  [IO.File]::WriteAllText((Join-Path $data 'state.json'),'{"Schema":1,"Paused":false,"Automatic":false,"LastRestart":0,"Generations":[]}')
  if($expected -eq 65){
   $discordExe=Join-Path $root 'app-1.0.10/Discord.exe';[IO.File]::Copy((Join-Path $repo 'artifacts/CoreTests.exe'),$discordExe,$true)
   $discord=Start-Owned $discordExe @('--fixture-wait')
  }
  [IO.File]::Delete((Join-Path $resources '_app.asar'));[IO.File]::WriteAllBytes((Join-Path $resources 'app.asar'),$stock)
  $worker=Start-Owned $exe @('--worker','--data-dir',$data,'--discord-root',$root)
  if($scenario -ne 'simultaneous startup'){Start-Sleep -Seconds 2}
  if($worker.HasExited){throw 'Worker must be active during Status request'}
  $show=Start-Owned $exe @('--data-dir',$data,'--discord-root',$root)
  $timer=[Diagnostics.Stopwatch]::StartNew();$ui=$null
  do {
   foreach($p in @(Get-Process -Name VencordAutoUpdate -ErrorAction SilentlyContinue)) {
    if($p.Path -eq $exe -and $p.MainWindowHandle -ne [IntPtr]::Zero){$ui=$p;break};$p.Dispose()
   }
   if(-not $ui){Start-Sleep -Milliseconds 100}
  } while(-not $ui -and $timer.Elapsed.TotalSeconds -lt 5)
  if(-not $ui){throw 'Status did not open promptly during active worker'}
  $null=$ui.Handle;$owned.Add($ui)
  Wait-Code $worker $expected
  if($ui.HasExited){throw 'Service-facing completion closed on-demand Status'}
  Write-Output "PASS worker-first Status opens promptly; service-facing invocation exits actual $expected while Status survives"
  if($expected -eq 65){if($discord.HasExited){throw 'Deferred fixture exited before result assertion'};if(-not $discord.WaitForExit(35000)){throw 'Discord fixture did not exit naturally'}}
  $next=Start-Owned $exe @('--worker','--data-dir',$data,'--discord-root',$root);Wait-Code $next 0
  if($ui.HasExited){throw 'Subsequent batch closed Status'}
  if(-not $ui.CloseMainWindow()){throw 'Status close request failed'};Wait-Code $ui 0
  Write-Output 'PASS promoted Status accepts subsequent UI-first work and closes normally without watchdog intervention'
 } finally {
  $stop=Start-Owned $exe @('--stop','--data-dir',$data);Wait-Code $stop 0 45000
  foreach($p in $owned){if(-not $p.WaitForExit(35000)){throw "Owned fixture remains $($p.Id); retain data"};$p.Dispose()}
  # Exact copied image is unique to this fixture; child hosts must also drain.
  foreach($p in @(Get-Process -Name VencordAutoUpdate -ErrorAction SilentlyContinue)) {
   try {if($p.Path -eq $exe -and -not $p.WaitForExit(5000)){throw 'Owned host remained after orderly stop'}} finally {$p.Dispose()}
  }
  $fixture.Dispose()
 }
}
Write-Output 'PASS both worker-first result paths cleaned all owned processes and fixtures'
