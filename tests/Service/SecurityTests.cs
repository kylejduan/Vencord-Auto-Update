using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    static void SecurityTests() {
        var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);var users=new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);
        var acl=new DirectorySecurity();acl.SetOwner(admin);
        acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.CreateDirectories,AccessControlType.Allow));
        TrustedPaths.RequireAcl(acl,false);Check(true,"drive ancestor create directories does not permit replacing protected child");
        bool refused=false;try{TrustedPaths.RequireAcl(acl,true);}catch(UnauthorizedAccessException){refused=true;}Check(refused,"installation directory user write refused");
        acl=new DirectorySecurity();acl.SetOwner(admin);acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.InheritOnly,AccessControlType.Allow));TrustedPaths.RequireAcl(acl,false);Check(true,"inherit-only rights not effective on ancestor");
        acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.DeleteSubdirectoriesAndFiles,AccessControlType.Allow));refused=false;try{TrustedPaths.RequireAcl(acl,false);}catch(UnauthorizedAccessException){refused=true;}Check(refused,"ancestor delete-child refused");
        acl=new DirectorySecurity();acl.SetOwner(users);refused=false;try{TrustedPaths.RequireAcl(acl,false);}catch(UnauthorizedAccessException){refused=true;}Check(refused,"untrusted owner refused");
        foreach(string rights in new[]{"GW","GA"}) {
            var raw=new DirectorySecurity();raw.SetSecurityDescriptorSddlForm("O:BAG:BAD:(A;;"+rights+";;;BU)");
            refused=false;try{TrustedPaths.RequireAcl(raw,true);}catch(UnauthorizedAccessException){refused=true;}Check(refused,"untrusted generic "+rights+" refused");
        }
        string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);refused=false;try{TrustedPaths.WithinProfile(local,Path.GetDirectoryName(local));}catch(IOException){refused=true;}Check(refused,"known folder escape refused");
        foreach(var assembly in typeof(BrokerService).Assembly.GetReferencedAssemblies())Check(assembly.Name!="System.Windows.Forms","service has no WinForms dependency");
        Check(typeof(BrokerService).Assembly.GetName().Version.ToString()=="0.1.0.0","service version matches release");
        DateTime now=DateTime.UtcNow;var schedule=new BrokerSchedule(now);schedule.Signal(now.AddSeconds(1));Check(!schedule.Take(now.AddSeconds(9)) && schedule.Take(now.AddSeconds(10)),"session replacement preserves per SID rate cap");
    }
}
}
