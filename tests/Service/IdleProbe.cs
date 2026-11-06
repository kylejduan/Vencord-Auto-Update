using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    sealed class IdleProcess:IWorkerProcess {
        public void OnExit(Action<int> callback){ThreadPool.QueueUserWorkItem(delegate{callback(0);});}
        public bool TryGetExitCode(out int result){result=0;return true;}
        public void Dispose(){}
    }
    sealed class IdleLauncher:IWorkerLauncher {
        int launches;
        internal int Count {get{return Interlocked.CompareExchange(ref launches,0,0);}}
        public IWorkerProcess Launch(UserSession user,bool stop){if(!stop)Interlocked.Increment(ref launches);return new IdleProcess();}
    }
    // Optional sustained native qualification. No production service or Discord is touched,
    // no forced GC masks handle growth, and one observer process owns the entire sample.
    static void IdleProbe(int seconds) {
        if(seconds<60 || seconds>3600)throw new ArgumentOutOfRangeException("seconds","Use 60..3600 seconds.");
        string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vencord-Service-Idle-"+Guid.NewGuid().ToString("N"));
        string local=Path.Combine(fixture,"Local"),discord=Path.Combine(local,"Discord"),dist=Path.Combine(fixture,"Roaming","Vencord","dist");
        Directory.CreateDirectory(discord);Directory.CreateDirectory(dist);
        string log=Path.Combine(discord,"dispatch.log");File.WriteAllText(log,"seed");
        var launcher=new IdleLauncher();UserEnrollment enrollment=null;string error=null;
        try {
            enrollment=new UserEnrollment(new UserSession(1,"fixture-only",fixture,new[]{discord,Path.Combine(local,"DiscordPTB"),Path.Combine(local,"DiscordCanary"),dist}),launcher,delegate(string message){Interlocked.CompareExchange(ref error,message,null);},null,delegate{});
            enrollment.Signal();var start=Stopwatch.StartNew();
            while(launcher.Count==0 && start.ElapsedMilliseconds<5000)Thread.Sleep(20);
            Check(launcher.Count==1,"idle probe startup reconciliation");
            for(int i=0;i<250;i++){File.AppendAllText(log,"append\n");Thread.Sleep(10);}
            string sibling=Path.Combine(local,"unrelated.tmp");
            for(int i=0;i<100;i++){File.WriteAllText(sibling,"x");File.Delete(sibling);Thread.Sleep(10);}
            Thread.Sleep(12000);
            Check(launcher.Count==1,"paced log and unrelated sibling events do not launch workers");
            using(var self=Process.GetCurrentProcess()) {
                var elapsed=Stopwatch.StartNew();int baseline=0,last=0;TimeSpan cpu=self.TotalProcessorTime;
                do {
                    Check(error==null,"idle probe broker reports no errors");self.Refresh();last=self.HandleCount;if(baseline==0)baseline=last;
                    Console.WriteLine("IDLE seconds="+(int)elapsed.Elapsed.TotalSeconds+" handles="+last+" private_bytes="+self.PrivateMemorySize64+" cpu_ms="+(self.TotalProcessorTime-cpu).TotalMilliseconds+" launches="+launcher.Count);
                    if(elapsed.Elapsed.TotalSeconds>=seconds)break;
                    Thread.Sleep(Math.Min(15000,Math.Max(1,(seconds-(int)elapsed.Elapsed.TotalSeconds)*1000)));
                }while(true);
                Check(launcher.Count==1,"sustained idle has no worker launches");
                Check(last<=baseline+8,"sustained idle handles remain bounded without forced collection");
            }
            File.WriteAllText(Path.Combine(discord,"module.js"),"real change");start.Restart();
            while(launcher.Count<2 && start.ElapsedMilliseconds<5000)Thread.Sleep(20);
            Check(launcher.Count>=2,"real installation changes still wake the broker after idle");
            Console.WriteLine("PASS sustained native idle qualification");
        }finally {
            if(enrollment!=null){enrollment.StopDispatch();if(!enrollment.WaitForDrain(5000))throw new Exception("Idle fixture did not drain; retaining evidence.");enrollment.Dispose();}
            Directory.Delete(fixture,true);
        }
    }
}
}
