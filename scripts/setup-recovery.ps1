# Recovery is machine-owned and never parses a user's state/cache/journals.
function New-SetupRecoveryRecord($Paths,[string]$Stage,[string]$Operation,$Old,$Service,[bool]$Shortcut,$Sources) {
    # An empty subexpression becomes {} in Windows PowerShell JSON and reads
    # back as a truthy object. Missing snapshot hashes must be explicit nulls.
    @{ Schema=1; Destination=$Paths.Destination; ServiceName=$Paths.ServiceName; ShortcutPath=$Paths.ShortcutPath; HadOld=[bool]$Old; HadService=[bool]$Service; WasRunning=[bool]($Service -and $Service.State -eq 4); Operation=$Operation;
        OldManifestHash=$(if ($Old) { Get-SetupHash (Join-Path $Stage 'old\installation.json') } else { $null }); NewManifestHash=$(if ($Sources) { Get-SetupHash (Join-Path $Stage 'new\installation.json') } else { $null });
        OldShortcutHash=$(if ($Shortcut) { Get-SetupHash (Join-Path $Stage 'old.lnk') } else { $null }); NewShortcutHash=$(if ($Sources) { Get-SetupHash (Join-Path $Stage 'new.lnk') } else { $null }) }
}
function New-SetupRecovery($Paths,$Sources,[string]$Operation) {
    if (Test-Path -LiteralPath $Paths.Recovery) { throw 'Recovery already exists; restore it first.' }
    $old=Read-SetupManifest $Paths
    $service=Assert-SetupService $Paths
    $shortcut=Assert-SetupShortcut $Paths $(if ($old) { $old.ShortcutSha256 })
    if ($old -and -not $shortcut) { throw 'Owned shortcut is missing; retain installation evidence.' }
    Assert-NoSetupTask $Paths
    if (-not $old) {
        if ($service -or $shortcut) { throw 'Service/shortcut exists without the machine installation manifest.' }
        foreach ($name in Get-OwnedFileNames) { if (Test-Path -LiteralPath (Join-Path $Paths.Destination $name)) { throw "Foreign destination file exists: $name" } }
    }
    if ($Operation -eq 'uninstall' -and -not $old) { return $false }
    [VencordSetup.Paths]::CreateDirectory($Paths.Destination)
    $stage=Join-Path $Paths.ControlRoot ('staging-'+[Guid]::NewGuid().ToString('N'))
    [VencordSetup.Paths]::CreateDirectory($stage)
    foreach ($set in @('old','new')) { [VencordSetup.Paths]::CreateDirectory((Join-Path $stage $set)) }
    if ($old) {
        foreach ($name in @((Get-OwnedFileNames))+'installation.json') { Copy-SetupDurable (Join-Path $Paths.Destination $name) (Join-Path $stage "old\$name") }
        [void](Read-SetupManifest $Paths (Join-Path $stage 'old'))
    }
    if ($Sources) {
        foreach ($name in Get-OwnedFileNames) { Copy-SetupDurable $Sources[$name] (Join-Path $stage "new\$name") }
        New-SetupShortcut $Paths (Join-Path $stage 'new.lnk')
        Write-SetupDurable (Join-Path $stage 'new\installation.json') (ConvertTo-Json (New-SetupManifest $Paths (Join-Path $stage 'new') (Get-SetupHash (Join-Path $stage 'new.lnk'))) -Depth 5)
        [void](Read-SetupManifest $Paths (Join-Path $stage 'new'))
    }
    if ($shortcut) { Copy-SetupDurable $Paths.ShortcutPath (Join-Path $stage 'old.lnk') }
    $record=New-SetupRecoveryRecord $Paths $stage $Operation $old $service $shortcut $Sources
    Write-SetupDurable (Join-Path $stage 'record.json') (ConvertTo-Json $record -Depth 5)
    # No active mutation before complete snapshots and ownership revalidation.
    [void](Read-SetupManifest $Paths); [void](Assert-SetupService $Paths); [void](Assert-SetupShortcut $Paths)
    [IO.Directory]::Move($stage,$Paths.Recovery)
    [void](Read-SetupRecovery $Paths)
    return $true
}
function Read-SetupRecovery($Paths) {
    [VencordSetup.Paths]::Protected($Paths.Recovery)
    if (-not (Test-Path -LiteralPath $Paths.Recovery)) { return $null }
    $root=$Paths.Recovery; $recordPath=Join-Path $root 'record.json'
    [VencordSetup.Paths]::Protected($recordPath)
    $record=Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    if ($record.Schema -ne 1 -or @($record.PSObject.Properties).Count -ne 12 -or $record.Operation -notin @('install','uninstall') -or $record.HadOld -isnot [bool] -or $record.HadService -isnot [bool] -or $record.WasRunning -isnot [bool]) { throw 'Unknown recovery record; retained.' }
    foreach ($key in @('Destination','ServiceName','ShortcutPath')) { if ($record.$key -cne $Paths[$key]) { throw 'Recovery scope mismatch; retained.' } }
    if (($record.HadService -or $record.OldShortcutHash) -and -not $record.HadOld) { throw 'Recovery prior ownership is inconsistent.' }
    $sets=@{}
    foreach ($set in @('old','new')) {
        $hash=$record.($set+'ManifestHash'); $setRoot=Join-Path $root $set
        if ($hash) { Assert-SetupHash (Join-Path $setRoot 'installation.json') @($hash); $sets[$set]=Read-SetupManifest $Paths $setRoot }
        elseif (@(Get-ChildItem -LiteralPath $setRoot -Force).Count -gt 0) { throw 'Unexpected recovery set; retained.' }
    }
    if ([bool]$sets.old -ne $record.HadOld -or [bool]$sets.new -ne ($record.Operation -eq 'install')) { throw 'Incomplete recovery snapshots; retained.' }
    $allowed=@{}; $allowed['record.json']=@(Get-SetupHash $recordPath)
    foreach ($set in @('old','new')) {
        if ($sets[$set]) {
            foreach ($file in $sets[$set].Files) { $allowed["$set\$($file.Name)"]=@($file.Sha256) }
            $allowed["$set\installation.json"]=@($record.($set+'ManifestHash'))
        }
        $hash=$record.($set+'ShortcutHash')
        if ($sets[$set] -and $sets[$set].ShortcutSha256 -cne $hash) { throw 'Recovery shortcut does not match its installation manifest.' }
        if ($hash) { $allowed["$set.lnk"]=@($hash); Assert-SetupHash (Join-Path $root "$set.lnk") @($hash) }
    }
    if (Test-Path -LiteralPath (Join-Path $root 'committed')) {
        if ((Get-Content -LiteralPath (Join-Path $root 'committed') -Raw) -cne $allowed['record.json'][0]) { throw 'Invalid recovery commit marker; retained.' }
        $allowed['committed']=@(Get-SetupHash (Join-Path $root 'committed'))
    }
    $inventory=@()
    foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        [VencordSetup.Paths]::Protected($item.FullName)
        $relative=$item.FullName.Substring($root.Length+1)
        if ($item.PSIsContainer) { if ($relative -notin @('old','new')) { throw 'Unknown recovery directory; retained.' }; continue }
        if (-not $allowed.ContainsKey($relative)) {
            if ($relative -cmatch '^transfer-[0-9a-f]{32}$') { continue }
            throw 'Unknown recovery file; retained.'
        }
        Assert-SetupHash $item.FullName $allowed[$relative]
        $inventory+=@{ Relative=$relative; Hash=(Get-SetupHash $item.FullName) }
    }
    foreach ($name in @((Get-OwnedFileNames))+'installation.json') {
        $hashes=@(foreach ($set in @('old','new')) { if ($sets[$set]) { if ($name -eq 'installation.json') { $record.($set+'ManifestHash') } else { ($sets[$set].Files | Where-Object Name -CEQ $name).Sha256 } } })
        Assert-SetupHash (Join-Path $Paths.Destination $name) $hashes -Optional
    }
    $shortcut=Assert-SetupShortcut $Paths
    if ($shortcut) { Assert-SetupHash $Paths.ShortcutPath @($record.OldShortcutHash,$record.NewShortcutHash) }
    Assert-NoSetupTask $Paths
    $service=Assert-SetupService $Paths
    return @{ Record=$record; Old=$sets.old; New=$sets.new; Inventory=$inventory; Service=$service; Committed=$allowed.ContainsKey('committed') }
}
function Complete-SetupRecovery($Paths) {
    $recovery=Read-SetupRecovery $Paths
    $retired=Join-Path $Paths.ControlRoot ('completed-'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::Move($Paths.Recovery,$retired)
    try {
        foreach ($file in $recovery.Inventory) { $path=Join-Path $retired $file.Relative; Assert-SetupHash $path @($file.Hash); Remove-Item -LiteralPath $path }
        foreach ($set in @('old','new')) { [IO.Directory]::Delete((Join-Path $retired $set)) }
        [IO.Directory]::Delete($retired)
    } catch { Write-Warning "Completed setup evidence retained at $retired. Cleanup did not finish: $_" }
}
function Restore-SetupRecovery($Paths) {
    Assert-SetupDirectory $Paths
    $r=Read-SetupRecovery $Paths
    if (-not $r) { return }
    if ($r.Committed) { Complete-SetupRecovery $Paths; return }
    Stop-SetupService $Paths; Wait-SetupExecutables $Paths
    $r=Read-SetupRecovery $Paths
    if (-not $r.Record.HadService -and $r.Service) { Remove-SetupService $Paths }
    foreach ($name in @((Get-OwnedFileNames))+'installation.json') {
        $target=Join-Path $Paths.Destination $name
        $hashes=@(foreach ($set in @('Old','New')) { if ($r[$set]) { if ($name -eq 'installation.json') { $r.Record.($set+'ManifestHash') } else { ($r[$set].Files | Where-Object Name -CEQ $name).Sha256 } } })
        if ($r.Old) { Set-SetupFile (Join-Path $Paths.Recovery "old\$name") $target $Paths.Recovery $hashes }
        elseif (Test-Path -LiteralPath $target) { Assert-SetupHash $target $hashes; Remove-Item -LiteralPath $target }
    }
    if ($r.Record.OldShortcutHash) { Set-SetupFile (Join-Path $Paths.Recovery 'old.lnk') $Paths.ShortcutPath $Paths.Recovery @($r.Record.OldShortcutHash,$r.Record.NewShortcutHash) }
    elseif (Test-Path -LiteralPath $Paths.ShortcutPath) { Assert-SetupHash $Paths.ShortcutPath @($r.Record.NewShortcutHash); Remove-Item -LiteralPath $Paths.ShortcutPath }
    if ($r.Record.HadService -and -not (Assert-SetupService $Paths)) { [VencordSetup.Scm]::Create($Paths.ServiceName,('"'+$Paths.ServiceExecutable+'"'),(Get-SetupDescription $Paths)) }
    if ($r.Record.WasRunning) { Start-SetupService $Paths }
    if ($r.Old) { [void](Read-SetupManifest $Paths) }
    Complete-SetupRecovery $Paths
    Write-Output 'Restored the verified previous machine installation. User data was not opened.'
}
