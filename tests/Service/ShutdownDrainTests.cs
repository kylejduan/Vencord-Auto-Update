using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    static void PendingDrainDeadlineTests() {
        long now=0;int hints=0,reports=0,waits=0;bool returned=false;
        ServiceDrain.Wait(delegate{return now>=240001;},delegate(int milliseconds){Check(milliseconds==10000,"pending drain advertises bounded SCM hint");hints++;},
            delegate{Check(now==225000,"overrun reports at original monotonic 225 second deadline");reports++;},delegate{return now;},delegate(int milliseconds){
                Check(!returned,"pending drain retains ownership before completion");waits++;Check(milliseconds<=5000 && hints==waits,"one aggregate bounded wait follows each hint");now+=milliseconds;
            });
        returned=true;Check(now>=240001 && reports==1 && hints>45 && waits>45,"late exit completes beyond installer bound with one overrun and continued hints");
        now=0;hints=0;reports=0;
        ServiceDrain.Wait(delegate{return true;},delegate{hints++;},delegate{reports++;},delegate{return now;},delegate{throw new Exception("idle wait");});
        Check(hints==0 && reports==0,"already drained service does not create idle wait or overrun");
        now=0;reports=0;
        ServiceDrain.Wait(delegate{return now>=225000;},delegate{},delegate{reports++;},delegate{return now;},delegate(int milliseconds){now+=milliseconds;});
        Check(reports==0,"exit observed at soft deadline completes cleanly without false overrun");
        now=900000;reports=0;bool first=false,second=false;
        ServiceDrain.Wait(delegate{first=now>=1000000;second=now>=1130000;return first && second;},delegate{},delegate{Check(now==1125000 && first && !second,"one drain deadline spans partial worker completion");reports++;},delegate{return now;},delegate(int milliseconds){now+=milliseconds;});
        Check(reports==1 && first && second,"nonzero monotonic clock origin and multiple pending workers complete");
        now=0;reports=0;int observations=0,cleanup=0;
        ServiceDrain.Wait(delegate{
            observations++;if(observations==1)throw new IOException("transient observation failure");
            if(now<230000)return false;
            cleanup++;if(cleanup==1)throw new IOException("transient cleanup failure");return true;
        },delegate{if(now==0)throw new IOException("transient pending report failure");},delegate{reports++;throw new IOException("diagnostic unavailable");},delegate{return now;},delegate(int milliseconds){now+=milliseconds;});
        Check(now==235000 && cleanup==2 && reports==1,"observation, pending hint, diagnostic and cleanup exceptions retain pending until a successful retry");
    }
    static void ShutdownDrainTests() {
        string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vencord-Shutdown-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var launcher=new FakeLauncher();var broker=new BrokerService();broker.AutoLog=false;
        UserEnrollment enrollment=null;Thread shutdown=null,duplicate=null;Exception failure=null;
        try {
            var roots=new[]{Path.Combine(fixture,"Discord"),Path.Combine(fixture,"DiscordPTB"),Path.Combine(fixture,"DiscordCanary"),Path.Combine(fixture,"Vencord","dist")};Directory.CreateDirectory(roots[0]);
            enrollment=new UserEnrollment(new UserSession(1,"shutdown-fixture",fixture,roots),launcher,delegate{},null,delegate{});
            enrollment.Signal();Check(launcher.Launched.WaitOne(5000),"shutdown fixture owns active worker");DateTime? launched=enrollment.LastLaunch;
            var users=(Dictionary<string,UserEnrollment>)typeof(BrokerService).GetField("users",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(broker);users.Add("shutdown-fixture",enrollment);
            // Only in-memory Framework state: never register/start a local SCM service.
            var field=typeof(ServiceBase).GetField("status",BindingFlags.Instance|BindingFlags.NonPublic);
            object status=field.GetValue(broker);var state=status.GetType().GetField("currentState",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);state.SetValue(status,4);field.SetValue(broker,status);
            shutdown=new Thread(delegate(){try{typeof(BrokerService).GetMethod("OnShutdown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(broker,null);}catch(Exception e){failure=e;}});shutdown.Start();
            var timer=System.Diagnostics.Stopwatch.StartNew();while(launcher.StopWorker==null && shutdown.IsAlive && timer.ElapsedMilliseconds<5000)Thread.Sleep(10);
            Thread.Sleep(100);
            Check(failure==null && shutdown.IsAlive,"OnShutdown stays active while owned repair remains");
            Check((int)state.GetValue(field.GetValue(broker))==3,"OnShutdown enters Framework STOP_PENDING before requesting time");
            duplicate=new Thread(delegate(){try{broker.Stop();}catch(Exception e){failure=e;}});duplicate.Start();Thread.Sleep(100);
            Check(duplicate.IsAlive && failure==null,"queued duplicate stop waits for the same owned drain");
            launcher.StopWorker.Complete(0);Check(!launcher.Worker.Disposed,"stop invoker completion retains active repair handle");
            launcher.Worker.Complete(0);Check(shutdown.Join(7000) && failure==null,"OnShutdown completes after actual owned exit");
            Check(duplicate.Join(7000) && failure==null,"duplicate stop completes without duplicate handle disposal");
            broker.Stop();Check(broker.ExitCode==0,"repeated already drained stop remains successful");
            Check((int)state.GetValue(field.GetValue(broker))==1 && broker.ExitCode==0,"successful shutdown reports stopped zero only after drain");
            Check(launcher.Worker.Disposed && launcher.StopWorker.Disposed && users.Count==0,"shutdown releases observed handles and ownership registry");enrollment=null;
        } finally {
            if(enrollment!=null){enrollment.StopDispatch();if(launcher.StopWorker!=null && !launcher.StopWorker.Disposed)launcher.StopWorker.Complete(0);if(launcher.Worker!=null && !launcher.Worker.Disposed)launcher.Worker.Complete(0);}
            if(shutdown!=null)shutdown.Join(7000);if(duplicate!=null)duplicate.Join(7000);
            if(enrollment!=null)try{enrollment.Dispose();}catch(ObjectDisposedException){}
            broker.Dispose();launcher.Launched.Dispose();Directory.Delete(fixture,true);
        }
    }
}
}
