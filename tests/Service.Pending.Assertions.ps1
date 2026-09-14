# Test-only read-only SCM status detail; never sourced by production setup.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class PendingScmProbe {
    [StructLayout(LayoutKind.Sequential)]public struct Status {public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,WaitHint,Pid,Flags;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string machine,string database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr scm,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,uint size,out uint needed);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
    public static Status Read(string name){
        IntPtr scm=OpenSCManager(null,null,1);if(scm==IntPtr.Zero)throw new Win32Exception();
        try{IntPtr service=OpenService(scm,name,4);if(service==IntPtr.Zero)throw new Win32Exception();
            try{Status status;uint needed;if(!QueryServiceStatusEx(service,0,out status,(uint)Marshal.SizeOf(typeof(Status)),out needed))throw new Win32Exception();return status;}
            finally{CloseServiceHandle(service);}
        }finally{CloseServiceHandle(scm);}
    }
}
'@
