using System;
using System.Collections.Generic;
namespace VencordAutoUpdate {
internal static class TestRunner {
    static int failures;
    internal static void Run(string name, Action test) {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    internal static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int Main(string[] args) { if(args.Length==1 && args[0]=="--fixture-wait") {System.Threading.Thread.Sleep(30000);return 0;} if(args.Length==1 && args[0]=="--worker-tests") { WorkerTests.Run(); return failures==0?0:1; } ArchiveTests.Run(); RepairTests.Run(); BoundaryTests.Run(); PolicyTests.Run(); SupervisorTests.Run(); ProcessTests.Run(); RuntimeBoundaryTests.Run(); InstanceTests.Run(); WorkerTests.Run(); WorkPipeTests.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
}
}
