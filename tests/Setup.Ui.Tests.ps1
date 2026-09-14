$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $repo 'artifacts/VencordAutoUpdate.exe')))
$type=$assembly.GetType('VencordAutoUpdate.WorkerApplication',$true)
$method=$type.GetMethod('SetupStartInfo',[Reflection.BindingFlags]'NonPublic,Static')
if (-not $method) { throw 'FAIL setup has no testable exact UAC launch boundary' }
# Loaded-as-bytes BaseDirectory is PowerShell's directory. Supply the real script
# path as a reflection argument to the internal encoder, never production CLI.
$script=Join-Path $repo "scripts/install.ps1"
$info=$method.Invoke($null,[object[]]@([string]$script))
if ($info.Verb -cne 'runas' -or -not $info.UseShellExecute) { throw 'FAIL explicit UAC ShellExecute boundary missing' }
if ($info.FileName -ne (Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe')) { throw 'FAIL setup did not select system PowerShell' }
$encoded=($info.Arguments -split ' ')[-1]
$command=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded))
if ($command -cne ("& '"+$script.Replace("'","''")+"'")) { throw 'FAIL setup command is not the exact script literal' }
$info=$method.Invoke($null,[object[]]@([string]"C:\fixture's source\install.ps1"))
$command=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String(($info.Arguments -split ' ')[-1]))
if ($command -cne "& 'C:\fixture''s source\install.ps1'") { throw 'FAIL setup apostrophe escaping' }
Write-Output 'PASS exact system PowerShell/UAC/encoded script literal; no process started or elevated'
