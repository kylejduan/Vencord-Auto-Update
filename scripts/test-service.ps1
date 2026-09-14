$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'Install .NET Framework 4.8 to compile the service harness.' }
$out = Join-Path $root 'artifacts'
[void](New-Item -ItemType Directory -Force -Path $out)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Service') -Filter *.cs | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Protocol') -Filter *.cs | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'tests/Service') -Filter *.cs | ForEach-Object { $_.FullName })
& $compiler /nologo /warn:4 /warnaserror+ /langversion:5 /optimize+ /target:exe /main:VencordAutoUpdate.ServiceTests "/out:$(Join-Path $out 'ServiceTests.exe')" /r:System.dll /r:System.Core.dll /r:System.ServiceProcess.dll $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $out 'ServiceTests.exe')
exit $LASTEXITCODE
