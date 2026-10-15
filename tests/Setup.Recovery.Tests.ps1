param([switch]$RequireAdmin)
. (Join-Path $PSScriptRoot 'Setup.Fixture.ps1')
$permission=@(Test-ScmPermission $RequireAdmin); $permission | Where-Object { $_ -is [string] } | Write-Output
if ($permission[-1] -ne $true) { return }
if (-not ('VencordSetupTest.PartialService' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace VencordSetupTest {
// Test-only: the first step of production Scm.Create without its later configuration calls.
public static class PartialService {
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)]static extern IntPtr OpenSCManager(string machine,string database,uint access);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)]static extern IntPtr CreateService(IntPtr scm,string name,string display,uint access,uint type,uint start,uint error,string image,string group,IntPtr tag,string dependencies,string account,string password);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool CloseServiceHandle(IntPtr handle);
    public static void Create(string name,string image) {
        IntPtr scm=OpenSCManager(null,null,3);if(scm==IntPtr.Zero)throw new Win32Exception();
        try{IntPtr h=CreateService(scm,name,name,0xf01ff,0x10,3,1,image,null,IntPtr.Zero,null,"LocalSystem",null);if(h==IntPtr.Zero)throw new Win32Exception();CloseServiceHandle(h);}finally{CloseServiceHandle(scm);}
    }
}
}
'@
}
$f=New-MachineFixture
try {
    Install-Fixture $f
    $oldHash=Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')
    $sources=Get-SetupSources (Join-Path $script:Repo 'artifacts')
    $newReadme=Join-Path $f.Root 'updated-readme.txt'; Write-SetupDurable $newReadme 'test-only next release README'
    $sources['README.md']=$newReadme
    $lock=Enter-SetupLock $f.Paths
    try {
        [void](New-SetupRecovery $f.Paths $sources 'install')
        Stop-SetupService $f.Paths; Wait-SetupExecutables $f.Paths
        $r=Read-SetupRecovery $f.Paths
        Set-SetupFile (Join-Path $f.Paths.Recovery 'new\README.md') (Join-Path $f.Paths.Destination 'README.md') $f.Paths.Recovery @($oldHash,(Get-SetupHash $newReadme))
        Check ((Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')) -ne $oldHash) 'interrupted upgrade actually contains mixed old/new files'
        Restore-SetupRecovery $f.Paths
        Check ((Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')) -eq $oldHash) 'rollback restores exact previous bytes'
        Check ((Assert-SetupService $f.Paths).State -eq 4) 'rollback restores prior running state'
        Check (-not (Test-Path -LiteralPath $f.Paths.Recovery)) 'recovery retired only after verified rollback'
        [void](New-SetupRecovery $f.Paths $sources 'install')
        $target=Join-Path $f.Paths.Destination 'README.md'
        $bytes=[IO.File]::ReadAllBytes($target)
        try { [IO.File]::WriteAllText($target,'unknown concurrent bytes'); Refuses { Restore-SetupRecovery $f.Paths } 'unknown active content blocks rollback without overwrite'; Check ((Get-Content $target -Raw) -ceq 'unknown concurrent bytes') 'ambiguous evidence remains' }
        finally { [IO.File]::WriteAllBytes($target,$bytes) }
        # Simulate a crash during transfer copy: never authoritative, retained inert.
        $partial=Join-Path $f.Paths.Recovery ('transfer-'+[Guid]::NewGuid().ToString('N'))
        Write-SetupDurable $partial 'partial copy'
        Restore-SetupRecovery $f.Paths
        $retained=@(Get-ChildItem -LiteralPath $f.Paths.ControlRoot -Directory -Filter 'completed-*')
        Check ($retained.Count -eq 1) 'incomplete transfer cleanup retains explicit completed evidence'
        Check (@(Get-ChildItem $retained[0].FullName -File).Count -eq 1) 'retained partial transfer never substituted for verified old snapshot'
        # Simulate a crash after CreateService but before its ACL, failure actions and description.
        [void](New-SetupRecovery $f.Paths $null 'uninstall')
        Stop-SetupService $f.Paths; Wait-SetupExecutables $f.Paths
        Remove-SetupService $f.Paths
        [VencordSetupTest.PartialService]::Create($f.Paths.ServiceName,('"'+$f.Paths.ServiceExecutable+'"'))
        Refuses { Assert-SetupService $f.Paths } 'partially created service is never accepted as complete'
        Restore-SetupRecovery $f.Paths
        Check ((Assert-SetupService $f.Paths).State -eq 4) 'interrupted service creation is replaced by the exact owned running service'
        Check (-not (Test-Path -LiteralPath $f.Paths.Recovery)) 'partial service recovery retired its record'
        # Simulate a crash after complete configuration but before the service was ever started.
        [void](New-SetupRecovery $f.Paths $null 'uninstall')
        Stop-SetupService $f.Paths; Wait-SetupExecutables $f.Paths
        Remove-SetupService $f.Paths
        [VencordSetup.Scm]::Create($f.Paths.ServiceName,('"'+$f.Paths.ServiceExecutable+'"'),(Get-SetupDescription $f.Paths))
        Check ((Assert-SetupService $f.Paths).Exit -eq 1077) 'fixture service is configured but never started'
        Restore-SetupRecovery $f.Paths
        Check ((Assert-SetupService $f.Paths).State -eq 4) 'never-started complete service drains trivially and is restored running'
        # A crash before the commit marker rename leaves only an inert transfer file: not committed.
        [void](New-SetupRecovery $f.Paths $null 'uninstall')
        Write-SetupDurable (Join-Path $f.Paths.Recovery ('transfer-'+[Guid]::NewGuid().ToString('N'))) (Get-SetupHash (Join-Path $f.Paths.Recovery 'record.json'))
        Check (-not (Read-SetupRecovery $f.Paths).Committed) 'unpublished commit marker is not a commit'
        Restore-SetupRecovery $f.Paths
        Check ((Assert-SetupService $f.Paths).State -eq 4) 'unpublished uninstall commit restores the running installation'
        Get-ChildItem -LiteralPath $f.Paths.ControlRoot -Directory -Filter 'completed-*' | Remove-Item -Recurse -Force
        [void](New-SetupRecovery $f.Paths $null 'uninstall')
        Stop-SetupService $f.Paths; Wait-SetupExecutables $f.Paths
        Remove-SetupService $f.Paths
        Remove-Item -LiteralPath (Join-Path $f.Paths.Destination 'README.md')
        Restore-SetupRecovery $f.Paths
        Check ((Get-SetupHash (Join-Path $f.Paths.Destination 'README.md')) -eq $oldHash) 'interrupted uninstall restores removed owned file'
        Check ((Assert-SetupService $f.Paths).State -eq 4) 'interrupted uninstall recreates exact owned service and prior state'
    } finally { Exit-SetupLock $lock }
    Uninstall-Fixture $f
} finally { Remove-MachineFixture $f }
