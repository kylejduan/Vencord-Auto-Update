using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
namespace VencordAutoUpdate {
internal sealed class Fixtures : IDisposable {
    internal readonly string Base, Root, Resources, Dist, Cache;
    internal Fixtures() {
        Base = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VencordAutoUpdate-tests-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Base, "Discord"); Resources = Path.Combine(Root, "app-1.0.10", "resources");
        Dist = Path.Combine(Base, "Vencord", "dist"); Cache = Path.Combine(Base, "helper");
        Directory.CreateDirectory(Resources); Directory.CreateDirectory(Dist);
        File.WriteAllText(Path.Combine(Root, "Update.exe"), "fixture");
        File.WriteAllText(Path.Combine(Root, "app-1.0.10", "Discord.exe"), "fixture");
        foreach (string name in new [] { "patcher.js", "preload.js", "renderer.js", "renderer.css" }) File.WriteAllText(Path.Combine(Dist, name), "fixture");
    }
    // Test-only ASAR writer: literal schema, independently framed Pickle header.
    internal static byte[] Archive(string package, string entryName, string content) {
        byte[] a = Encoding.UTF8.GetBytes(package), b = Encoding.UTF8.GetBytes(content);
        string header = "{\"files\":{\"package.json\":{\"size\":" + a.Length + ",\"offset\":\"0\"},";
        string item = "{\"size\":" + b.Length + ",\"offset\":\"" + a.Length + "\"}";
        if (entryName == "app_bootstrap/index.js") header += "\"app_bootstrap\":{\"files\":{\"index.js\":" + item + "}}";
        else header += "\"" + entryName + "\":" + item;
        header += "}}";
        return Raw(header, Combine(a,b));
    }
    static byte[] Combine(byte[] a,byte[] b) { byte[] result = new byte[a.Length + b.Length]; Buffer.BlockCopy(a,0,result,0,a.Length); Buffer.BlockCopy(b,0,result,a.Length,b.Length); return result; }
    internal static byte[] Raw(string header,byte[] data) {
        byte[] h = Encoding.UTF8.GetBytes(header); int padded = (h.Length + 3) & ~3;
        using (MemoryStream m = new MemoryStream()) using (BinaryWriter w = new BinaryWriter(m)) {
            w.Write(4); w.Write(padded + 8); w.Write(padded + 4); w.Write(h.Length); w.Write(h);
            for (int i = h.Length; i < padded; i++) w.Write((byte)0);
            w.Write(data); return m.ToArray();
        }
    }
    internal static byte[] Stock(string extra) { return Archive("{\"name\":\"discord\",\"main\":\"app_bootstrap/index.js\"}", "app_bootstrap/index.js", "// stock bootstrap " + extra); }
    internal byte[] Loader(string suffix) { return Archive("{\"name\":\"discord\",\"main\":\"index.js\"}", "index.js", "require(" + new JavaScriptSerializer().Serialize(Path.Combine(Dist,"patcher.js")) + ")" + suffix); }
    internal void Write(string name, byte[] bytes) { File.WriteAllBytes(Path.Combine(Resources, name), bytes); }
    internal Installation Inspect() { return Installation.Inspect(Root, "Discord.exe", Dist); }
    public void Dispose() { Directory.Delete(Base, true); }
}
}
