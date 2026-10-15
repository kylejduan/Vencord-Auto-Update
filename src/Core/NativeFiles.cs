using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VencordAutoUpdate {
internal static class NativeFiles {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MoveFileEx(string existing, string destination, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int type, IntPtr information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    // BY_HANDLE_FILE_INFORMATION uses 4-byte aligned FILETIME pairs.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct FileInformation { internal uint Attributes; internal long Created, Accessed, Written; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    // Reads and deletes one held file object, so only the bytes that pass validation are deleted.
    internal static bool DeleteVerified(string path, int maximum, Func<byte[],bool> validate) {
        path = SafePath.Local(path);
        // GENERIC_READ|DELETE, FILE_SHARE_READ, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT.
        using (SafeFileHandle handle = CreateFile(path,0x80010000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero)) {
            if (handle.IsInvalid) throw new IOException("Cannot hold file for verified deletion: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            FileInformation info;
            if (!GetFileInformationByHandle(handle,out info)) throw new IOException("Cannot inspect file: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if ((info.Attributes & 0x410) != 0 || info.Links != 1 || info.SizeHigh != 0 || info.SizeLow == 0 || info.SizeLow > maximum) return false;
            byte[] bytes = new byte[info.SizeLow];
            using (FileStream stream = new FileStream(handle,FileAccess.Read,4096,false)) {
                int read = 0;
                while (read < bytes.Length) { int count = stream.Read(bytes,read,bytes.Length - read); if (count == 0) return false; read += count; }
                if (stream.ReadByte() != -1 || !validate(bytes)) return false;
                // FILE_DISPOSITION_INFO { BOOLEAN DeleteFile }.
                IntPtr disposition = Marshal.AllocHGlobal(4);
                try {
                    Marshal.WriteInt32(disposition,1);
                    if (!SetFileInformationByHandle(handle,4,disposition,1)) throw new IOException("Verified deletion failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                } finally { Marshal.FreeHGlobal(disposition); }
            }
        }
        return true;
    }
    internal static void MoveNew(string source, string destination) {
        SafePath.Local(source); SafePath.Local(destination);
        // MOVEFILE_WRITE_THROUGH only: deliberately omit REPLACE_EXISTING and COPY_ALLOWED.
        if (!MoveFileEx(source,destination,8)) throw new IOException("No-replace rename failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
    // Hashes and renames one opened file object. Other openers cannot write, rename or delete it
    // between verification and the no-replace rename, so the renamed bytes are the verified bytes.
    internal static void MoveNewVerified(string source, string destination, string expectedHash) {
        source = SafePath.Local(source); destination = SafePath.Local(destination);
        if (!SafePath.Same(Path.GetDirectoryName(source),Path.GetDirectoryName(destination))) throw new IOException("Verified rename stays within one directory.");
        // GENERIC_READ|GENERIC_WRITE|DELETE, FILE_SHARE_READ, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT.
        using (SafeFileHandle handle = CreateFile(source,0xC0010000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero)) {
            if (handle.IsInvalid) throw new IOException("Cannot hold rename source: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            FileInformation info;
            if (!GetFileInformationByHandle(handle,out info)) throw new IOException("Cannot inspect rename source: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if ((info.Attributes & 0x410) != 0 || info.Links != 1) throw new IOException("Rename source is not a single ordinary file.");
            using (FileStream stream = new FileStream(handle,FileAccess.ReadWrite,4096,false)) {
                string actual;
                using (SHA256 hash = SHA256.Create()) actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                if (actual != expectedHash) throw new IOException("Rename source changed after validation; evidence retained.");
                // FILE_RENAME_INFO: BOOLEAN ReplaceIfExists, HANDLE RootDirectory, DWORD FileNameLength, WCHAR FileName[].
                // An explicit NT path is valid whether or not the Win32 layer converts DOS paths.
                string target = "\\??\\" + destination;
                int nameOffset = 2 * IntPtr.Size + 4, nameBytes = target.Length * 2, size = nameOffset + nameBytes + 2 * IntPtr.Size;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try {
                    for (int i = 0; i < size; i++) Marshal.WriteByte(buffer,i,0);
                    Marshal.WriteInt32(buffer,2 * IntPtr.Size,nameBytes);
                    Marshal.Copy(target.ToCharArray(),0,IntPtr.Add(buffer,nameOffset),target.Length);
                    if (!SetFileInformationByHandle(handle,3,buffer,(uint)size)) throw new IOException("No-replace rename failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                } finally { Marshal.FreeHGlobal(buffer); }
                // Same durability as MOVEFILE_WRITE_THROUGH: flush the renamed file before returning.
                stream.Flush(true);
            }
        }
    }
}
}
