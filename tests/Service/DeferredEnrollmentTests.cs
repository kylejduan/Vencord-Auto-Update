using System;
using System.IO;
using System.Diagnostics;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    static void DeferredEnrollmentTests() {
        DeferredEnrollment(68,false,"healthy Stable remains open");
        DeferredEnrollment(68,true,"Canary exited before handoff census");
        DeferredEnrollment(69,false,"two deferred roots exit independently");
    }
    static void DeferredEnrollment(int result,bool early,string reason) {
        string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vencord-Service-Test-"+Guid.NewGuid().ToString("N"));
        string[] roots={Path.Combine(fixture,"Discord"),Path.Combine(fixture,"DiscordPTB"),Path.Combine(fixture,"DiscordCanary"),Path.Combine(fixture,"Vencord","dist")};
        var launcher=new FakeLauncher();UserEnrollment enrollment=null;Process stable=null,canary=null;
        try {
            foreach(int index in new[]{0,2}){Directory.CreateDirectory(roots[index]);File.Copy(typeof(ServiceTests).Assembly.Location,Path.Combine(roots[index],"Update.exe"));}
            stable=Process.Start(new ProcessStartInfo(Path.Combine(roots[0],"Update.exe"),"--fixture"){UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true});
            canary=Process.Start(new ProcessStartInfo(Path.Combine(roots[2],"Update.exe"),"--fixture"){UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true});
            enrollment=new UserEnrollment(new UserSession(1,"fixture-only",fixture,roots),launcher,delegate(string e){throw new Exception(e);},null,delegate{});
            enrollment.Signal();Check(launcher.Launched.WaitOne(5000),reason+": initial worker");DateTime? launch=enrollment.LastLaunch;
            if(early){canary.StandardInput.WriteLine();Check(canary.WaitForExit(5000),"Canary exits before worker result");}
            launcher.Worker.Complete(result);
            if(!early){canary.StandardInput.WriteLine();Check(canary.WaitForExit(5000),"Canary cohort exits");}
            Check(launcher.Launched.WaitOne(12000),reason+": actionable root launches followup");launch=enrollment.LastLaunch;
            Check(!stable.HasExited && launcher.Count==2,reason+": unrelated Stable neither blocks nor closes");launcher.Worker.Complete(0);
            enrollment.Dispose();enrollment=null;
            using(var self=Process.GetCurrentProcess()) {
                self.Refresh();int before=self.HandleCount;
                for(int i=0;i<50;i++)using(var waits=DeferredProcessWaits.Capture(roots,5,delegate{})){Check(waits.Known && waits.LiveMask==1 && waits.AnyExited,"hinted cohorts retain root identity and empty handoff");}
                GC.Collect();GC.WaitForPendingFinalizers();self.Refresh();Check(self.HandleCount<=before+8,"root grouped native handles are disposed");
            }
            Console.WriteLine("PASS native deferred-root enrollment: "+reason);
        }finally {
            if(enrollment!=null){enrollment.StopDispatch();if(launcher.StopWorker!=null)launcher.StopWorker.Complete(0);if(launcher.Worker!=null)launcher.Worker.Complete(0);enrollment.Dispose();}
            foreach(var process in new[]{stable,canary})if(process!=null){if(!process.HasExited){process.StandardInput.WriteLine();if(!process.WaitForExit(5000))throw new Exception("Owned fixture failed to exit; retain data");}process.Dispose();}
            launcher.Launched.Dispose();Directory.Delete(fixture,true);
        }
    }
}
}
