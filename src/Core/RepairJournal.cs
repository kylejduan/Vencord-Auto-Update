using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace VencordAutoUpdate {
internal sealed class RepairJournal {
    readonly Dictionary<string, object> values;
    internal string Temporary { get { return (string)values["temporary"]; } }
    RepairJournal(Dictionary<string, object> map) { values = map; }
    internal static RepairJournal Create(Installation candidate, ValidatedLoader loader) {
        Dictionary<string, object> map = new Dictionary<string, object> {
            {"owner","VencordAutoUpdate"}, {"schema",1}, {"root",candidate.Root}, {"version",candidate.Version},
            {"resources",candidate.Resources}, {"executable",candidate.Executable}, {"dist",candidate.Dist},
            {"originalHash",candidate.OriginalHash}, {"loaderHash",loader.Hash},
            {"temporary",".vencord-auto-update." + Guid.NewGuid().ToString("N") + ".loader.tmp"}
        };
        return new RepairJournal(map);
    }
    internal byte[] Bytes() { return Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(values)); }
    internal static RepairJournal Read(string path, Installation candidate, ValidatedLoader loader) {
        SafePath.RequireFile(path); if (new FileInfo(path).Length > 16384) throw new IOException("Oversized repair journal retained.");
        Dictionary<string, object> map = StrictJson.Object(StrictJson.Parse(new UTF8Encoding(false,true).GetString(SafePath.ReadBounded(path,16384))));
        object schema;
        if (map.Count != 10 || !map.TryGetValue("schema",out schema) || !(schema is decimal) || (decimal)schema != 1 || StrictJson.String(map,"owner") != "VencordAutoUpdate") throw new IOException("Foreign or corrupt repair journal retained.");
        foreach (KeyValuePair<string,string> pair in new Dictionary<string,string> {
            {"root",candidate.Root}, {"version",candidate.Version}, {"resources",candidate.Resources},
            {"executable",candidate.Executable}, {"dist",candidate.Dist}, {"originalHash",candidate.OriginalHash}, {"loaderHash",loader.Hash}
        }) if (!String.Equals(StrictJson.String(map,pair.Key),pair.Value,StringComparison.Ordinal)) throw new IOException("Journal identity does not match this generation; evidence retained.");
        string name = StrictJson.String(map,"temporary"); Guid id;
        const string prefix = ".vencord-auto-update.", suffix = ".loader.tmp";
        if (!name.StartsWith(prefix,StringComparison.Ordinal) || !name.EndsWith(suffix,StringComparison.Ordinal) || name.Length != prefix.Length + 32 + suffix.Length || !Guid.TryParseExact(name.Substring(prefix.Length,32),"N",out id)) throw new IOException("Invalid journal temporary name.");
        SafePath.Local(Path.Combine(candidate.Resources,name));
        return new RepairJournal(map);
    }
}
}
