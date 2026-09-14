param([switch]$RequireAdmin)
. (Join-Path $PSScriptRoot 'Setup.Fixture.ps1')
$permission=@(Test-ScmPermission $RequireAdmin); $permission | Where-Object { $_ -is [string] } | Write-Output
if ($permission[-1] -ne $true) { return }
$f=New-MachineFixture
try {
    $lock=Enter-SetupLock $f.Paths
    try { Refuses { Enter-SetupLock $f.Paths } 'machine lock refuses concurrent setup independent of installer SID'; [VencordSetup.Paths]::Protected($f.Paths.Lock) }
    finally { Exit-SetupLock $lock }
    [VencordSetup.Paths]::CreateDirectory($f.Paths.Destination)
    $foreign=Join-Path $f.Paths.Destination 'VencordAutoUpdate.exe'
    Write-SetupDurable $foreign 'foreign executable must never execute'
    Refuses { Install-Fixture $f } 'fresh install refuses a foreign owned-name file'
    Check ((Get-Content -LiteralPath $foreign -Raw) -ceq 'foreign executable must never execute') 'foreign file bytes preserved'
    Remove-Item -LiteralPath $foreign
    $taskName=$f.Paths.ServiceName
    $action=New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\cmd.exe') -Argument '/c exit 0'
    Register-ScheduledTask -TaskName $taskName -Action $action | Out-Null
    try { Refuses { Install-Fixture $f } 'unexpected task is refused without legacy migration'; Check ([bool](Get-ScheduledTask -TaskName $taskName)) 'foreign task remains' }
    finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    # Real SCM foreign registration, deliberately never started.
    New-Service -Name $f.Paths.ServiceName -BinaryPathName '"C:\Windows\System32\cmd.exe" /c exit 0' -StartupType Manual | Out-Null
    try { Refuses { Install-Fixture $f } 'foreign service ImagePath/configuration is refused'; Check ([bool][VencordSetup.Scm]::Read($f.Paths.ServiceName)) 'foreign service preserved' }
    finally { [VencordSetup.Scm]::Delete($f.Paths.ServiceName) }
    Install-Fixture $f
    $beforeSidecar=Assert-SetupService $f.Paths
    $beforeManifest=Get-SetupHash $f.Paths.Manifest
    $sidecar=Join-Path $f.Paths.Destination 'VencordAutoUpdate.Service.exe.config'
    Write-SetupDurable $sidecar '<configuration><runtime><assemblyBinding /></runtime></configuration>'
    try {
        Refuses { Install-Fixture $f } 'unowned CLR configuration is refused even with protected ACLs'
        Check (Test-Path -LiteralPath $sidecar) 'unsafe foreign runtime sidecar is retained without execution'
        $afterSidecar=Assert-SetupService $f.Paths
        Check ($afterSidecar.State -eq 4 -and $afterSidecar.Pid -eq $beforeSidecar.Pid) 'sidecar refusal occurs before service stop or restart'
        Check ((Get-SetupHash $f.Paths.Manifest) -eq $beforeManifest) 'sidecar refusal leaves installation manifest unchanged'
        [void](Read-SetupManifest $f.Paths)
    } finally { Remove-Item -LiteralPath $sidecar }
    $neighbor=Join-Path $f.Paths.Destination 'neighbor.txt'; Write-SetupDurable $neighbor 'foreign non-runtime text'
    $neighborAcl=Get-Acl -LiteralPath $neighbor
    try {
        $writable=Get-Acl -LiteralPath $neighbor
        $writable.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $neighbor -AclObject $writable
        Refuses { Install-Fixture $f } 'user-writable foreign descendant blocks privileged setup'
    } finally { Set-Acl -LiteralPath $neighbor -AclObject $neighborAcl }
    Install-Fixture $f
    Check ((Get-Content -LiteralPath $neighbor -Raw) -ceq 'foreign non-runtime text') 'protected non-runtime foreign neighbor is preserved'
    $manifest=[IO.File]::ReadAllBytes($f.Paths.Manifest)
    try { [IO.File]::WriteAllText($f.Paths.Manifest,'{"Schema":1}'); Refuses { Uninstall-Fixture $f } 'unknown machine manifest is preserved' }
    finally { [IO.File]::WriteAllBytes($f.Paths.Manifest,$manifest) }
    $shortcut=[IO.File]::ReadAllBytes($f.Paths.ShortcutPath)
    try {
        $shell=New-Object -ComObject WScript.Shell; $link=$shell.CreateShortcut($f.Paths.ShortcutPath); $link.Arguments='--worker'; $link.Save()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        Refuses { Uninstall-Fixture $f } 'changed shortcut arguments are refused'
    } finally { [IO.File]::WriteAllBytes($f.Paths.ShortcutPath,$shortcut) }
    $readme=Join-Path $f.Paths.Destination 'README.md'; $original=[IO.File]::ReadAllBytes($readme)
    try { [IO.File]::WriteAllText($readme,'modified'); Refuses { Install-Fixture $f } 'modified manifest-owned bytes are refused' }
    finally { [IO.File]::WriteAllBytes($readme,$original) }
    $acl=Get-Acl -LiteralPath $f.Paths.Destination
    try {
        $changed=Get-Acl -LiteralPath $f.Paths.Destination
        $changed.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $f.Paths.Destination -AclObject $changed
        Refuses { Install-Fixture $f } 'effective writable destination ACL blocks setup'
    } finally { Set-Acl -LiteralPath $f.Paths.Destination -AclObject $acl }
    # Mapped EXE / open status protection is independent of SCM worker ownership.
    Stop-SetupService $f.Paths
    $held=[IO.File]::Open($f.Paths.Executable,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { Refuses { Wait-SetupExecutables $f.Paths } 'held user EXE blocks replacement with close-Status guidance' }
    finally { $held.Dispose() }
    Wait-SetupExecutables $f.Paths
    $data=Join-Path $f.Root 'simulated-user-evidence'; [VencordSetup.Paths]::CreateDirectory($data)
    Write-SetupDurable (Join-Path $data 'state.json') 'unknown untrusted user policy'
    Write-SetupDurable (Join-Path $data 'pending.journal') 'pending evidence'
    Uninstall-Fixture $f
    Check ((Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw) -ceq 'unknown untrusted user policy') 'uninstall never parses user state'
    Check ((Get-Content -LiteralPath (Join-Path $data 'pending.journal') -Raw) -ceq 'pending evidence') 'uninstall preserves pending user evidence'
} finally { Remove-MachineFixture $f }
