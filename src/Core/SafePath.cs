using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VencordAutoUpdate {
internal static class SafePath {
    internal static string Local(string path) {
        // IsPathRooted also accepts C:relative and \root-relative on Windows.
        // Validate the original spelling before GetFullPath can consult the current directory.
        if (String.IsNullOrWhiteSpace(path) || path.Length < 3 ||
            !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) ||
            path[1] != ':' || (path[2] != '\\' && path[2] != '/')) throw new IOException("A fully qualified local drive path is required.");
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length < 3 || full[1] != ':' || full[2] != '\\' || full.IndexOf(':', 2) >= 0) throw new IOException("Only local drive paths are supported.");
        for (string current = full; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are unsupported: " + current);
        }
        return full;
    }
    internal static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    internal static void RequireFile(string path) {
        Local(path);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("Missing or empty file: " + path);
    }
    internal static string Hash(string path) {
        Local(path);
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 hash = SHA256.Create()) { return Hex(hash.ComputeHash(stream)); }
    }
    internal static string Hash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) { return Hex(hash.ComputeHash(bytes)); } }
    static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    internal static string Metadata(string path) {
        Local(path);
        if (File.Exists(path)) { FileInfo f = new FileInfo(path); return path + "|F|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks + "|" + (int)f.Attributes; }
        if (Directory.Exists(path)) { DirectoryInfo d = new DirectoryInfo(path); return path + "|D|" + d.LastWriteTimeUtc.Ticks + "|" + (int)d.Attributes; }
        return path + "|missing";
    }
    internal static byte[] ReadBounded(string path,int maximum) {
        RequireFile(path);
        using (FileStream stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            if (stream.Length > maximum) throw new IOException("File exceeds supported read bounds.");
            byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
            while (offset < bytes.Length) { int count = stream.Read(bytes,offset,bytes.Length-offset); if (count == 0) throw new IOException("File truncated during bounded read."); offset += count; }
            return bytes;
        }
    }
    internal static void WriteNew(string path, byte[] bytes, Action created = null) {
        Local(path);
        using (FileStream f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
            // Report ownership only after CreateNew succeeds, before any write/flush/dispose failure.
            if (created != null) created();
            f.Write(bytes, 0, bytes.Length); f.Flush(true);
        }
    }
    internal static string TextHash(string value) { return Hash(Encoding.UTF8.GetBytes(value)); }
}
}
