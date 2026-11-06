using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace VencordAutoUpdate {
// Shared read-only account classification; unresolved ownership is never excluded.
internal static class SessionOwnership {
    [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode)]static extern bool WTSQuerySessionInformation(IntPtr server,int session,int information,out IntPtr value,out int bytes);
    [DllImport("wtsapi32.dll")]static extern void WTSFreeMemory(IntPtr value);
    static string SessionText(int session,int information) {
        IntPtr value;int bytes;
        if(!WTSQuerySessionInformation(IntPtr.Zero,session,information,out value,out bytes))return null;
        try{return Marshal.PtrToStringUni(value);}finally{WTSFreeMemory(value);}
    }
    internal static bool KnownOtherUser(int session,string ownSid) {
        try {
            string name=SessionText(session,5),domain=SessionText(session,7);
            if(String.IsNullOrEmpty(name) || String.IsNullOrEmpty(domain))return false;
            var sid=(SecurityIdentifier)new NTAccount(domain,name).Translate(typeof(SecurityIdentifier));
            return sid.Value!=ownSid;
        }catch(Exception){return false;}
    }
}
}
