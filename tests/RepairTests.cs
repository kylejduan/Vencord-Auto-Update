using System;
using System.IO;
using System.Linq;
namespace VencordAutoUpdate {
internal static class RepairTests {
    static ValidatedLoader Cache(Fixtures f) {
        f.Write("app.asar",f.Loader("")); f.Write("_app.asar",Fixtures.Stock("old"));
        ValidatedLoader loader = new LoaderCache(f.Cache).Capture(f.Inspect());
        File.Delete(Path.Combine(f.Resources,"app.asar")); File.Delete(Path.Combine(f.Resources,"_app.asar"));
        return loader;
    }
    internal static void Run() {
        TestRunner.Run("journal creation failure reports retained mutation", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("creation failure"); f.Write("app.asar",original);
            Installation candidate = f.Inspect(); string journal = Path.Combine(f.Resources,RepairTransaction.JournalName);
            RepairResult result = RepairTransaction.Repair(candidate,loader,delegate { return true; },delegate(string point) { if (point == "JournalCreated") throw new IOException("injected after journal CreateNew"); });
            TestRunner.Assert(!result.Success && result.Reason == "injected after journal CreateNew", "journal creation injection did not fail");
            TestRunner.Assert(File.Exists(journal) && new FileInfo(journal).Length == 0,"created journal evidence missing");
            TestRunner.Assert(File.ReadAllBytes(Path.Combine(f.Resources,"app.asar")).SequenceEqual(original) && f.Inspect().OriginalHash == candidate.OriginalHash,"journal failure changed original");
            TestRunner.Assert(result.Changed,"created journal reported mutation-free failure");
        } });
        TestRunner.Run("recovered staging creation failure reports retained mutation", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("staging failure"); f.Write("_app.asar",original);
            RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "JournalWritten") throw new IOException("prepare recoverable journal"); });
            Installation candidate = f.Inspect(); string journal = Path.Combine(f.Resources,RepairTransaction.JournalName);
            byte[] journalBefore = File.ReadAllBytes(journal); string temporary = Path.Combine(f.Resources,RepairJournal.Read(journal,candidate,loader).Temporary);
            RepairResult result = RepairTransaction.Repair(candidate,loader,delegate { return true; },delegate(string point) { if (point == "LoaderCreated") throw new IOException("injected after loader CreateNew"); });
            TestRunner.Assert(!result.Success && result.Reason == "injected after loader CreateNew","staging creation injection did not fail");
            TestRunner.Assert(File.Exists(temporary) && new FileInfo(temporary).Length == 0 && File.ReadAllBytes(journal).SequenceEqual(journalBefore),"staging/journal evidence missing or changed");
            TestRunner.Assert(!File.Exists(Path.Combine(f.Resources,"app.asar")) && File.ReadAllBytes(Path.Combine(f.Resources,"_app.asar")).SequenceEqual(original) && f.Inspect().OriginalHash == candidate.OriginalHash,"staging failure changed original");
            TestRunner.Assert(result.Changed,"created staging file reported mutation-free failure");
        } });
        TestRunner.Run("journal create collision is not a helper mutation", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("journal collision"); f.Write("app.asar",original);
            Installation candidate = f.Inspect(); string journal = Path.Combine(f.Resources,RepairTransaction.JournalName);
            RepairResult result = RepairTransaction.Repair(candidate,loader,delegate { File.WriteAllText(journal,"concurrent evidence"); return true; },null);
            TestRunner.Assert(!result.Success && !result.Changed,"journal collision reported helper creation");
            TestRunner.Assert(File.ReadAllText(journal) == "concurrent evidence" && File.ReadAllBytes(Path.Combine(f.Resources,"app.asar")).SequenceEqual(original) && f.Inspect().OriginalHash == candidate.OriginalHash,"journal collision lost evidence");
        } });
        TestRunner.Run("recovered staging collision is not a helper mutation", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("staging collision"); f.Write("_app.asar",original);
            RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "JournalWritten") throw new IOException("prepare recoverable journal"); });
            Installation candidate = f.Inspect(); string journal = Path.Combine(f.Resources,RepairTransaction.JournalName);
            byte[] journalBefore = File.ReadAllBytes(journal); string temporary = Path.Combine(f.Resources,RepairJournal.Read(journal,candidate,loader).Temporary);
            RepairResult result = RepairTransaction.Repair(candidate,loader,delegate { File.WriteAllText(temporary,"concurrent evidence"); return true; },null);
            TestRunner.Assert(!result.Success && !result.Changed,"staging collision reported helper creation");
            TestRunner.Assert(File.ReadAllText(temporary) == "concurrent evidence" && File.ReadAllBytes(journal).SequenceEqual(journalBefore) && File.ReadAllBytes(Path.Combine(f.Resources,"_app.asar")).SequenceEqual(original) && f.Inspect().OriginalHash == candidate.OriginalHash,"staging collision lost evidence");
        } });
        TestRunner.Run("repair preserves original byte for byte", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("new"); f.Write("app.asar",original);
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },null);
            TestRunner.Assert(result.Success && result.Changed,"repair not verified");
            TestRunner.Assert(File.ReadAllBytes(Path.Combine(f.Resources,"_app.asar")).SequenceEqual(original),"original bytes changed");
            TestRunner.Assert(f.Inspect().Kind == InstallationKind.Healthy,"loader not healthy");
        } });
        TestRunner.Run("healthy repair is mutation free", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("app.asar",loader.Bytes); f.Write("_app.asar",Fixtures.Stock("new"));
            string before = f.Inspect().MetadataSignature;
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { throw new Exception("unexpected mutation gate"); },null);
            TestRunner.Assert(result.Success && !result.Changed && f.Inspect().MetadataSignature == before,"healthy no-op failed");
        } });
        TestRunner.Run("missing loader with valid original recovers", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("_app.asar",Fixtures.Stock("built-in"));
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },null);
            TestRunner.Assert(result.Success && f.Inspect().Kind == InstallationKind.Healthy,"built-in interruption not recovered");
        } });
        TestRunner.Run("crash after original rename recovers on fresh inspection", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("crash"); f.Write("app.asar",original);
            RepairResult crash = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "OriginalMoved") throw new IOException("simulated crash"); });
            TestRunner.Assert(!crash.Success && !File.Exists(Path.Combine(f.Resources,"app.asar")) && File.Exists(Path.Combine(f.Resources,"_app.asar")),"crash did not occur between real renames");
            RepairResult recovery = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },null);
            TestRunner.Assert(recovery.Success && File.ReadAllBytes(Path.Combine(f.Resources,"_app.asar")).SequenceEqual(original),"journal recovery failed");
        } });
        TestRunner.Run("concurrent original destination retained", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("new"), concurrent = Fixtures.Stock("concurrent"); f.Write("app.asar",original);
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "BeforeOriginalMove") f.Write("_app.asar",concurrent); });
            TestRunner.Assert(!result.Success && File.ReadAllBytes(Path.Combine(f.Resources,"app.asar")).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(f.Resources,"_app.asar")).SequenceEqual(concurrent),"concurrent original overwritten");
        } });
        TestRunner.Run("concurrent loader destination retained", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] concurrent = Fixtures.Stock("concurrent"); f.Write("app.asar",Fixtures.Stock("new"));
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },delegate(string point) { if (point == "BeforeLoaderMove") f.Write("app.asar",concurrent); });
            TestRunner.Assert(!result.Success && File.ReadAllBytes(Path.Combine(f.Resources,"app.asar")).SequenceEqual(concurrent) && File.Exists(Path.Combine(f.Resources,"_app.asar")),"concurrent loader destination overwritten");
        } });
        TestRunner.Run("invalid earlier cache preserves bytes and falls through to a valid loader", delegate {
            using (Fixtures f = new Fixtures()) {
                ValidatedLoader expected = Cache(f);
                string rejected = Path.Combine(f.Cache, "loader-0000.asar");
                File.WriteAllText(rejected, "corrupt earlier cache entry");
                string reason;
                ValidatedLoader actual = new LoaderCache(f.Cache).Load(f.Dist, out reason);
                TestRunner.Assert(actual != null && actual.Hash == expected.Hash && reason == null, "invalid earlier cache hid valid loader");
                TestRunner.Assert(File.ReadAllText(rejected) == "corrupt earlier cache entry", "rejected entry changed");
            }
        });
        TestRunner.Run("all invalid cache entries are preserved with a useful reason", delegate {
            using (Fixtures f = new Fixtures()) {
                Directory.CreateDirectory(f.Cache);
                string first = Path.Combine(f.Cache, "loader-0000.asar");
                string second = Path.Combine(f.Cache, "loader-1111.asar");
                File.WriteAllText(first, "bad first");
                File.WriteAllText(second, "bad second");
                string reason;
                TestRunner.Assert(new LoaderCache(f.Cache).Load(f.Dist, out reason) == null && !String.IsNullOrEmpty(reason), "invalid entries accepted without diagnosis");
                TestRunner.Assert(File.ReadAllText(first) == "bad first" && File.ReadAllText(second) == "bad second", "invalid cache evidence changed");
            }
        });
        TestRunner.Run("invalid cache rejected", delegate { using (Fixtures f = new Fixtures()) {
            Cache(f); foreach (string file in Directory.GetFiles(f.Cache,"*.asar")) File.WriteAllText(file,"corrupt");
            string reason; TestRunner.Assert(new LoaderCache(f.Cache).Load(f.Dist,out reason) == null && !String.IsNullOrEmpty(reason),"corrupt cached loader accepted");
        } });
        TestRunner.Run("failed quiescence has no archive writes", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); byte[] original = Fixtures.Stock("new"); f.Write("app.asar",original);
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return false; },null);
            TestRunner.Assert(!result.Success && Directory.GetFiles(f.Resources).Length == 1 && File.ReadAllBytes(Path.Combine(f.Resources,"app.asar")).SequenceEqual(original),"failed gate changed files");
        } });
        TestRunner.Run("changing candidate refused", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("app.asar",Fixtures.Stock("one")); Installation prior = f.Inspect(); byte[] current = Fixtures.Stock("two"); f.Write("app.asar",current);
            RepairResult result = RepairTransaction.Repair(prior,loader,delegate { return true; },null);
            TestRunner.Assert(!result.Success && File.ReadAllBytes(Path.Combine(f.Resources,"app.asar")).SequenceEqual(current) && !File.Exists(Path.Combine(f.Resources,"_app.asar")),"stale candidate modified");
        } });
        TestRunner.Run("corrupt journal never authorizes repair", delegate { using (Fixtures f = new Fixtures()) {
            ValidatedLoader loader = Cache(f); f.Write("_app.asar",Fixtures.Stock("one")); File.WriteAllText(Path.Combine(f.Resources,RepairTransaction.JournalName),"{}");
            RepairResult result = RepairTransaction.Repair(f.Inspect(),loader,delegate { return true; },null);
            TestRunner.Assert(!result.Success && !File.Exists(Path.Combine(f.Resources,"app.asar")),"corrupt journal authorized mutation");
        } });
    }
}
}
