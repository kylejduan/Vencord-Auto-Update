using System;
using System.IO;
namespace VencordAutoUpdate {
internal static class ArchiveTests {
    internal static void Run() {
        TestRunner.Run("stock archive parses", delegate { using (Fixtures f = new Fixtures()) { f.Write("app.asar", Fixtures.Stock("one")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Stock, "expected recognized stock"); } });
        TestRunner.Run("known loader healthy requires valid original", delegate { using (Fixtures f = new Fixtures()) { f.Write("app.asar", f.Loader("")); f.Write("_app.asar", Fixtures.Stock("one")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Healthy, "expected healthy"); } });
        TestRunner.Run("loader without original refused", delegate { using (Fixtures f = new Fixtures()) { f.Write("app.asar", f.Loader("")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported, "missing original cannot be healthy"); } });
        TestRunner.Run("unknown loader code refused", delegate { using (Fixtures f = new Fixtures()) { f.Write("app.asar", f.Loader(";evil()")); f.Write("_app.asar", Fixtures.Stock("one")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported, "arbitrary JS accepted"); } });
        TestRunner.Run("required dist assets checked", delegate { using (Fixtures f = new Fixtures()) { f.Write("app.asar", f.Loader("")); f.Write("_app.asar", Fixtures.Stock("one")); File.Delete(Path.Combine(f.Dist,"renderer.css")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported, "missing asset accepted"); } });
        TestRunner.Run("truncated and excessive header rejected", delegate { using (Fixtures f = new Fixtures()) { foreach (byte[] bytes in new [] { new byte[3], new byte[] {4,0,0,0,255,255,255,127,4,0,0,0,0,0,0,0} }) { f.Write("app.asar",bytes); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported,"bad bounds accepted"); } } });
        TestRunner.Run("numeric newest version selected", delegate { using (Fixtures f = new Fixtures()) { Directory.CreateDirectory(Path.Combine(f.Root,"app-1.0.9","resources")); f.Write("app.asar",Fixtures.Stock("one")); TestRunner.Assert(f.Inspect().Resources == f.Resources,"lexical version ordering"); } });
        TestRunner.Run("two originals refused", delegate { using (Fixtures f = new Fixtures()) { f.Write("app.asar",Fixtures.Stock("one")); f.Write("_app.asar",Fixtures.Stock("two")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported,"ambiguous originals accepted"); } });
        TestRunner.Run("directory loader refused", delegate { using (Fixtures f = new Fixtures()) { Directory.CreateDirectory(Path.Combine(f.Resources,"app.asar")); TestRunner.Assert(f.Inspect().Kind == InstallationKind.Unsupported,"directory accepted"); } });
    }
}
}
