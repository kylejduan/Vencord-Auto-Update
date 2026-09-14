$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$files=@(Get-ChildItem (Join-Path $repo 'scripts') -Filter *.ps1)+@(Get-ChildItem (Join-Path $repo 'tests') -Filter *.ps1)
$failures=@(); $passed=0
foreach ($file in $files) {
    $tokens=$null; $errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if ($errors.Count -gt 0) { $failures+=($file.Name+': '+(($errors | ForEach-Object Message) -join '; ')) } else { $passed++ }
}
Write-Output ("PowerShell parse: $passed/$($files.Count) readable and valid")
if ($failures.Count -gt 0) { throw ($failures -join "`n") }
