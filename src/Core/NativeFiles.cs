using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VencordAutoUpdate {
internal static class NativeFiles {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MoveFileEx(string existing, string destination, uint flags);
    internal static void MoveNew(string source, string destination) {
        SafePath.Local(source); SafePath.Local(destination);
        // MOVEFILE_WRITE_THROUGH only: deliberately omit REPLACE_EXISTING and COPY_ALLOWED.
        if (!MoveFileEx(source,destination,8)) throw new IOException("No-replace rename failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
}
}
