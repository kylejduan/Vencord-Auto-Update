using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
namespace VencordAutoUpdate {
internal sealed class UserSession {
    internal readonly int Id;internal readonly string Sid,Profile;internal readonly string[] Roots;
    internal UserSession(int id,string sid,string profile,string[] roots){Id=id;Sid=sid;Profile=profile;Roots=roots;}
}
internal static class UserSessions {
    internal static TokenHandle OrdinaryToken(int session,string expectedSid) {
        TokenHandle source;if(!SessionNative.WTSQueryUserToken(session,out source))throw new Win32Exception();
        using(source) {
            TokenHandle limited=null;
            try {
                if(SessionNative.TokenInt(source,18)==2) {
                    IntPtr buffer=Marshal.AllocHGlobal(IntPtr.Size);
                    try {int needed;if(!SessionNative.GetTokenInformation(source,19,buffer,IntPtr.Size,out needed))throw new Win32Exception();limited=new TokenHandle(Marshal.ReadIntPtr(buffer));}finally{Marshal.FreeHGlobal(buffer);}
                }
                TokenHandle ordinary=limited ?? source;
                if(SessionNative.TokenInt(ordinary,20)!=0 || SessionNative.TokenInt(ordinary,12)!=session)throw new UnauthorizedAccessException("Worker requires the exact ordinary user session.");
                using(var identity=new WindowsIdentity(ordinary.DangerousGetHandle())) {
                    SecurityIdentifier sid=identity.User;
                    if(sid==null || !sid.IsAccountSid() || identity.IsSystem || sid.IsWellKnown(WellKnownSidType.LocalServiceSid) || sid.IsWellKnown(WellKnownSidType.NetworkServiceSid) || (expectedSid!=null && sid.Value!=expectedSid))throw new UnauthorizedAccessException("Worker user identity refused.");
                }
                TokenHandle primary;if(!SessionNative.DuplicateTokenEx(ordinary,0x02000000,IntPtr.Zero,2,1,out primary))throw new Win32Exception();return primary;
            }finally{if(limited!=null)limited.Dispose();}
        }
    }
    static string KnownFolder(TokenHandle token,string id) {Guid folder=new Guid(id);IntPtr path;int hr=SessionNative.SHGetKnownFolderPath(ref folder,0x4000,token,out path);if(hr!=0)Marshal.ThrowExceptionForHR(hr);try{return Marshal.PtrToStringUni(path);}finally{Marshal.FreeCoTaskMem(path);}}
    internal static UserSession Describe(int session) {
        using(TokenHandle token=OrdinaryToken(session,null))using(var identity=new WindowsIdentity(token.DangerousGetHandle())) {
            var profile=new StringBuilder(32768);int size=profile.Capacity;if(!SessionNative.GetUserProfileDirectory(token,profile,ref size))throw new Win32Exception();
            string home=TrustedPaths.RequireLocal(profile.ToString());
            string local=TrustedPaths.WithinProfile(home,KnownFolder(token,"F1B32785-6FBA-4FCF-9D55-7B8E7F157091"));
            string roaming=TrustedPaths.WithinProfile(home,KnownFolder(token,"3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"));
            return new UserSession(session,identity.User.Value,home,new[]{Path.Combine(local,"Discord"),Path.Combine(local,"DiscordPTB"),Path.Combine(local,"DiscordCanary"),Path.Combine(roaming,"Vencord","dist")});
        }
    }
    internal static bool IsInteractive(int session) {
        IntPtr value;int size;if(!SessionNative.WTSQuerySessionInformation(IntPtr.Zero,session,25,out value,out size))return false;
        try {
            // WTSINFOEX: DWORD Level, 8-byte aligned WTSINFOEX_LEVEL1;
            // SessionId, State, SessionFlags. Windows 10/11 use 1 for unlocked.
            return size>=20 && Marshal.ReadInt32(value)==1 && Marshal.ReadInt32(value,8)==session && Marshal.ReadInt32(value,12)==0 && Marshal.ReadInt32(value,16)==1;
        }finally{SessionNative.WTSFreeMemory(value);}
    }
    internal static IList<UserSession> Eligible(Action<string> report) {
        IntPtr memory;int count;if(!SessionNative.WTSEnumerateSessions(IntPtr.Zero,0,1,out memory,out count))throw new Win32Exception();
        var result=new List<UserSession>();var seen=new HashSet<string>(StringComparer.Ordinal);
        try {int stride=Marshal.SizeOf(typeof(SessionNative.Session));for(int i=0;i<count;i++) {
            var session=(SessionNative.Session)Marshal.PtrToStructure(IntPtr.Add(memory,i*stride),typeof(SessionNative.Session));
            if(session.Id==0 || session.State!=0 || !IsInteractive(session.Id))continue;
            try{var user=Describe(session.Id);if(seen.Add(user.Sid))result.Add(user);}catch(Exception e){report("Session "+session.Id+" refused: "+e.Message);}
        }}finally{SessionNative.WTSFreeMemory(memory);}return result;
    }
}
}
