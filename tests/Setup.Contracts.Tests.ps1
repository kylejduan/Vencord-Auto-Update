$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/setup-common.ps1')
function Check($condition, $message) { if (-not $condition) { throw "FAIL $message" }; Write-Output "PASS $message" }
function Refuses([scriptblock]$action, [string]$message) { $refused = $false; try { & $action } catch { $refused = $true }; Check $refused $message }
$defaults = Resolve-SetupPaths @{}
$native = if ($env:ProgramW6432) { $env:ProgramW6432 } else { [Environment]::GetFolderPath('ProgramFiles') }
Check ($defaults.Destination -eq (Join-Path $native 'VencordAutoUpdate')) 'machine installation defaults to native Program Files'
Check ($defaults.ServiceName -eq 'VencordAutoUpdate') 'fixed production SCM service name'
Check (-not $defaults.ContainsKey('DataDirectory')) 'privileged setup has no user data authority'
Refuses { Resolve-SetupPaths @{ Destination = (Join-Path $env:LOCALAPPDATA 'foreign') } } 'partial destination override refused'
Refuses { Resolve-SetupPaths @{ DataDirectory = 'C:\foreign' } } 'legacy user-data override refused'
$id = [Guid]::NewGuid().ToString('N')
$root = Join-Path $native "VencordAutoUpdate.Tests\$id"
$fixture = @{ FixtureRoot=$root; Destination=(Join-Path $root 'Application With Spaces'); ServiceName="VencordAutoUpdate-Test-$id"; ShortcutPath=(Join-Path $root 'Status.lnk') }
$p = Resolve-SetupPaths $fixture
Check ($p.Destination -eq $fixture.Destination) 'complete protected fixture is accepted without mutation'
$bad = $fixture.Clone(); $bad.FixtureRoot = Join-Path $env:LOCALAPPDATA $id
Refuses { Resolve-SetupPaths $bad } 'user-controlled fixture root refused'
$bad = $fixture.Clone(); $bad.ServiceName = 'Spooler'
Refuses { Resolve-SetupPaths $bad } 'foreign service fixture refused'
$bad = $fixture.Clone(); $bad.ShortcutPath = Join-Path $root '..\neighbor.lnk'
Refuses { Resolve-SetupPaths $bad } 'fixture shortcut escape refused'
$expected = @('VencordAutoUpdate.exe','VencordAutoUpdate.Service.exe','install.ps1','uninstall.ps1','setup-common.ps1','setup-native.ps1','setup-files.ps1','setup-service.ps1','setup-recovery.ps1','README.md','LICENSE','CHANGELOG.md','CONTRIBUTING.md','SECURITY.md')
Check (-not (Compare-Object (Get-OwnedFileNames | Sort-Object) ($expected | Sort-Object))) 'exact two-binary setup module inventory'
$identity = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $bindingRefused=$false
    try { & (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/install.ps1') -DataDirectory 'C:\unknown-legacy-data' } catch { $bindingRefused=$_.FullyQualifiedErrorId -match 'NamedParameterNotFound' }
    Check $bindingRefused 'legacy install parameter is rejected by binding, never silently routed to default setup'
    Refuses { Assert-SetupAdministrator } 'ordinary user setup requires explicit UAC elevation'
    Refuses { & (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/install.ps1') } 'install entrypoint refuses ordinary user before target mutation'
    Refuses { & (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/uninstall.ps1') } 'uninstall entrypoint refuses ordinary user before target mutation'
}
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.AccessControlType]::Allow))
Refuses { [VencordSetup.Paths]::CheckAcl($acl,$true) } 'ordinary-user target modify ACL refused'
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles,[Security.AccessControl.AccessControlType]::Allow))
Refuses { [VencordSetup.Paths]::CheckAcl($acl,$false) } 'ancestor delete-child ACL refused'
Check (-not (Test-Path -LiteralPath $root)) 'contract tests create no machine installation'
# Exact QueryServiceObjectSecurity readback from a Windows CI run.
# Exercise the same binary descriptor round trip used by setup, without SCM I/O.
$scmReadback='O:BAG:BAD:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;BU)'
$security=[Security.AccessControl.RawSecurityDescriptor]::new([VencordSetup.Scm]::Security)
$securityBytes=New-Object byte[] $security.BinaryLength
$security.GetBinaryForm($securityBytes,0)
$security=[Security.AccessControl.RawSecurityDescriptor]::new($securityBytes,0)
$securityText=$security.GetSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access')
Check ($securityText -ceq $scmReadback -and [VencordSetup.Scm]::CanonicalSecurity([VencordSetup.Scm]::Security) -ceq $scmReadback) 'created service descriptor and strict expected descriptor match actual Windows readback'
Check ($security.Owner.Value -ceq 'S-1-5-32-544' -and $security.Group.Value -ceq 'S-1-5-32-544') 'service descriptor retains Administrators owner and group'
$usersAce=$security.DiscretionaryAcl[2]
Check ($usersAce.SecurityIdentifier.Value -ceq 'S-1-5-32-545' -and $usersAce.AccessMask -eq 0x2018d) 'service descriptor retains exact Builtin Users access'
# Users must not gain change-config, start, stop, pause, delete, DACL or owner writes.
Check (($usersAce.AccessMask -band 0xd0072) -eq 0) 'Builtin Users cannot mutate service configuration, lifecycle or security'
# Exercise the production recovery record's JSON boundary using only owned
# ordinary files. No protected path, SCM, shortcut COM or setup action is invoked.
$stage=Join-Path $env:LOCALAPPDATA ('VencordAutoUpdate-RecordTest-'+[Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory($stage)
    foreach ($set in @('old','new')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $stage $set))
        [IO.File]::WriteAllText((Join-Path $stage "$set\installation.json"),'abc',[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $stage "$set.lnk"),'',[Text.UTF8Encoding]::new($false))
    }
    $manifestHash='BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD'
    $shortcutHash='E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855'
    $old=@{ Schema=3 }; $running=@{ State=4 }; $sources=@{ Present=$true }
    $cases=@(
        @{ Name='first install'; Operation='install'; Old=$null; Service=$null; Shortcut=$false; Sources=$sources; HadOld=$false; HadService=$false; WasRunning=$false; HasNew=$true },
        @{ Name='running upgrade'; Operation='install'; Old=$old; Service=$running; Shortcut=$true; Sources=$sources; HadOld=$true; HadService=$true; WasRunning=$true; HasNew=$true },
        @{ Name='stopped uninstall'; Operation='uninstall'; Old=$old; Service=@{ State=1 }; Shortcut=$true; Sources=$null; HadOld=$true; HadService=$true; WasRunning=$false; HasNew=$false },
        @{ Name='missing service repair'; Operation='install'; Old=$old; Service=$null; Shortcut=$true; Sources=$sources; HadOld=$true; HadService=$false; WasRunning=$false; HasNew=$true }
    )
    foreach ($case in $cases) {
        $record=New-SetupRecoveryRecord $p $stage $case.Operation $case.Old $case.Service $case.Shortcut $case.Sources
        $json=ConvertTo-Json $record -Depth 5
        $decoded=$json | ConvertFrom-Json
        Check ($decoded.Schema -eq 1 -and @($decoded.PSObject.Properties).Count -eq 12 -and $decoded.Operation -ceq $case.Operation) "$($case.Name): complete recovery record survives JSON"
        foreach ($key in @('Destination','ServiceName','ShortcutPath')) { Check ($decoded.$key -ceq $p[$key]) "$($case.Name): $key scope survives JSON" }
        foreach ($key in @('HadOld','HadService','WasRunning')) { Check ($decoded.$key -is [bool] -and $decoded.$key -eq $case[$key]) "$($case.Name): $key remains the correct boolean" }
        foreach ($set in @('Old','New')) {
            $present=if ($set -eq 'Old') { $case.HadOld } else { $case.HasNew }
            foreach ($kind in @('Manifest','Shortcut')) {
                $key=$set+$kind+'Hash'
                if ($present) {
                    $expectedHash=if ($kind -eq 'Manifest') { $manifestHash } else { $shortcutHash }
                    Check ($decoded.$key -is [string] -and $decoded.$key -ceq $expectedHash) "$($case.Name): $key identifies exact fixture bytes"
                } else {
                    Check ($null -eq $decoded.$key) "$($case.Name): absent $key remains null after JSON, never a truthy ownership claim"
                }
            }
        }
    }
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
Check (-not (Test-Path -LiteralPath $stage)) 'ordinary recovery record fixture cleaned'
# Exercise the actual production CLR invocation on ordinary files. Full
# Set-SetupFile ownership/ACL enforcement and SCM acceptance require admin CI;
# do not stub those protections or copy the replacement expression here.
$replaceCalls=@((Get-Command Set-SetupFile).ScriptBlock.Ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
        $node.Static -and $node.Expression.TypeName.FullName -eq 'IO.File' -and $node.Member.Value -eq 'Replace'
},$true))
Check ($replaceCalls.Count -eq 1) 'replacement boundary is uniquely identified in production setup'
$replace=[scriptblock]::Create($replaceCalls[0].Extent.Text)
$stage=Join-Path $env:LOCALAPPDATA ('VencordAutoUpdate-ReplaceTest-'+[Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory($stage)
    $Target=Join-Path $stage 'owned target'; $temporary=Join-Path $stage 'transfer'
    # Production targets have explicit protected DACLs. Give only these owned
    # ordinary fixtures deterministic ACLs instead of inheriting host policy.
    $fixtureIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try { $fixtureOwner=$fixtureIdentity.User } finally { $fixtureIdentity.Dispose() }
    $systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $targetAcl=[Security.AccessControl.FileSecurity]::new()
    $targetAcl.SetOwner($fixtureOwner); $targetAcl.SetAccessRuleProtection($true,$false)
    $targetAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($fixtureOwner,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
    $targetAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($systemSid,[Security.AccessControl.FileSystemRights]::Read,[Security.AccessControl.AccessControlType]::Allow))
    [IO.File]::WriteAllText($Target,'old installation bytes')
    [IO.File]::SetAccessControl($Target,$targetAcl)
    [IO.File]::SetCreationTimeUtc($Target,[datetime]::new(2020,1,2,3,4,5,[DateTimeKind]::Utc))
    $creation=[IO.File]::GetCreationTimeUtc($Target)
    $security=[IO.File]::GetAccessControl($Target)
    Check ($security.AreAccessRulesProtected -and $security.GetOwner([Security.Principal.SecurityIdentifier]) -eq $fixtureOwner) 'ordinary target has explicit protected ACL and current-user owner'
    $descriptor=[Security.AccessControl.RawSecurityDescriptor]::new($security.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access'))
    $dacl=New-Object byte[] $descriptor.DiscretionaryAcl.BinaryLength
    $descriptor.DiscretionaryAcl.GetBinaryForm($dacl,0)
    foreach ($bytes in @('new installation bytes','old installation bytes')) {
        [IO.File]::WriteAllText($temporary,$bytes)
        # FileSecurity tracks persisted sections, so each new file needs a
        # freshly configured descriptor rather than a previously saved object.
        $transferAcl=[Security.AccessControl.FileSecurity]::new()
        $transferAcl.SetOwner($fixtureOwner); $transferAcl.SetAccessRuleProtection($true,$false)
        $transferAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($fixtureOwner,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
        $transferAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($systemSid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
        [IO.File]::SetAccessControl($temporary,$transferAcl)
        $sourceSecurity=[IO.File]::GetAccessControl($temporary)
        Check ($sourceSecurity.AreAccessRulesProtected -and $sourceSecurity.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) -cne $security.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)) 'ordinary transfer has deliberately different protected DACL'
        [IO.File]::SetCreationTimeUtc($temporary,[datetime]::new(2021,1,2,3,4,5,[DateTimeKind]::Utc))
        & $replace
        Check ([IO.File]::ReadAllText($Target) -ceq $bytes) 'production replacement installs exact upgrade or rollback bytes'
        Check (-not [IO.File]::Exists($temporary)) 'production replacement consumes transfer file'
        Check ([IO.File]::GetCreationTimeUtc($Target) -eq $creation) 'production replacement preserves target creation time'
        $currentSecurity=[IO.File]::GetAccessControl($Target)
        $currentDescriptor=[Security.AccessControl.RawSecurityDescriptor]::new($currentSecurity.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access'))
        $currentDacl=New-Object byte[] $currentDescriptor.DiscretionaryAcl.BinaryLength
        $currentDescriptor.DiscretionaryAcl.GetBinaryForm($currentDacl,0)
        if ([Convert]::ToBase64String($currentDacl) -cne [Convert]::ToBase64String($dacl) -or $currentSecurity.AreAccessRulesProtected -ne $security.AreAccessRulesProtected) {
            Write-Output "Replacement target before: $($security.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access'))"
            Write-Output "Replacement transfer before: $($sourceSecurity.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access'))"
            Write-Output "Replacement target after: $($currentSecurity.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access'))"
        }
        Check ($currentDescriptor.Owner -eq $descriptor.Owner -and $currentDescriptor.Group -eq $descriptor.Group) 'production replacement preserves target owner and group'
        Check ([Convert]::ToBase64String($currentDacl) -ceq [Convert]::ToBase64String($dacl) -and $currentSecurity.AreAccessRulesProtected -eq $security.AreAccessRulesProtected) 'production replacement preserves exact target DACL entries and protection'
        Check (@([IO.Directory]::GetFiles($stage)).Count -eq 1) 'production replacement leaves no unexpected backup file'
    }
} finally {
    if ([IO.Directory]::Exists($stage)) { [IO.Directory]::Delete($stage,$true) }
    Check (-not [IO.Directory]::Exists($stage)) 'ordinary replacement fixture cleaned'
}
