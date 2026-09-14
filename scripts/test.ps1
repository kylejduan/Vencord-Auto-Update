$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Tests
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/CoreTests.exe')
exit $LASTEXITCODE
