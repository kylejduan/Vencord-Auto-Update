using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
namespace VencordAutoUpdate {
internal static class TrustedPaths {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetLongPathName(string path,StringBuilder result,uint length);
    internal static bool IsUnder(string path,string root) {return String.Equals(path,root,StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
    internal static string RequireLocal(string path) {
        if(String.IsNullOrEmpty(path) || path.Length<3 || !Char.IsLetter(path[0]) || path[1]!=':' || path[2]!='\\' || path.IndexOf(':',2)>=0)throw new IOException("A local absolute drive path is required.");
        string full=Path.GetFullPath(path).TrimEnd('\\');if(full.Length==2)full+="\\";
        if(new DriveInfo(Path.GetPathRoot(full)).DriveType!=DriveType.Fixed)throw new IOException("Only fixed local volumes are eligible.");
        for(string current=full;!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)) {
            try {if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse traversal refused.");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        if(File.Exists(full) || Directory.Exists(full)) {
            var canonical=new StringBuilder(32768);uint length=GetLongPathName(full,canonical,(uint)canonical.Capacity);
            if(length==0 || length>=canonical.Capacity)throw new IOException("Cannot canonicalize local path.");full=canonical.ToString().TrimEnd('\\');if(full.Length==2)full+="\\";
        }else if(full.IndexOf('~')>=0)throw new IOException("Unresolved short path alias refused.");
        return full;
    }
    internal static string WithinProfile(string profile,string path) {profile=RequireLocal(profile);path=RequireLocal(path);if(!IsUnder(path,profile) || String.Equals(path,profile,StringComparison.OrdinalIgnoreCase))throw new IOException("Known folder escaped the OS profile.");return path;}
    static bool Trusted(SecurityIdentifier sid) {
        if(sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))return true;
        return sid.Equals(new NTAccount("NT SERVICE","TrustedInstaller").Translate(typeof(SecurityIdentifier)));
    }
    internal static void RequireAcl(FileSystemSecurity acl,bool target) {
        if(!Trusted((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))))throw new UnauthorizedAccessException("Untrusted binary/ancestor owner.");
        FileSystemRights dangerous=FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles;
        if(target)dangerous|=FileSystemRights.WriteData|FileSystemRights.AppendData|FileSystemRights.WriteAttributes|FileSystemRights.WriteExtendedAttributes|FileSystemRights.Delete;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier))) {
            if(rule.AccessControlType!=AccessControlType.Allow || (rule.PropagationFlags&PropagationFlags.InheritOnly)!=0)continue;
            if(((rule.FileSystemRights&dangerous)!=0 || (((uint)rule.FileSystemRights)&0x50000000)!=0) && !Trusted((SecurityIdentifier)rule.IdentityReference))throw new UnauthorizedAccessException("Untrusted binary/ancestor write permission.");
        }
    }
    internal static string RequireProtectedBinary(string path) {
        path=RequireLocal(path);string programFiles=RequireLocal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if(!IsUnder(path,programFiles) || !File.Exists(path))throw new IOException("Binary must be installed beneath Program Files.");
        RequireAcl(File.GetAccessControl(path),true);
        bool installDirectory=true;
        for(string current=Path.GetDirectoryName(path);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)) {RequireAcl(Directory.GetAccessControl(current),installDirectory);installDirectory=false;}
        return path;
    }
}
}
