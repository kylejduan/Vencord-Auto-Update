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
        TestRunner.Run("child process churn keeps the session identity and stop ends the whole session",delegate {using(var f=new Fixtures()) {
            string exe=Path.Combine(f.Root,"app-1.0.10","Discord.exe");File.Copy(typeof(TestRunner).Assembly.Location,exe,true);
            using(Process p=Process.Start(new ProcessStartInfo(exe,"--fixture-parent"){UseShellExecute=false,CreateNoWindow=true})) {
                var inspector=new ProcessInspector();var channel=new Channel(f.Root,"Discord.exe");
                try {
                    ProcessSnapshot snapshot=null;var wait=Stopwatch.StartNew();
                    while(wait.ElapsedMilliseconds<5000){snapshot=inspector.Snapshot(channel);if(snapshot.Discord.Count==2)break;Thread.Sleep(100);}
                    TestRunner.Assert(snapshot.Discord.Count==2,"fixture parent and child not discovered");
                    var main=snapshot.Discord.Find(x=>x.Main);var child=snapshot.Discord.Find(x=>!x.Main);
                    TestRunner.Assert(main!=null && main.Pid==p.Id && child!=null,"main and child processes not distinguished");
                    using(Process c=Process.GetProcessById(child.Pid)){c.Kill();c.WaitForExit();}
                    var after=inspector.Snapshot(channel);
                    TestRunner.Assert(after.Discord.Count==1 && after.Cohort==snapshot.Cohort,"child exit changed the session identity");
                    TestRunner.Assert(inspector.Stop(channel,snapshot) && p.HasExited && inspector.Snapshot(channel).Quiet,"stop did not end the captured session after child churn");
                }finally {if(!p.HasExited)p.Kill();p.WaitForExit();foreach(var x in inspector.Snapshot(channel).Discord)try{using(var q=Process.GetProcessById(x.Pid))q.Kill();}catch(Exception){}}
            }
        }});
        TestRunner.Run("stop never ends a replacement session",delegate {using(var f=new Fixtures()) {
            string exe=Path.Combine(f.Root,"app-1.0.10","Discord.exe");File.Copy(typeof(TestRunner).Assembly.Location,exe,true);
            var inspector=new ProcessInspector();var channel=new Channel(f.Root,"Discord.exe");ProcessSnapshot first;
            using(Process a=Process.Start(new ProcessStartInfo(exe,"--fixture-wait"){UseShellExecute=false,CreateNoWindow=true})) {Thread.Sleep(300);first=inspector.Snapshot(channel);a.Kill();a.WaitForExit();}
            using(Process b=Process.Start(new ProcessStartInfo(exe,"--fixture-wait"){UseShellExecute=false,CreateNoWindow=true})) {
                try {Thread.Sleep(300);TestRunner.Assert(!inspector.Stop(channel,first) && !b.HasExited,"replacement session terminated");}
                finally {if(!b.HasExited)b.Kill();b.WaitForExit();}
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
