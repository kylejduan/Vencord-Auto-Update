param(
    [string]$OutputDirectory,
    [string]$Version = '0.1.0',
    [switch]$RequireClean,
    [switch]$RequireTag
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Application build failed; package was not produced.' }
$exe = Join-Path $repo 'artifacts/VencordAutoUpdate.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build scripts/build.ps1 before packaging.' }
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$builtVersion = '{0}.{1}.{2}' -f $fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart
if ($builtVersion -ne $Version) { throw "Built executable version $builtVersion does not match package version $Version." }
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    $sourceRoot = $repo
} else {
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if (-not $wsl) { throw 'Git is required for source provenance.' }
    $sourceRoot = (& $wsl.Source --exec wslpath -u $repo).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $sourceRoot) { throw 'Cannot resolve source checkout for Git.' }
}
function Invoke-SourceGit([string[]]$Arguments) {
    if ($git) { return & $git.Source -C $sourceRoot @Arguments }
    return & $wsl.Source --exec git -C $sourceRoot @Arguments
}
$commit = (Invoke-SourceGit @('rev-parse', 'HEAD')).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify source commit.' }
$status = @(Invoke-SourceGit @('status', '--porcelain', '--untracked-files=normal'))
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source status.' }
$dirty = $status.Count -gt 0
if ($RequireClean -and $dirty) { throw 'Release packaging requires a clean source checkout.' }
$tag = "v$Version"
if ($RequireTag) {
    $head = (Invoke-SourceGit @('rev-list', '-n', '1', $tag)).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $commit) { throw "Release tag $tag must point to the source commit." }
}
# Share the exact installed inventory; SOURCE is package provenance only.
. (Join-Path $PSScriptRoot 'setup-common.ps1')
$files = Get-SetupSources (Join-Path $repo 'artifacts')
foreach ($name in $files.Keys) {
    if (-not (Test-Path -LiteralPath $files[$name] -PathType Leaf)) { throw "Package input missing: $name" }
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$stage = Join-Path $OutputDirectory ('package-' + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $OutputDirectory "VencordAutoUpdate-$Version-windows.zip"
try {
    [void][IO.Directory]::CreateDirectory($stage)
    foreach ($name in $files.Keys) {
        Copy-Item -LiteralPath $files[$name] -Destination (Join-Path $stage $name)
    }
    $source = "Version: $Version`nCommit: $commit`nDirty: $($dirty.ToString().ToLowerInvariant())`n"
    [IO.File]::WriteAllText((Join-Path $stage 'SOURCE.txt'), $source, [Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS'), "$hash  VencordAutoUpdate-$Version-windows.zip`n", [Text.ASCIIEncoding]::new())
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
Write-Host "Packaged VencordAutoUpdate-$Version-windows.zip and SHA256SUMS from $commit (dirty: $dirty)."
