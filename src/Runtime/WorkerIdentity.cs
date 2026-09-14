using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace VencordAutoUpdate {
internal static class WorkerIdentity {
    [DllImport("advapi32.dll",SetLastError=true)]
    static extern bool GetTokenInformation(IntPtr token,int information,out int value,int length,out int returned);
    internal static bool Ordinary {
        get {
            using(var identity=WindowsIdentity.GetCurrent()) {
                if(identity.User==null || identity.IsSystem ||
                    identity.User.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
                    identity.User.IsWellKnown(WellKnownSidType.NetworkServiceSid)) return false;
                int elevated,length;
                return GetTokenInformation(identity.Token,20,out elevated,4,out length) && elevated==0;
            }
        }
    }
}
}
