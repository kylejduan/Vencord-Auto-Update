using System;
using System.Runtime.InteropServices;
namespace VencordAutoUpdate {
internal static class SessionState {
    [DllImport("user32.dll",SetLastError=true)]static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll",SetLastError=true)]static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")]static extern bool CloseDesktop(IntPtr desktop);
    internal static bool Interactive {
        get {
            if(!Environment.UserInteractive)return false;
            IntPtr desktop=OpenInputDesktop(0,false,0x0100);
            if(desktop==IntPtr.Zero)return false;
            try{return SwitchDesktop(desktop);}finally{CloseDesktop(desktop);}
        }
    }
}
}
