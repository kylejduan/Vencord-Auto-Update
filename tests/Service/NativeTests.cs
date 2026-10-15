using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    static partial void NativeTests() {
        string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vencord-Service-Test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try {
            string root=Path.Combine(fixture,"Discord");string dist=Path.Combine(fixture,"Vencord","dist");
            using(var changed=new AutoResetEvent(false)) using(var watches=new RootNotifications(fixture,new[]{root,dist},delegate{changed.Set();})) {
                watches.Refresh();Directory.CreateDirectory(root);Check(changed.WaitOne(5000),"missing root parent notification");watches.Refresh();
                while(changed.WaitOne(0)){}File.WriteAllText(Path.Combine(root,"app.asar"),"fixture");Check(changed.WaitOne(5000),"real file notification");
                Directory.Delete(root,true);Check(changed.WaitOne(5000),"root deletion");watches.Refresh();Directory.CreateDirectory(root);Check(changed.WaitOne(5000),"root recreation");watches.Refresh();
                string executable=Path.Combine(root,"Update.exe");File.Copy(typeof(ServiceTests).Assembly.Location,executable);
                using(var process=Process.Start(new ProcessStartInfo(executable,"--fixture"){UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true})) using(var exited=new ManualResetEvent(false)) {
                    try {using(var waits=OwnedProcessWaits.Capture(new[]{root},delegate{exited.Set();})) {Check(waits.Known && waits.Count==1,"exact owned Update process handle");process.StandardInput.WriteLine();Check(exited.WaitOne(5000),"real process exit wait");Check(process.WaitForExit(5000),"fixture ended");}}
                    finally {if(!process.HasExited){process.StandardInput.WriteLine();if(!process.WaitForExit(5000)){process.Kill();process.WaitForExit();}}}
                }
                while(changed.WaitOne(0)){}watches.Refresh();Thread.Sleep(300);
                using(var self=Process.GetCurrentProcess()) {
                    self.Refresh();int before=self.HandleCount;TimeSpan cpu=self.TotalProcessorTime;long notifications=watches.Events;
                    Thread.Sleep(2000);self.Refresh();Console.WriteLine("Nonprivileged fixture host idle: cpu_ms="+(self.TotalProcessorTime-cpu).TotalMilliseconds+" working_set="+self.WorkingSet64+" handles="+self.HandleCount+" notification_delta="+(watches.Events-notifications));Check(watches.Events==notifications,"idle watcher has no activity");
                    for(int i=0;i<50;i++) {using(var cycle=new RootNotifications(fixture,new[]{root,dist},delegate{})){cycle.Refresh();}var schedule=new BrokerSchedule();DateTime time=DateTime.UtcNow;schedule.Signal(time);Check(schedule.Take(time),"cycle dispatch");schedule.Complete(0,0,time);Check(schedule.Due==null,"cycle idle");using(var waits=OwnedProcessWaits.Capture(new[]{root},delegate{})){Check(waits.Known && waits.Count==0,"cycle process census empty");}}
                    GC.Collect();GC.WaitForPendingFinalizers();self.Refresh();Console.WriteLine("Nonprivileged fixture host 50-cycle handles: before="+before+" after="+self.HandleCount);Check(self.HandleCount<=before+8,"50-cycle bounded handles");
                }
            }
            string local=Path.Combine(fixture,"Local"),nested=Path.Combine(local,"Discord");Directory.CreateDirectory(nested);
            using(var changed=new AutoResetEvent(false)) using(var ancestor=new AutoResetEvent(false))
            using(var raw=new FileSystemWatcher(fixture){IncludeSubdirectories=false,NotifyFilter=NotifyFilters.LastWrite})
            using(var watches=new RootNotifications(fixture,new[]{nested},delegate{changed.Set();})) {
                raw.Changed+=delegate(object sender,FileSystemEventArgs e){if(String.Equals(e.FullPath,local,StringComparison.OrdinalIgnoreCase))ancestor.Set();};raw.EnableRaisingEvents=true;
                Check(watches.Refresh(),"nested root subscribed");Thread.Sleep(200);while(changed.WaitOne(0)){}
                string churn=Path.Combine(local,"unrelated.tmp");File.WriteAllText(churn,"x");File.Delete(churn);
                Check(ancestor.WaitOne(5000),"fixture produces the ancestor timestamp notification");
                Check(!changed.WaitOne(1500),"unrelated sibling churn under an ancestor stays idle");
                string asar=Path.Combine(nested,"app.asar");File.WriteAllText(asar,"fixture");Check(changed.WaitOne(5000),"nested root change still notifies");
                File.Delete(asar);Thread.Sleep(500);while(changed.WaitOne(0)){}
                Directory.Delete(nested);Check(changed.WaitOne(5000),"empty nested root deletion notifies");
            }
            string target=Path.Combine(fixture,"canary-target"),junction=Path.Combine(fixture,"DiscordCanary");Directory.CreateDirectory(target);Directory.CreateDirectory(root);
            using(var link=Process.Start(new ProcessStartInfo("cmd.exe","/c mklink /J \""+junction+"\" \""+target+"\""){UseShellExecute=false,CreateNoWindow=true})){link.WaitForExit();Check(link.ExitCode==0,"fixture junction root");}
            try {
                using(var changed=new AutoResetEvent(false)) using(var watches=new RootNotifications(fixture,new[]{root,junction,dist},delegate{changed.Set();})) {
                    watches.Refresh();while(changed.WaitOne(0)){}
                    File.WriteAllText(Path.Combine(root,"isolated.txt"),"fixture");Check(changed.WaitOne(5000),"unsupported sibling root leaves valid root subscribed");
                }
            } finally {Directory.Delete(junction);}
            string denied=Path.Combine(fixture,"DiscordPTB");Directory.CreateDirectory(denied);
            var rule=new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,System.Security.AccessControl.FileSystemRights.ListDirectory,System.Security.AccessControl.AccessControlType.Deny);
            var acl=Directory.GetAccessControl(denied);acl.AddAccessRule(rule);Directory.SetAccessControl(denied,acl);
            try {
                using(var changed=new AutoResetEvent(false)) using(var watches=new RootNotifications(fixture,new[]{root,denied,dist},delegate{changed.Set();})) {
                    Check(!watches.Refresh(),"unsubscribable root is reported for retry");while(changed.WaitOne(0)){}
                    File.WriteAllText(Path.Combine(root,"partial.txt"),"fixture");Check(changed.WaitOne(5000),"failed sibling subscription leaves valid root subscribed");
                }
            } finally {acl=Directory.GetAccessControl(denied);acl.RemoveAccessRule(rule);Directory.SetAccessControl(denied,acl);}
            Check(TrustedPaths.IsUnder(root,fixture) && !TrustedPaths.IsUnder(fixture+"-other",fixture),"path separator containment");
            bool refused=false;try{TrustedPaths.RequireLocal("\\\\server\\share");}catch(IOException){refused=true;}Check(refused,"network paths refused");
            refused=false;try{TrustedPaths.RequireProtectedBinary(typeof(ServiceTests).Assembly.Location);}catch(Exception e){refused=e is IOException || e is UnauthorizedAccessException;}Check(refused,"user writable/non ProgramFiles binary refused");
        } finally {Directory.Delete(fixture,true);}
    }
}
}
