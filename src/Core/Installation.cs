using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VencordAutoUpdate {
internal enum InstallationKind { Healthy, Stock, Interrupted, Unsettled, Unsupported }
internal sealed class Installation {
    internal string Root { get; private set; }
    internal string Executable { get; private set; }
    internal string Version { get; private set; }
    internal string Resources { get; private set; }
    internal string Dist { get; private set; }
    internal string MetadataSignature { get; private set; }
    internal string EnvironmentSignature { get; private set; }
    internal InstallationKind Kind { get; private set; }
    internal string OriginalHash { get; private set; }
    internal string LoaderHash { get; private set; }
    internal string Reason { get; private set; }
    internal string Generation { get { return Root + "|" + Version + "|" + OriginalHash; } }
    Installation() { Kind = InstallationKind.Unsupported; }
    internal static IList<Installation> Discover(string localAppData, string dist) {
        List<Installation> result = new List<Installation>();
        foreach (string channel in new [] { "Discord", "DiscordPTB", "DiscordCanary" }) {
            string root = Path.Combine(localAppData,channel);
            if (Directory.Exists(root)) result.Add(Inspect(root, channel + ".exe",dist));
        }
        return result;
    }
    internal static string GetSettlingMetadataSignature(string root) { return MetadataTree(root,null); }
    static string MetadataTree(string root, string ignoreArchivesIn) {
        root = SafePath.Local(root); List<string> records = new List<string>();
        WalkMetadata(root,ignoreArchivesIn,records,0); records.Sort(StringComparer.Ordinal);
        return SafePath.TextHash(String.Join("\n",records));
    }
    static void WalkMetadata(string directory, string ignoreArchivesIn, List<string> records, int depth) {
        SafePath.Local(directory);
        if (depth > 64 || records.Count > 100000) throw new IOException("Installation metadata scan exceeds supported bounds.");
        if (!Directory.Exists(directory)) { records.Add(directory + "|missing"); return; }
        records.Add(directory + "|directory");
        foreach (string path in Directory.GetFileSystemEntries(directory)) {
            SafePath.Local(path); string name = Path.GetFileName(path);
            bool resources = Path.GetFileName(directory) == "resources";
            if (resources && (name == RepairTransaction.JournalName || (name.StartsWith(".vencord-auto-update.",StringComparison.Ordinal) && name.EndsWith(".loader.tmp",StringComparison.Ordinal)))) continue;
            if (SafePath.Same(directory,ignoreArchivesIn) && (name == "app.asar" || name == "_app.asar")) continue;
            if (Directory.Exists(path)) WalkMetadata(path,ignoreArchivesIn,records,depth+1);
            else records.Add(SafePath.Metadata(path));
        }
    }
    static string EnvironmentMetadata(string root,string resources,string dist) {
        StringBuilder value = new StringBuilder(MetadataTree(root,resources));
        foreach (string name in new [] { "patcher.js", "preload.js", "renderer.js", "renderer.css" }) value.AppendLine(SafePath.Metadata(Path.Combine(dist,name)));
        return SafePath.TextHash(value.ToString());
    }
    internal static string GetMetadataSignature(string root, string executable, string dist) {
        root = SafePath.Local(root); dist = SafePath.Local(dist); ValidateExecutable(executable);
        StringBuilder s = new StringBuilder(); List<string> directories = Versions(root);
        foreach (string directory in directories) s.AppendLine(SafePath.Metadata(directory));
        if (directories.Count != 0) {
            string latest = Latest(directories), resources = Path.Combine(latest,"resources");
            s.AppendLine(SafePath.Metadata(Path.Combine(latest,executable)));
            // Do not include the resources directory timestamp: helper journal/temp writes must not change generation metadata.
            SafePath.Local(resources);
            foreach (string name in new [] { "app.asar", "_app.asar", "app", "app.asar.unpacked", "_app.asar.unpacked" }) s.AppendLine(SafePath.Metadata(Path.Combine(resources,name)));
        }
        s.AppendLine(SafePath.Metadata(Path.Combine(root,"Update.exe")));
        foreach (string name in new [] { "patcher.js", "preload.js", "renderer.js", "renderer.css" }) s.AppendLine(SafePath.Metadata(Path.Combine(dist,name)));
        return SafePath.TextHash(s.ToString());
    }
    internal static Installation Inspect(string root, string executable, string dist) { return InspectSelected(root,executable,dist,null); }
    internal static Installation InspectVersion(string root,string executable,string dist,string version) {
        System.Version parsed;
        if(!System.Version.TryParse(version,out parsed) || parsed.Build<0 || parsed.ToString()!=version) throw new ArgumentException("A canonical numeric version is required.");
        return InspectSelected(root,executable,dist,version);
    }
    internal static IList<Installation> InspectVersions(string root,string executable,string dist) {
        List<Installation> result=new List<Installation>();
        foreach(string directory in Versions(SafePath.Local(root))) result.Add(InspectVersion(root,executable,dist,Path.GetFileName(directory).Substring(4)));
        return result;
    }
    static Installation InspectSelected(string root,string executable,string dist,string version) {
        Installation result = new Installation { Root = root, Executable = executable, Dist = dist };
        try {
            result.Root = SafePath.Local(root); result.Dist = SafePath.Local(dist); ValidateExecutable(executable);
            List<string> versions = Versions(result.Root);
            if (versions.Count == 0) { result.Kind = InstallationKind.Unsettled; result.Reason = "No numeric app version directory exists."; return result; }
            string latest = version==null?Latest(versions):Path.Combine(result.Root,"app-"+version);
            if(!versions.Contains(latest)) { result.Kind=InstallationKind.Unsettled;result.Reason="Selected version is missing.";return result; }
            result.Version = Path.GetFileName(latest).Substring(4); result.Resources = Path.Combine(latest,"resources");
            result.MetadataSignature = GetMetadataSignature(result.Root,executable,result.Dist);
            result.EnvironmentSignature = EnvironmentMetadata(result.Root,result.Resources,result.Dist);
            if (!File.Exists(Path.Combine(root,"Update.exe")) || !File.Exists(Path.Combine(latest,executable)) || !Directory.Exists(result.Resources)) { result.Kind = InstallationKind.Unsettled; result.Reason = "Installation files are incomplete."; return result; }
            SafePath.RequireFile(Path.Combine(root,"Update.exe")); SafePath.RequireFile(Path.Combine(latest,executable));
            string app = Path.Combine(result.Resources,"app.asar"), original = Path.Combine(result.Resources,"_app.asar");
            foreach (string name in new [] { "app", "app.asar.unpacked", "_app.asar.unpacked" }) if (Directory.Exists(Path.Combine(result.Resources,name)) || File.Exists(Path.Combine(result.Resources,name))) throw new IOException("Unsupported directory/unpacked installation layout.");
            if (Directory.Exists(app) || Directory.Exists(original)) throw new IOException("Directory-form archives are unsupported.");
            bool hasApp = File.Exists(app), hasOriginal = File.Exists(original);
            if (hasOriginal) {
                using (AsarArchive archive = new AsarArchive(original)) if (!archive.IsStock()) throw new IOException("_app.asar is not a recognized original bootstrap.");
                result.OriginalHash = SafePath.Hash(original);
            }
            if (!hasApp) {
                result.Kind = hasOriginal ? InstallationKind.Interrupted : InstallationKind.Unsettled;
                result.Reason = hasOriginal ? "Valid original present; loader is missing." : "Both archives are missing.";
            } else using (AsarArchive archive = new AsarArchive(app)) {
                if (archive.IsStock()) {
                    if (hasOriginal) throw new IOException("Both archive paths contain originals; preserving ambiguous state.");
                    result.OriginalHash = SafePath.Hash(app); result.Kind = InstallationKind.Stock; result.Reason = "Recognized stock bootstrap.";
                } else if (hasOriginal && archive.IsLoader(result.Dist)) {
                    result.LoaderHash = SafePath.Hash(app); result.Kind = InstallationKind.Healthy; result.Reason = "Validated loader and original.";
                } else throw new IOException("Unknown loader or missing valid original; run the official Vencord installer.");
            }
            if (result.MetadataSignature != GetMetadataSignature(result.Root,executable,result.Dist) || result.EnvironmentSignature != EnvironmentMetadata(result.Root,result.Resources,result.Dist)) { result.Kind = InstallationKind.Unsettled; result.Reason = "Installation changed during inspection."; }
        } catch (Exception e) {
            if (!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is ArgumentException) && !(e is NotSupportedException)) throw;
            result.Kind = InstallationKind.Unsupported; result.Reason = e.Message;
        }
        return result;
    }
    static void ValidateExecutable(string executable) {
        if (executable != "Discord.exe" && executable != "DiscordPTB.exe" && executable != "DiscordCanary.exe") throw new IOException("Unsupported channel executable.");
    }
    static List<string> Versions(string root) {
        List<string> result = new List<string>();
        if (!Directory.Exists(root)) return result;
        foreach (string directory in Directory.GetDirectories(root,"app-*",SearchOption.TopDirectoryOnly)) {
            System.Version version;
            string suffix = Path.GetFileName(directory).Substring(4);
            if (!System.Version.TryParse(suffix,out version) || version.Build < 0) continue;
            SafePath.Local(directory); result.Add(directory);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase); return result;
    }
    static string Latest(List<string> versions) {
        string latest = null; System.Version maximum = null;
        foreach (string directory in versions) {
            System.Version version = new System.Version(Path.GetFileName(directory).Substring(4));
            if (maximum != null && version.Equals(maximum)) throw new IOException("Ambiguous numeric version directories.");
            if (maximum == null || version.CompareTo(maximum) > 0) { latest = directory; maximum = version; }
        }
        return latest;
    }
}
}
