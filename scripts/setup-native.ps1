# Native readback avoids localized sc.exe output and verifies effective SCM/ACL state.
if (-not ('VencordSetup.Scm' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
namespace VencordSetup {
public static class Paths {
    static bool Trusted(SecurityIdentifier sid) {
        return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.Equals(new NTAccount("NT SERVICE","TrustedInstaller").Translate(typeof(SecurityIdentifier)));
    }
    public static void CheckAcl(FileSystemSecurity acl,bool target) {
        if(!Trusted((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))))throw new UnauthorizedAccessException("Untrusted setup target/ancestor owner.");
        FileSystemRights danger=FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles;
        if(target)danger|=FileSystemRights.WriteData|FileSystemRights.AppendData|FileSystemRights.WriteAttributes|FileSystemRights.WriteExtendedAttributes;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier))) {
            if(rule.AccessControlType!=AccessControlType.Allow || (rule.PropagationFlags&PropagationFlags.InheritOnly)!=0)continue;
            if(((rule.FileSystemRights&danger)!=0 || ((uint)rule.FileSystemRights&0x50000000)!=0) && !Trusted((SecurityIdentifier)rule.IdentityReference))throw new UnauthorizedAccessException("Untrusted setup target/ancestor write or delete permission.");
        }
    }
    public static void Protected(string path) {
        if(path.Length<3 || !Char.IsLetter(path[0]) || path[1]!=':' || path[2]!='\\' || path.IndexOf(':',2)>=0)throw new IOException("Local absolute path required.");
        if(new DriveInfo(Path.GetPathRoot(path)).DriveType!=DriveType.Fixed)throw new IOException("Fixed volume required.");
        bool target=true;
        for(string p=Path.GetFullPath(path);!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p)) {
            FileAttributes attributes;
            try{attributes=File.GetAttributes(p);}
            catch(FileNotFoundException){target=false;continue;}
            catch(DirectoryNotFoundException){target=false;continue;}
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse target or ancestor refused.");
            CheckAcl((attributes&FileAttributes.Directory)!=0?(FileSystemSecurity)Directory.GetAccessControl(p):File.GetAccessControl(p),target);
            target=false;
        }
    }
    static FileSecurity NewFileAcl() {
        var acl=new FileSecurity();acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)");return acl;
    }
    public static FileStream CreateFile(string path) {
        Protected(Path.GetDirectoryName(path));
        return new FileStream(path,FileMode.CreateNew,FileSystemRights.Read|FileSystemRights.Write,FileShare.None,4096,FileOptions.None,NewFileAcl());
    }
    public static void SecureCreatedShortcut(string path) {
        // Called only for the fresh COM-created link inside an owned protected stage.
        File.SetAccessControl(path,NewFileAcl());Protected(path);
    }
    public static void CreateDirectory(string path) {
        if(Directory.Exists(path)){Protected(path);return;}
        string parent=Path.GetDirectoryName(path);Protected(parent);
        if(!Directory.Exists(parent))throw new IOException("Create protected directories one level at a time.");
        var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
        Directory.CreateDirectory(path,acl);Protected(path);
    }
}
public sealed class Snapshot {
    public uint Type,Start,Error,State,Exit,SpecificExit,Pid,Reset,FailureFlag,Delayed;
    public string Image,Account,Display,Group,Dependencies,Description,Dacl,FailureCommand,RebootMessage;
    public uint[] Actions,Delays;
}
public static class Scm {
    // SCM expands GENERIC_ALL to SERVICE_ALL_ACCESS (0xF01FF) on readback.
    // Emit the effective rights so the exact ownership comparison stays stable.
    public const string Security="O:BAG:BAD:(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;CCLCSWLOCRRC;;;BU)";
    [StructLayout(LayoutKind.Sequential)]struct Config {public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    [StructLayout(LayoutKind.Sequential)]struct Failure {public uint Reset;public IntPtr Reboot,Command;public uint Count;public IntPtr Actions;}
    [StructLayout(LayoutKind.Sequential)]struct Status {public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,WaitHint,Pid,Flags;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string machine,string database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr scm,string name,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateService(IntPtr scm,string name,string display,uint access,uint type,uint start,uint error,string image,string group,IntPtr tag,string deps,string account,string password);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryServiceConfig(IntPtr service,IntPtr buffer,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryServiceConfig2(IntPtr service,uint level,IntPtr buffer,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ChangeServiceConfig2(IntPtr service,uint level,IntPtr info);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceObjectSecurity(IntPtr service,uint info,IntPtr sd,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool SetServiceObjectSecurity(IntPtr service,uint info,byte[] sd);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool StartService(IntPtr service,uint count,IntPtr args);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool ControlService(IntPtr service,uint control,IntPtr status);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool DeleteService(IntPtr service);
    static void Check(bool ok){if(!ok)throw new Win32Exception();}
    static IntPtr Open(string name,uint access){IntPtr scm=OpenSCManager(null,null,1);if(scm==IntPtr.Zero)throw new Win32Exception();try{return OpenService(scm,name,access);}finally{CloseServiceHandle(scm);}}
    static string Text(IntPtr p){return p==IntPtr.Zero?"":Marshal.PtrToStringUni(p);}
    static IntPtr Query(IntPtr service,uint level){uint needed;QueryServiceConfig2(service,level,IntPtr.Zero,0,out needed);if(needed==0)throw new Win32Exception();IntPtr p=Marshal.AllocHGlobal((int)needed);try{Check(QueryServiceConfig2(service,level,p,needed,out needed));return p;}catch{Marshal.FreeHGlobal(p);throw;}}
    public static Snapshot Read(string name) {
        IntPtr h=Open(name,0x20005);if(h==IntPtr.Zero){int error=Marshal.GetLastWin32Error();if(error==1060)return null;throw new Win32Exception(error);}
        try {
            var s=new Snapshot();uint needed;QueryServiceConfig(h,IntPtr.Zero,0,out needed);IntPtr p=Marshal.AllocHGlobal((int)needed);
            try{Check(QueryServiceConfig(h,p,needed,out needed));var c=(Config)Marshal.PtrToStructure(p,typeof(Config));s.Type=c.Type;s.Start=c.Start;s.Error=c.Error;s.Image=Text(c.Image);s.Group=Text(c.Group);s.Dependencies=Text(c.Dependencies);s.Account=Text(c.Account);s.Display=Text(c.Display);}finally{Marshal.FreeHGlobal(p);}
            p=Query(h,2);try{var f=(Failure)Marshal.PtrToStructure(p,typeof(Failure));if(f.Count>100)throw new IOException("Invalid failure action count.");s.Reset=f.Reset;s.FailureCommand=Text(f.Command);s.RebootMessage=Text(f.Reboot);s.Actions=new uint[f.Count];s.Delays=new uint[f.Count];for(int i=0;i<f.Count;i++){s.Actions[i]=(uint)Marshal.ReadInt32(f.Actions,i*8);s.Delays[i]=(uint)Marshal.ReadInt32(f.Actions,i*8+4);}}finally{Marshal.FreeHGlobal(p);}
            p=Query(h,4);try{s.FailureFlag=(uint)Marshal.ReadInt32(p);}finally{Marshal.FreeHGlobal(p);}
            p=Query(h,3);try{s.Delayed=(uint)Marshal.ReadInt32(p);}finally{Marshal.FreeHGlobal(p);}
            p=Query(h,1);try{s.Description=Text(Marshal.ReadIntPtr(p));}finally{Marshal.FreeHGlobal(p);}
            QueryServiceObjectSecurity(h,7,IntPtr.Zero,0,out needed);p=Marshal.AllocHGlobal((int)needed);
            try{Check(QueryServiceObjectSecurity(h,7,p,needed,out needed));byte[] bytes=new byte[needed];Marshal.Copy(p,bytes,0,(int)needed);s.Dacl=new RawSecurityDescriptor(bytes,0).GetSddlForm(AccessControlSections.Owner|AccessControlSections.Group|AccessControlSections.Access);}finally{Marshal.FreeHGlobal(p);}
            Status status;Check(QueryServiceStatusEx(h,0,out status,(uint)Marshal.SizeOf(typeof(Status)),out needed));s.State=status.State;s.Exit=status.Exit;s.SpecificExit=status.SpecificExit;s.Pid=status.Pid;return s;
        }finally{CloseServiceHandle(h);}
    }
    public static string CanonicalSecurity(string text){return new RawSecurityDescriptor(text).GetSddlForm(AccessControlSections.Owner|AccessControlSections.Group|AccessControlSections.Access);}
    public static void Create(string name,string image,string description) {
        IntPtr scm=OpenSCManager(null,null,3);if(scm==IntPtr.Zero)throw new Win32Exception();IntPtr h=IntPtr.Zero;
        try {
            h=CreateService(scm,name,name,0xf01ff,0x10,2,1,image,null,IntPtr.Zero,null,"LocalSystem",null);if(h==IntPtr.Zero)throw new Win32Exception();
            var sd=new RawSecurityDescriptor(Security);byte[] bytes=new byte[sd.BinaryLength];sd.GetBinaryForm(bytes,0);Check(SetServiceObjectSecurity(h,7,bytes));
            IntPtr actions=Marshal.AllocHGlobal(32),data=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Failure))),empty=Marshal.StringToHGlobalUni("");
            try{for(int i=0;i<4;i++){Marshal.WriteInt32(actions,i*8,i<3?1:0);Marshal.WriteInt32(actions,i*8+4,i<3?60000:0);}var f=new Failure{Reset=86400,Command=empty,Reboot=empty,Count=4,Actions=actions};Marshal.StructureToPtr(f,data,false);Check(ChangeServiceConfig2(h,2,data));Marshal.WriteInt32(data,0);Check(ChangeServiceConfig2(h,4,data));Check(ChangeServiceConfig2(h,3,data));}finally{Marshal.FreeHGlobal(actions);Marshal.FreeHGlobal(data);Marshal.FreeHGlobal(empty);}
            IntPtr text=Marshal.StringToHGlobalUni(description),ptr=Marshal.AllocHGlobal(IntPtr.Size);try{Marshal.WriteIntPtr(ptr,text);Check(ChangeServiceConfig2(h,1,ptr));}finally{Marshal.FreeHGlobal(text);Marshal.FreeHGlobal(ptr);}
        }finally{if(h!=IntPtr.Zero)CloseServiceHandle(h);CloseServiceHandle(scm);}
    }
    public static void Start(string name){IntPtr h=Open(name,0x10);if(h==IntPtr.Zero)throw new Win32Exception();try{Check(StartService(h,0,IntPtr.Zero));}finally{CloseServiceHandle(h);}}
    public static void Stop(string name){IntPtr h=Open(name,0x20);if(h==IntPtr.Zero)throw new Win32Exception();IntPtr p=Marshal.AllocHGlobal(36);try{Check(ControlService(h,1,p));}finally{Marshal.FreeHGlobal(p);CloseServiceHandle(h);}}
    public static void Delete(string name){IntPtr h=Open(name,0x10000);if(h==IntPtr.Zero)throw new Win32Exception();try{Check(DeleteService(h));}finally{CloseServiceHandle(h);}}
}
}
'@
}
