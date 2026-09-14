param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw '.NET Framework C# compiler not found. Install .NET Framework 4.8.' }
$out = Join-Path $root 'artifacts'
[void](New-Item -ItemType Directory -Force -Path $out)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Core') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Protocol') -Filter *.cs | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Runtime') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
if ($Tests) {
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter *.cs | ForEach-Object { $_.FullName })
    $target = 'exe'; $name = 'CoreTests.exe'
} else {
    $app = @(Get-ChildItem -LiteralPath (Join-Path $root 'src/App') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
    if ($app.Count -gt 0) { $sources += @($app | Where-Object { $_ -notin $sources }); $target = 'winexe'; $name = 'VencordAutoUpdate.exe' }
    else { $target = 'library'; $name = 'VencordAutoUpdate.Core.dll' }
}
$extra = @()
if (-not $Tests) { $extra += "/win32manifest:$(Join-Path $root 'src/App/app.manifest')" }
& $compiler @extra /nologo /warn:4 /warnaserror+ /langversion:5 /optimize+ "/target:$target" "/out:$(Join-Path $out $name)" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $name"

if (-not $Tests) {
    $service = @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Service') -Filter *.cs | ForEach-Object { $_.FullName })
    $service += @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Protocol') -Filter *.cs | ForEach-Object { $_.FullName })
    & $compiler /nologo /warn:4 /warnaserror+ /langversion:5 /optimize+ /target:exe "/out:$(Join-Path $out 'VencordAutoUpdate.Service.exe')" /r:System.dll /r:System.Core.dll /r:System.ServiceProcess.dll $service
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host 'Built VencordAutoUpdate.Service.exe'
}
