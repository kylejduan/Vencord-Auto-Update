$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
# Load by path. Loading an executable from a byte array is a fileless-malware pattern that
# antivirus products (for example Bitdefender) block. No later test in this shell rebuilds it.
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'artifacts\VencordAutoUpdate.exe'))
$type=$assembly.GetType('VencordAutoUpdate.WorkerApplication',$true)
$method=$type.GetMethod('SetupStartInfo',[Reflection.BindingFlags]'NonPublic,Static')
if (-not $method) { throw 'FAIL setup has no testable exact UAC launch boundary' }
# Supply the real script path as a reflection argument to the internal encoder, never production CLI.
$script=Join-Path $repo "scripts/install.ps1"
$info=$method.Invoke($null,[object[]]@([string]$script))
if ($info.Verb -cne 'runas' -or -not $info.UseShellExecute) { throw 'FAIL explicit UAC ShellExecute boundary missing' }
if ($info.FileName -ne (Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe')) { throw 'FAIL setup did not select system PowerShell' }
# Compare by encoding the expected literal. Base64 is one-to-one, so this proves the exact
# command without decoding one: decoding encoded PowerShell is quarantined by antivirus heuristics.
function Get-ExpectedArgument([string]$Literal) { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Literal)) }
if (($info.Arguments -split ' ')[-1] -cne (Get-ExpectedArgument ("& '"+$script.Replace("'","''")+"'"))) { throw 'FAIL setup command is not the exact script literal' }
$info=$method.Invoke($null,[object[]]@([string]"C:\fixture's source\install.ps1"))
if (($info.Arguments -split ' ')[-1] -cne (Get-ExpectedArgument "& 'C:\fixture''s source\install.ps1'")) { throw 'FAIL setup apostrophe escaping' }
Write-Output 'PASS exact system PowerShell/UAC/encoded script literal; no process started or elevated'
