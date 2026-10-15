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
            TestRunner.Assert(!File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName)),"recovered transaction left its journal at " + stop);
            TestRunner.Assert(Directory.GetFiles(f.Resources,".vencord-auto-update.*").Length == 0,"recovered transaction left staging at " + stop);
        } });
        TestRunner.Run("healthy supervision retires a completed journal", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("app.asar",Fixtures.Stock("completed"));
            RepairResult crash = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "LoaderMoved") throw new IOException("simulated interruption"); });
            TestRunner.Assert(!crash.Success && f.Inspect().Kind == InstallationKind.Healthy,"interruption did not leave healthy archives");
            string journal = Path.Combine(f.Resources,RepairTransaction.JournalName);
            TestRunner.Assert(File.Exists(journal),"fixture did not retain the completed journal");
            TestRunner.Assert(RepairTransaction.RetireCompletedJournal(f.Inspect(),loader) && !File.Exists(journal),"completed journal was not retired");
        } });
        TestRunner.Run("foreign or mismatched journals are never retired", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("app.asar",Fixtures.Stock("foreign"));
            RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "LoaderMoved") throw new IOException("simulated interruption"); });
            string journal = Path.Combine(f.Resources,RepairTransaction.JournalName);
            File.WriteAllText(journal,"{\"owner\":\"someone else\"}");
            TestRunner.Assert(!RepairTransaction.RetireCompletedJournal(f.Inspect(),loader) && File.Exists(journal),"foreign journal removed");
        } });
        TestRunner.Run("verified deletion removes only the validated file object", delegate { using (Fixtures f = new Fixtures()) {
            string path = Path.Combine(f.Resources,"journal.bin");
            File.WriteAllText(path,"keep");
            TestRunner.Assert(!NativeFiles.DeleteVerified(path,16384,delegate(byte[] b) { return System.Text.Encoding.UTF8.GetString(b) == "delete"; }) && File.Exists(path),"unvalidated bytes deleted");
            File.WriteAllText(path,"delete");
            TestRunner.Assert(NativeFiles.DeleteVerified(path,16384,delegate(byte[] b) { return System.Text.Encoding.UTF8.GetString(b) == "delete"; }) && !File.Exists(path),"validated bytes not deleted");
        } });
        TestRunner.Run("verified rename moves only the validated bytes", delegate { using (Fixtures f = new Fixtures()) {
            string source = Path.Combine(f.Resources,"source.bin"), target = Path.Combine(f.Resources,"target.bin"), other = Path.Combine(f.Resources,"other.bin");
            File.WriteAllText(source,"validated");
            string expected = SafePath.Hash(source);
            File.WriteAllText(source,"replaced after validation");
            bool refused = false; try { NativeFiles.MoveNewVerified(source,target,expected); } catch (IOException) { refused = true; }
            TestRunner.Assert(refused && File.Exists(source) && !File.Exists(target),"changed source was renamed");
            File.WriteAllText(source,"validated"); File.WriteAllText(other,"existing");
            refused = false; try { NativeFiles.MoveNewVerified(source,other,expected); } catch (IOException) { refused = true; }
            TestRunner.Assert(refused && File.ReadAllText(other) == "existing" && File.Exists(source),"verified rename replaced an existing destination");
            NativeFiles.MoveNewVerified(source,target,expected);
            TestRunner.Assert(!File.Exists(source) && File.ReadAllText(target) == "validated","verified rename did not move the file");
        } });
        TestRunner.Run("verified rename holds the source against writers", delegate { using (Fixtures f = new Fixtures()) {
            string source = Path.Combine(f.Resources,"held.bin");
            File.WriteAllText(source,"held"); string expected = SafePath.Hash(source);
            using (FileStream writer = new FileStream(source,FileMode.Open,FileAccess.Write,FileShare.ReadWrite | FileShare.Delete)) {
                bool refused = false; try { NativeFiles.MoveNewVerified(source,Path.Combine(f.Resources,"moved.bin"),expected); } catch (IOException e) { refused = e.Message.StartsWith("Cannot hold rename source"); }
                TestRunner.Assert(refused && File.Exists(source),"rename proceeded while another writer held the source");
            }
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
        TestRunner.Run("settling ignores Discord log appends but not installation files", delegate { using (Fixtures f = new Fixtures()) {
            string modules = Path.Combine(f.Root,"app-1.0.10","modules","discord_dispatch-1","discord_dispatch"); Directory.CreateDirectory(modules);
            string log = Path.Combine(modules,"dispatch.log"); File.WriteAllText(log,"start");
            string before = Installation.GetSettlingMetadataSignature(f.Root);
            File.AppendAllText(log,"\nmodule check"); File.WriteAllText(Path.Combine(f.Root,"SquirrelSetup.log"),"launch");
            TestRunner.Assert(Installation.GetSettlingMetadataSignature(f.Root) == before,"log append changed settling");
            File.WriteAllText(Path.Combine(modules,"index.js"),"module update");
            TestRunner.Assert(Installation.GetSettlingMetadataSignature(f.Root) != before,"module file change ignored");
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
