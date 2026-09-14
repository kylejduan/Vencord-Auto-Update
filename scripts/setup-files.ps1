function Assert-SetupDirectory($Paths) {
    [VencordSetup.Paths]::Protected($Paths.Destination)
    if (-not (Test-Path -LiteralPath $Paths.Destination)) { return }
    $owned=@(Get-OwnedFileNames)+'installation.json'
    $pending=[Collections.Generic.Queue[string]]::new(); $pending.Enqueue($Paths.Destination)
    while ($pending.Count -gt 0) {
        $directory=$pending.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            # Verify before descending, so a reparse never expands deletion/load scope.
            [VencordSetup.Paths]::Protected($item.FullName)
            $relative=$item.FullName.Substring($Paths.Destination.Length+1)
            if ($owned -cnotcontains $relative -and ($item.Extension -in @('.exe','.dll','.config','.manifest','.local'))) {
                throw "Unowned runtime sidecar may affect privileged loading; retained without starting service: $relative"
            }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        }
    }
}
function Get-SetupHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-SetupHash([string]$Path, $Hashes, [switch]$Optional) {
    [VencordSetup.Paths]::Protected($Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { if ($Optional) { return }; throw "Owned file missing: $Path" }
    $hash=Get-SetupHash $Path
    if (@($Hashes) -notcontains $hash) { throw "Owned file changed; evidence retained: $Path" }
}
function Write-SetupDurable([string]$Path, [string]$Text) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes($Text)
    $stream=[VencordSetup.Paths]::CreateFile($Path)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function Copy-SetupDurable([string]$Source, [string]$Target) {
    $inputStream=[IO.File]::OpenRead($Source)
    try { $outputStream=[VencordSetup.Paths]::CreateFile($Target)
        try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}
function Get-SetupSources([string]$SourceDirectory) {
    if (-not $SourceDirectory) {
        if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'VencordAutoUpdate.exe')) { $SourceDirectory=$PSScriptRoot }
        else { $SourceDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts' }
    }
    $sourceRoot=if (Test-Path -LiteralPath (Join-Path $SourceDirectory 'README.md')) { $SourceDirectory } else { Split-Path -Parent $SourceDirectory }
    $sources=@{}
    foreach ($name in Get-OwnedFileNames) {
        $root=if ($name.EndsWith('.exe')) { $SourceDirectory } elseif ($name.EndsWith('.ps1')) { $PSScriptRoot } else { $sourceRoot }
        $sources[$name]=Join-Path $root $name
        if (-not (Test-Path -LiteralPath $sources[$name] -PathType Leaf)) { throw "Complete source/release file missing: $name" }
    }
    foreach ($name in @('VencordAutoUpdate.exe','VencordAutoUpdate.Service.exe')) {
        $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($sources[$name])
        if ($version.FileVersion -ne '0.1.0.0') { throw "Unexpected $name version. Build both reviewed binaries before setup." }
    }
    return $sources
}
function New-SetupManifest($Paths, [string]$Root, [string]$ShortcutHash) {
    @{ Schema=3; Product='VencordAutoUpdate'; Destination=$Paths.Destination; ServiceName=$Paths.ServiceName; ShortcutPath=$Paths.ShortcutPath; ShortcutSha256=$ShortcutHash;
        Files=@(foreach ($name in Get-OwnedFileNames) { @{ Name=$name; Sha256=(Get-SetupHash (Join-Path $Root $name)) } }) }
}
function Read-SetupManifest($Paths, [string]$Root=$Paths.Destination) {
    $path=Join-Path $Root 'installation.json'
    [VencordSetup.Paths]::Protected($path)
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $m=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($m.Schema -ne 3 -or $m.Product -cne 'VencordAutoUpdate' -or @($m.PSObject.Properties).Count -ne 7 -or $m.ShortcutSha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'Unknown machine installation manifest; retained.' }
    foreach ($key in @('Destination','ServiceName','ShortcutPath')) { if ($m.$key -cne $Paths[$key]) { throw "Installation manifest mismatch: $key" } }
    $names=@(Get-OwnedFileNames); $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (@($m.Files).Count -ne $names.Count) { throw 'Incomplete installation inventory.' }
    foreach ($file in $m.Files) {
        if ($names -cnotcontains $file.Name -or -not $seen.Add($file.Name) -or $file.Sha256 -cnotmatch '^[0-9A-F]{64}$' -or @($file.PSObject.Properties).Count -ne 2) { throw 'Unknown/duplicate installation file ownership.' }
        Assert-SetupHash (Join-Path $Root $file.Name) @($file.Sha256)
    }
    return $m
}
function Assert-SetupShortcut($Paths, [string]$ExpectedHash) {
    [VencordSetup.Paths]::Protected($Paths.ShortcutPath)
    if (-not (Test-Path -LiteralPath $Paths.ShortcutPath)) { return $false }
    if ($ExpectedHash) { Assert-SetupHash $Paths.ShortcutPath @($ExpectedHash) }
    $shell=New-Object -ComObject WScript.Shell
    try { $link=$shell.CreateShortcut($Paths.ShortcutPath)
        try { if ($link.TargetPath -cne $Paths.Executable -or $link.Arguments -ne '' -or $link.WorkingDirectory -cne $Paths.Destination -or $link.Description -cne 'Vencord Auto Update status and setup') { throw 'Foreign or changed Start menu shortcut; retained.' } }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    return $true
}
function New-SetupShortcut($Paths, [string]$Target) {
    $shell=New-Object -ComObject WScript.Shell
    try { $link=$shell.CreateShortcut($Target)
        try { $link.TargetPath=$Paths.Executable; $link.Arguments=''; $link.WorkingDirectory=$Paths.Destination; $link.Description='Vencord Auto Update status and setup'; $link.Save(); [VencordSetup.Paths]::SecureCreatedShortcut($Target) }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Wait-SetupExecutables($Paths) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $held=@(); $ready=$true
        try {
            foreach ($path in @($Paths.Executable,$Paths.ServiceExecutable)) {
                [VencordSetup.Paths]::Protected($path)
                if (Test-Path -LiteralPath $path) { $held+= [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
            }
        } catch [IO.IOException] { $ready=$false }
        catch [UnauthorizedAccessException] { $ready=$false }
        finally { foreach ($stream in $held) { $stream.Dispose() } }
        if ($ready) { return }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    throw 'Installed executable is in use. Close Status / Setup in every user session, let active repairs finish, then retry. Files and recovery evidence were retained; no process was killed.'
}
function Set-SetupFile([string]$Source,[string]$Target,[string]$Recovery,$AllowedHashes) {
    Assert-SetupHash $Target $AllowedHashes -Optional
    $temporary=Join-Path $Recovery ('transfer-'+[Guid]::NewGuid().ToString('N'))
    Copy-SetupDurable $Source $temporary
    Assert-SetupHash $temporary @((Get-SetupHash $Source))
    Assert-SetupHash $Target $AllowedHashes -Optional
    # PowerShell 5.1 binds $null to an empty string here; Replace needs CLR null
    # to omit the backup path while retaining atomic replacement and metadata.
    if (Test-Path -LiteralPath $Target) { [IO.File]::Replace($temporary,$Target,[System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary,$Target) }
}
