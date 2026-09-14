using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace VencordAutoUpdate {
internal sealed class TokenHandle:SafeHandleZeroOrMinusOneIsInvalid {
    internal TokenHandle():base(true){}
    internal TokenHandle(IntPtr value):base(true){SetHandle(value);}
    protected override bool ReleaseHandle(){return SessionNative.CloseHandle(handle);}
}
internal static class SessionNative {
    [StructLayout(LayoutKind.Sequential)]internal struct Session {internal int Id;internal IntPtr Station;internal int State;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct Startup {
        internal int Size;internal string Reserved,Desktop,Title;internal int X,Y,XSize,YSize,XChars,YChars,Fill,Flags;internal short Show,ReservedSize;internal IntPtr ReservedBytes,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)]internal struct ProcessInfo {internal IntPtr Process,Thread;internal int Pid,Tid;}
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("wtsapi32.dll",SetLastError=true)]internal static extern bool WTSEnumerateSessions(IntPtr server,int reserved,int version,out IntPtr sessions,out int count);
    [DllImport("wtsapi32.dll")]internal static extern void WTSFreeMemory(IntPtr value);
    [DllImport("wtsapi32.dll",SetLastError=true)]internal static extern bool WTSQueryUserToken(int session,out TokenHandle token);
    [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool WTSQuerySessionInformation(IntPtr server,int session,int info,out IntPtr value,out int size);
    [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool GetTokenInformation(TokenHandle token,int info,IntPtr value,int length,out int needed);
    [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool DuplicateTokenEx(TokenHandle token,uint access,IntPtr attributes,int impersonation,int type,out TokenHandle duplicate);
    [DllImport("userenv.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool GetUserProfileDirectory(TokenHandle token,StringBuilder path,ref int size);
    [DllImport("shell32.dll")]internal static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,TokenHandle token,out IntPtr path);
    [DllImport("userenv.dll",SetLastError=true)]internal static extern bool CreateEnvironmentBlock(out IntPtr environment,TokenHandle token,bool inherit);
    [DllImport("userenv.dll")]internal static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool CreateProcessAsUser(TokenHandle token,string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetExitCodeProcess(TokenHandle handle,out int code);
    internal static int TokenInt(TokenHandle token,int kind) {IntPtr buffer=Marshal.AllocHGlobal(4);try{int needed;if(!GetTokenInformation(token,kind,buffer,4,out needed))throw new Win32Exception();return Marshal.ReadInt32(buffer);}finally{Marshal.FreeHGlobal(buffer);}}
}
}
