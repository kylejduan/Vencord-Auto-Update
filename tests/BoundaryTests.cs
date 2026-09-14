using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
namespace VencordAutoUpdate {
internal static class BoundaryTests {
    static ValidatedLoader Cache(Fixtures f) {
        f.Write("app.asar",f.Loader("")); f.Write("_app.asar",Fixtures.Stock("old"));
        ValidatedLoader loader = new LoaderCache(f.Cache).Capture(f.Inspect());
        File.Delete(Path.Combine(f.Resources,"app.asar")); File.Delete(Path.Combine(f.Resources,"_app.asar")); return loader;
    }
    static void AssertLoaderPathIndependent(Fixtures f,string target,bool accepted) {
        byte[] bytes = Fixtures.Archive("{\"name\":\"discord\",\"main\":\"index.js\"}","index.js","require(" + new JavaScriptSerializer().Serialize(target) + ")");
        string prior = Environment.CurrentDirectory;
        try {
            foreach (string directory in new [] { f.Base, f.Root }) {
                Environment.CurrentDirectory = directory; bool valid = true;
                try { new ValidatedLoader(bytes,f.Dist); } catch (IOException) { valid = false; }
                TestRunner.Assert(valid == accepted,"loader acceptance depends on relative path resolution");
            }
        } finally { Environment.CurrentDirectory = prior; }
    }
    internal static void Run() {
        TestRunner.Run("root-relative loader rejected in matching working directory", delegate { using (Fixtures f = new Fixtures()) {
            AssertLoaderPathIndependent(f,Path.Combine(f.Dist,"patcher.js").Substring(2),false);
        } });
        TestRunner.Run("drive-relative loader rejected in matching working directory", delegate { using (Fixtures f = new Fixtures()) {
            AssertLoaderPathIndependent(f,Path.GetPathRoot(f.Base).Substring(0,2) + "Vencord\\dist\\patcher.js",false);
        } });
        TestRunner.Run("absolute loader valid regardless of working directory", delegate { using (Fixtures f = new Fixtures()) {
            AssertLoaderPathIndependent(f,Path.Combine(f.Dist,"patcher.js"),true);
            AssertLoaderPathIndependent(f,Path.Combine(f.Dist,"patcher.js").Replace('\\','/'),true);
        } });
        TestRunner.Run("bounded file reads reject oversized content", delegate { using (Fixtures f = new Fixtures()) {
            string path = Path.Combine(f.Base,"bounded"); File.WriteAllBytes(path,new byte[100]);
            bool rejected = false; try { SafePath.ReadBounded(path,10); } catch (IOException) { rejected = true; }
            TestRunner.Assert(rejected,"oversized file read into memory");
        } });
        TestRunner.Run("loader trailing data and extra directories rejected", delegate { using (Fixtures f = new Fixtures()) {
            byte[] loader = f.Loader("");
            byte[] trailing = new byte[loader.Length+1]; Buffer.BlockCopy(loader,0,trailing,0,loader.Length);
            int headerLength = BitConverter.ToInt32(loader,12), start = 8 + BitConverter.ToInt32(loader,4);
            string header = Encoding.UTF8.GetString(loader,16,headerLength);
            header = header.Substring(0,header.Length-2) + ",\"extra\":{\"files\":{}}}}";
            byte[] data = new byte[loader.Length-start]; Buffer.BlockCopy(loader,start,data,0,data.Length);
            foreach (byte[] bytes in new [] { trailing, Fixtures.Raw(header,data) }) {
                bool rejected = false; try { new ValidatedLoader(bytes,f.Dist); } catch (IOException) { rejected = true; }
                TestRunner.Assert(rejected,"loader with extra archive content accepted");
            }
        } });
        TestRunner.Run("current bundle bootstrap recognized", delegate { using (Fixtures f = new Fixtures()) {
            f.Write("app.asar",Fixtures.Archive("{\"name\":\"discord\",\"main\":\"bundle.js\"}","bundle.js","// fixture"));
            TestRunner.Assert(f.Inspect().Kind == InstallationKind.Stock,"current bootstrap rejected");
        } });
        TestRunner.Run("identical originals retained as ambiguous", delegate { using (Fixtures f = new Fixtures()) {
            byte[] bytes = Fixtures.Stock("same"); f.Write("app.asar",bytes); f.Write("_app.asar",bytes);
            TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported,"two identical originals accepted");
        } });
        TestRunner.Run("loader never accepted as original", delegate { using (Fixtures f = new Fixtures()) {
            f.Write("app.asar",f.Loader("")); f.Write("_app.asar",f.Loader("")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported,"loader original accepted");
        } });
        TestRunner.Run("duplicate JSON property rejected", delegate { using (Fixtures f = new Fixtures()) {
            f.Write("app.asar",Fixtures.Archive("{\"name\":\"foreign\",\"name\":\"discord\",\"main\":\"bundle.js\"}","bundle.js","fixture"));
            TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported,"duplicate package property accepted");
        } });
        TestRunner.Run("archive ranges and entry names bounded", delegate { using (Fixtures f = new Fixtures()) {
            foreach (string header in new [] {
                "{\"files\":{\"package.json\":{\"offset\":\"99999\",\"size\":1}}}",
                "{\"files\":{\"package.json\":{\"offset\":\"0\",\"size\":1},\"bundle.js\":{\"offset\":\"0\",\"size\":1}}}",
                "{\"files\":{\"../package.json\":{\"offset\":\"0\",\"size\":1}}}",
                "{\"files\":{\"package.json\":{\"offset\":\"0\",\"size\":-1}}}",
                "{\"files\":{\"package.json\":{\"offset\":\"0\",\"size\":1,\"unpacked\":true}}}"
            }) {
                bool rejected = false; try { using (AsarArchive archive = new AsarArchive(Fixtures.Raw(header,new byte[] { 1 }))) { } } catch (IOException) { rejected = true; }
                TestRunner.Assert(rejected,"malformed range or path accepted");
            }
        } });
        TestRunner.Run("unknown and missing stock main refused", delegate { using (Fixtures f = new Fixtures()) {
            foreach (string main in new [] { "unknown.js", "bundle.js" }) {
                f.Write("app.asar",Fixtures.Archive("{\"name\":\"discord\",\"main\":\"" + main + "\"}","index.js","fixture"));
                TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported,"unknown or missing bootstrap accepted");
            }
        } });
        TestRunner.Run("loader expected path enforced", delegate { using (Fixtures f = new Fixtures()) {
            byte[] bytes = f.Loader(""); string other = Path.Combine(f.Base,"other"); Directory.CreateDirectory(other);
            bool rejected = false; try { new ValidatedLoader(bytes,other); } catch (IOException) { rejected = true; }
            TestRunner.Assert(rejected,"foreign patcher path accepted");
        } });
        TestRunner.Run("native no-replace rename preserves both files", delegate { using (Fixtures f = new Fixtures()) {
            string source = Path.Combine(f.Base,"source"), destination = Path.Combine(f.Base,"destination"); File.WriteAllText(source,"source"); File.WriteAllText(destination,"destination");
            bool refused = false; try { NativeFiles.MoveNew(source,destination); } catch (IOException) { refused = true; }
            TestRunner.Assert(refused && File.ReadAllText(source) == "source" && File.ReadAllText(destination) == "destination","native rename replaced destination");
        } });
        TestRunner.Run("ancestor junction refused without traversal", delegate { using (Fixtures f = new Fixtures()) {
            f.Write("app.asar",Fixtures.Stock("one")); string junction = Path.Combine(f.Base,"junction");
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe","/c mklink /J \"" + junction + "\" \"" + f.Root + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process p = Process.Start(info)) { p.StandardOutput.ReadToEnd(); string error = p.StandardError.ReadToEnd(); p.WaitForExit(); TestRunner.Assert(p.ExitCode == 0,"fixture junction creation failed: " + error); }
            try { TestRunner.Assert(Installation.Inspect(junction,"Discord.exe",f.Dist).Kind == InstallationKind.Unsupported,"junction root accepted"); }
            finally { Directory.Delete(junction); }
        } });
        TestRunner.Run("all journal checkpoints recover", delegate { foreach (string stop in new [] { "JournalWritten", "LoaderWritten", "LoaderMoved" }) using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock(stop); f.Write("app.asar",original);
            RepairResult crash = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == stop) throw new IOException("simulated interruption"); });
            TestRunner.Assert(!crash.Success,"checkpoint did not interrupt");
            RepairResult recovered = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },null);
            TestRunner.Assert(recovered.Success && File.ReadAllBytes(Path.Combine(f.Resources,"_app.asar")).SequenceEqual(original),"checkpoint recovery lost original");
        } });
        TestRunner.Run("gate checked before every transaction mutation", delegate { for (int failure = 1; failure <= 5; failure++) using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("gates"); f.Write("app.asar",original); int calls = 0;
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return ++calls != failure; },null);
            TestRunner.Assert(!result.Success && calls == failure,"mutation gate not checked");
            string preserved = Path.Combine(f.Resources,failure <= 3 ? "app.asar" : "_app.asar");
            TestRunner.Assert(File.ReadAllBytes(preserved).SequenceEqual(original),"gate failure lost original");
            TestRunner.Assert(File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName)) == (failure > 1),"journal gate incorrect");
            if (failure < 5) TestRunner.Assert(f.Inspect().Kind != InstallationKind.Healthy,"loader written after gate failed");
        } });
        TestRunner.Run("original source race refused", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("app.asar",Fixtures.Stock("old")); byte[] changed = Fixtures.Stock("race");
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "BeforeOriginalMove") f.Write("app.asar",changed); });
            TestRunner.Assert(!result.Success && File.ReadAllBytes(Path.Combine(f.Resources,"app.asar")).SequenceEqual(changed) && !File.Exists(Path.Combine(f.Resources,"_app.asar")),"changed source renamed");
        } });
        TestRunner.Run("foreign journal retained and refused", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("app.asar",Fixtures.Stock("one"));
            RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "OriginalMoved") throw new IOException("interrupt"); });
            string path = Path.Combine(f.Resources,RepairTransaction.JournalName), text = File.ReadAllText(path).Replace("VencordAutoUpdate","ForeignOwner"); File.WriteAllText(path,text);
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },null);
            TestRunner.Assert(!result.Success && File.ReadAllText(path) == text && !File.Exists(Path.Combine(f.Resources,"app.asar")),"foreign journal authorized recovery");
        } });
        TestRunner.Run("pending metadata notices modules and same-size writes", delegate { using (Fixtures f = new Fixtures()) {
            f.Write("app.asar",Fixtures.Stock("one")); string before = Installation.GetSettlingMetadataSignature(f.Root);
            string module = Path.Combine(f.Root,"app-1.0.10","modules","native.node"); Directory.CreateDirectory(Path.GetDirectoryName(module)); File.WriteAllText(module,"one");
            string added = Installation.GetSettlingMetadataSignature(f.Root); File.WriteAllText(module,"two"); File.SetLastWriteTimeUtc(module,DateTime.UtcNow.AddSeconds(3));
            string changed = Installation.GetSettlingMetadataSignature(f.Root);
            TestRunner.Assert(before != added && added != changed,"pending signature missed module activity");
        } });
        TestRunner.Run("module change during repair refuses mutation", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("app.asar",Fixtures.Stock("one"));
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "BeforeOriginalMove") File.WriteAllText(Path.Combine(f.Root,"module-update"),"in progress"); });
            TestRunner.Assert(!result.Success && !File.Exists(Path.Combine(f.Resources,"_app.asar")),"module change ignored during repair");
        } });
    }
}
}
