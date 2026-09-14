using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace VencordAutoUpdate {
internal static class ProcessTests {
    internal static void Run() {
        TestRunner.Run("owned old-version process blocks only exact root",delegate {
            using(var f=new Fixtures()) {
                string old=Path.Combine(f.Root,"app-1.0.9");Directory.CreateDirectory(old);
                string exe=Path.Combine(old,"Discord.exe");File.Copy(typeof(TestRunner).Assembly.Location,exe);
                using(Process p=Process.Start(new ProcessStartInfo(exe,"--fixture-wait"){UseShellExecute=false,CreateNoWindow=true})) {
                    try {
                        Thread.Sleep(300);
                        var inspector=new ProcessInspector();var snapshot=inspector.Snapshot(new Channel(f.Root,"Discord.exe"));
                        TestRunner.Assert(snapshot.Known && snapshot.Discord.Exists(x=>x.Pid==p.Id) && !snapshot.Quiet,"old process invisible");
                        string other=f.Root+"Other";Directory.CreateDirectory(other);
                        TestRunner.Assert(inspector.Snapshot(new Channel(other,"Discord.exe")).Quiet,"prefix root leaked");
                        var captured=snapshot.Discord.Find(x=>x.Pid==p.Id);
                        TestRunner.Assert(captured.StartTicks==p.StartTime.ToUniversalTime().Ticks,"start identity missing");
                    } finally {if(!p.HasExited)p.Kill();p.WaitForExit();}
                    TestRunner.Assert(p.HasExited,"fixture process not cleaned");
                }
            }
        });
        TestRunner.Run("updater under fixture root blocks quiet snapshot",delegate {using(var f=new Fixtures()) {
            string exe=Path.Combine(f.Root,"Update.exe");File.Copy(typeof(TestRunner).Assembly.Location,exe,true);
            using(Process p=Process.Start(new ProcessStartInfo(exe,"--fixture-wait"){UseShellExecute=false,CreateNoWindow=true})) {
                try {Thread.Sleep(300);var snapshot=new ProcessInspector().Snapshot(new Channel(f.Root,"Discord.exe"));TestRunner.Assert(snapshot.Known && snapshot.Updater && !snapshot.Quiet,"updater invisible");}
                finally {if(!p.HasExited)p.Kill();p.WaitForExit();}
                TestRunner.Assert(p.HasExited,"updater fixture not cleaned");
            }
        }});
        TestRunner.Run("termination rejects changed captured start identity",delegate {using(var f=new Fixtures()) {
            string exe=Path.Combine(f.Root,"app-1.0.10","Discord.exe");File.Copy(typeof(TestRunner).Assembly.Location,exe,true);
            using(Process p=Process.Start(new ProcessStartInfo(exe,"--fixture-wait"){UseShellExecute=false,CreateNoWindow=true})) {
                try {
                    Thread.Sleep(300);var inspector=new ProcessInspector();var channel=new Channel(f.Root,"Discord.exe");var snapshot=inspector.Snapshot(channel);
                    var original=snapshot.Discord.Find(x=>x.Pid==p.Id);TestRunner.Assert(original!=null,"fixture not discovered");snapshot.Discord.Remove(original);snapshot.Discord.Add(new ProcessIdentity(p.Id,original.StartTicks-1,original.Path));
                    TestRunner.Assert(!inspector.Stop(channel,snapshot) && !p.HasExited,"changed identity terminated");
                }finally {if(!p.HasExited)p.Kill();p.WaitForExit();}
            }
        }});
    }
}
}
