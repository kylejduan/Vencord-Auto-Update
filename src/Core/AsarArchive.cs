using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VencordAutoUpdate {
internal sealed class AsarArchive : IDisposable {
    const int HeaderLimit = 4 * 1024 * 1024;
    readonly Stream stream;
    readonly long dataStart;
    bool loaderShape = true;
    long contentLength;
    readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    sealed class Entry { internal long Offset; internal long Size; }
    internal AsarArchive(string path) : this(Open(path)) { }
    internal AsarArchive(byte[] bytes) : this(new MemoryStream(bytes, false)) { }
    static Stream Open(string path) { SafePath.RequireFile(path); return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
    AsarArchive(Stream source) {
        stream = source;
        try {
            if (stream.Length < 16) throw new IOException("Truncated ASAR framing.");
            BinaryReader reader = new BinaryReader(stream);
            uint size = reader.ReadUInt32(), header = reader.ReadUInt32(), payload = reader.ReadUInt32(), json = reader.ReadUInt32();
            if (size != 4 || json == 0 || json > HeaderLimit || header != ((json + 3) & ~3U) + 8 || payload != header - 4 || 8L + header > stream.Length) throw new IOException("Invalid ASAR header bounds.");
            dataStart = 8L + header;
            string text = new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)json));
            Dictionary<string, object> root = StrictJson.Object(StrictJson.Parse(text)); object files;
            if (root.Count != 1 || !root.TryGetValue("files", out files)) throw new IOException("Unsupported ASAR root.");
            Walk(StrictJson.Object(files), "", 0);
            List<Entry> ranges = new List<Entry>(entries.Values); ranges.Sort(delegate(Entry a, Entry b) { return a.Offset.CompareTo(b.Offset); });
            long end = 0;
            foreach (Entry entry in ranges) { if (entry.Offset < end && entry.Size != 0) throw new IOException("Overlapping ASAR entries."); end = Math.Max(end, entry.Offset + entry.Size); }
        } catch { stream.Dispose(); throw; }
    }
    void Walk(Dictionary<string, object> files, string prefix, int depth) {
        if (depth > 32) throw new IOException("ASAR tree too deep.");
        foreach (KeyValuePair<string, object> pair in files) {
            string name = pair.Key;
            if (String.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(new [] {'/', '\\', ':', '\0'}) >= 0) throw new IOException("Unsafe ASAR entry path.");
            Dictionary<string, object> item = StrictJson.Object(pair.Value); object children;
            if (item.ContainsKey("link") || item.ContainsKey("unpacked")) throw new IOException("Unpacked or linked ASAR entries unsupported.");
            if (item.TryGetValue("files", out children)) {
                loaderShape = false;
                if (item.Count != 1) throw new IOException("Ambiguous ASAR directory."); Walk(StrictJson.Object(children), prefix + name + "/", depth + 1); continue;
            }
            object size; long offset;
            if (!item.TryGetValue("size", out size) || !(size is decimal) || (decimal)size < 0 || (decimal)size != Decimal.Truncate((decimal)size) || (decimal)size > Int64.MaxValue || !Int64.TryParse(StrictJson.String(item,"offset"), NumberStyles.None,CultureInfo.InvariantCulture,out offset)) throw new IOException("Invalid ASAR entry range.");
            if (item.Count != 2) loaderShape = false;
            long length = (long)(decimal)size;
            if (offset > stream.Length - dataStart || length > stream.Length - dataStart - offset) throw new IOException("ASAR entry exceeds file.");
            if (entries.Count >= 100000) throw new IOException("Too many ASAR entries.");
            contentLength += length;
            entries.Add(prefix + name,new Entry { Offset = offset, Size = length });
        }
    }
    string ReadText(string path, int maximum) {
        Entry entry;
        if (!entries.TryGetValue(path,out entry) || entry.Size == 0 || entry.Size > maximum) throw new IOException("Missing or oversized archive entry: " + path);
        stream.Position = dataStart + entry.Offset; byte[] bytes = new byte[(int)entry.Size]; int read = 0;
        while (read < bytes.Length) { int count = stream.Read(bytes,read,bytes.Length-read); if (count == 0) throw new IOException("Truncated archive entry."); read += count; }
        return new UTF8Encoding(false,true).GetString(bytes);
    }
    Dictionary<string, object> Package() { return StrictJson.Object(StrictJson.Parse(ReadText("package.json", 65536))); }
    internal bool IsStock() {
        Dictionary<string, object> package = Package(); string main = StrictJson.String(package,"main"); Entry entry;
        return StrictJson.String(package,"name") == "discord" && (main == "bundle.js" || main == "app_bootstrap/index.js") && entries.TryGetValue(main,out entry) && entry.Size > 0;
    }
    internal bool IsLoader(string dist) {
        if (stream.Length > 65536 || entries.Count != 2 || !loaderShape || contentLength != stream.Length - dataStart) return false;
        Dictionary<string, object> package = Package();
        if (package.Count != 2 || StrictJson.String(package,"name") != "discord" || StrictJson.String(package,"main") != "index.js") return false;
        string source = ReadText("index.js", 32768);
        if (!source.StartsWith("require(",StringComparison.Ordinal) || !source.EndsWith(")",StringComparison.Ordinal)) return false;
        string path = StrictJson.Parse(source.Substring(8,source.Length-9)) as string;
        if (path == null || !SafePath.Same(SafePath.Local(path),Path.Combine(SafePath.Local(dist),"patcher.js"))) return false;
        foreach (string file in new [] { "patcher.js", "preload.js", "renderer.js", "renderer.css" }) SafePath.RequireFile(Path.Combine(dist,file));
        return true;
    }
    public void Dispose() { stream.Dispose(); }
}
}
