using System;
using System.IO;
namespace VencordAutoUpdate {
internal static class WorkerTests {
    static void Seed(Fixtures f) {
        f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));
        new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());
        File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("new"));
    }
    sealed class DeferredHost:ISupervisorHost {
        internal DateTime Time=new DateTime(2026,1,1);
        public DateTime Now {get{return Time;}}
        public bool Interactive {get{return true;}}
        public ProcessSnapshot Snapshot(Channel channel) {var result=new ProcessSnapshot();result.Discord.Add(new ProcessIdentity(7,Time.AddMinutes(-4).Ticks,Path.Combine(channel.Root,"app-1.0.10",channel.Executable)));return result;}
        public bool Stop(Channel channel,ProcessSnapshot cohort) {throw new Exception("Deferred cohort must not be stopped");}
        public bool Launch(Channel channel) {throw new Exception("Deferred cohort must not launch");}
        public void Countdown(string channel,Func<bool> valid,Action<bool> finished) {throw new Exception("Established cohort must not prompt");}
        public void Notify(string message) {}
    }
    internal static void Run() {
        TestRunner.Run("real deferred channel outcomes identify every known-root mask",delegate {
            foreach(int mask in new[]{1,2,3,4,5,6,7})using(var f=new Fixtures()) {
                Seed(f);var channels=new System.Collections.Generic.List<Channel>();string[] names={"Discord","DiscordPTB","DiscordCanary"};
                for(int i=0;i<3;i++)if((mask & (1<<i))!=0) {
                    string root=Path.Combine(f.Base,names[i]),version=Path.Combine(root,"app-1.0.10"),resources=Path.Combine(version,"resources");Directory.CreateDirectory(resources);
                    File.WriteAllText(Path.Combine(root,"Update.exe"),"fixture");File.WriteAllText(Path.Combine(version,names[i]+".exe"),"fixture");File.WriteAllBytes(Path.Combine(resources,"app.asar"),Fixtures.Stock("root-mask-fixture"));
                    channels.Add(new Channel(root,names[i]+".exe"));
                }
                var host=new DeferredHost();var supervisor=new Supervisor(channels,f.Dist,f.Cache,host);var batch=new WorkerBatch(supervisor);batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);host.Time=host.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(10));
                TestRunner.Assert((int?)batch.Result==new[]{0,65,66,67,68,69,70,71}[mask],"deferred known-root identity lost: "+mask);
            }
        });
        TestRunner.Run("healthy worker finishes after cache even with running Discord",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));
            var h=new FakeHost();h.Running(f.Root,7,1);var s=SupervisorTests.Make(f,h);
            var batch=new WorkerBatch(s);batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);
            TestRunner.Assert(batch.Result==WorkerResult.Done && Directory.GetFiles(Path.Combine(f.Cache,"cache")).Length>0,"healthy work stayed resident or missed cache");
        }});
        TestRunner.Run("deferred worker returns durable Deferred while exact cohort runs",delegate {using(var f=new Fixtures()) {
            Seed(f);var h=new FakeHost();h.Running(f.Root,7,181);var s=SupervisorTests.Make(f,h);
            var batch=new WorkerBatch(s);batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);
            h.Time=h.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(10));
            TestRunner.Assert((int?)batch.Result==65 && new StateStore(f.Cache).Find(f.Inspect().Generation).Deferred && h.Stops==0,"deferred work did not exit safely");
        }});
        TestRunner.Run("stable unsupported bytes refuse without mutation",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(";unknown()"));f.Write("_app.asar",Fixtures.Stock("old"));
            var h=new FakeHost();var s=SupervisorTests.Make(f,h);var batch=new WorkerBatch(s);batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);
            TestRunner.Assert(!batch.Result.HasValue,"unsettled refusal treated as permanent");
            h.Time=h.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(10));
            TestRunner.Assert(batch.Result==WorkerResult.Refused && h.Stops==0,"unsupported bytes not refused");
        }});
        TestRunner.Run("transient unknown census expires Retry and next UI batch can run",delegate {using(var f=new Fixtures()) {
            Seed(f);var h=new FakeHost();h.Processes.Known=false;var s=SupervisorTests.Make(f,h);var batch=new WorkerBatch(s);
            batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);batch.Step(TimeSpan.FromSeconds(180));
            TestRunner.Assert(batch.Result==WorkerResult.Retry && h.Stops==0,"transient batch not bounded");
            h.Processes.Known=true;batch.Start(TimeSpan.FromSeconds(181));batch.Step(TimeSpan.FromSeconds(181));h.Time=h.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(191));batch.Step(TimeSpan.FromSeconds(192));
            TestRunner.Assert(batch.Result==WorkerResult.Done && f.Inspect().Kind==InstallationKind.Healthy,"UI stranded subsequent work after expiry");
        }});
        TestRunner.Run("failed batch abort acknowledges Retry and permits another UI request",delegate {using(var f=new Fixtures()) {
            Seed(f);var h=new FakeHost();var s=SupervisorTests.Make(f,h);var batch=new WorkerBatch(s);
            batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);batch.Abort();batch.Step(TimeSpan.FromSeconds(1));
            TestRunner.Assert(batch.Result==WorkerResult.Retry && !batch.Running,"aborted batch remained active");
            batch.Start(TimeSpan.FromSeconds(2));h.Time=h.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(12));batch.Step(TimeSpan.FromSeconds(13));
            TestRunner.Assert(batch.Result==WorkerResult.Done,"error left the only UI supervisor stopped");
        }});
        TestRunner.Run("expiry cancels countdown and rejects late consent",delegate {using(var f=new Fixtures()) {
            Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);var s=SupervisorTests.Make(f,h);var batch=new WorkerBatch(s);
            batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);h.Time=h.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(10));
            TestRunner.Assert(h.Countdowns==1,"countdown missing");batch.Step(TimeSpan.FromSeconds(180));h.Finish(true);
            TestRunner.Assert(batch.Result==WorkerResult.Retry && h.Stops==0 && new StateStore(f.Cache).Find(f.Inspect().Generation).Deferred,"expired consent survived");
        }});
        TestRunner.Run("expiry drains an entered archive transaction",delegate {using(var f=new Fixtures()) {
            Seed(f);var h=new FakeHost();var s=SupervisorTests.Make(f,h);var batch=new WorkerBatch(s);bool entered=false;
            h.OnSnapshot=delegate {if(!entered && File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName))) {entered=true;batch.Step(TimeSpan.FromSeconds(180));TestRunner.Assert(!batch.Result.HasValue,"busy transaction acknowledged before drain");}};
            batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);h.Time=h.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(10));batch.Step(TimeSpan.FromSeconds(180));
            TestRunner.Assert(entered && batch.Result==WorkerResult.Retry && f.Inspect().Kind==InstallationKind.Healthy,"expiry interrupted transaction");
        }});
        TestRunner.Run("deadline reached inside transaction prevents a second channel transaction",delegate {using(var f=new Fixtures()) {
            Seed(f);string ptb=Path.Combine(f.Base,"DiscordPTB"),resources=Path.Combine(ptb,"app-1.0.10","resources");
            Directory.CreateDirectory(resources);File.WriteAllText(Path.Combine(ptb,"Update.exe"),"fixture");File.WriteAllText(Path.Combine(ptb,"app-1.0.10","DiscordPTB.exe"),"fixture");File.WriteAllBytes(Path.Combine(resources,"app.asar"),Fixtures.Stock("ptb"));
            var h=new FakeHost();var s=new Supervisor(new[]{new Channel(f.Root,"Discord.exe"),new Channel(ptb,"DiscordPTB.exe")},f.Dist,f.Cache,h);
            TimeSpan clock=TimeSpan.Zero;var batch=new WorkerBatch(s,()=>clock);batch.Start(clock);batch.Step(clock);h.Time=h.Time.AddSeconds(10);clock=TimeSpan.FromSeconds(10);
            h.OnSnapshot=delegate {if(File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName)))clock=TimeSpan.FromSeconds(180);};
            batch.Step(clock);
            TestRunner.Assert(batch.Result==WorkerResult.Retry && f.Inspect().Kind==InstallationKind.Healthy && Installation.Inspect(ptb,"DiscordPTB.exe",f.Dist).Kind==InstallationKind.Stock,"deadline started a later channel transaction or returned Done");
        }});
        TestRunner.Run("Done waits for persisted delayed restart verification",delegate {using(var f=new Fixtures()) {
            Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);var s=SupervisorTests.Make(f,h);var batch=new WorkerBatch(s);
            batch.Start(TimeSpan.Zero);batch.Step(TimeSpan.Zero);h.Time=h.Time.AddSeconds(10);batch.Step(TimeSpan.FromSeconds(10));h.Finish(true);
            batch.Step(TimeSpan.FromSeconds(11));TestRunner.Assert(!batch.Result.HasValue,"Done before delayed verification");
            h.Time=h.Time.AddSeconds(30);batch.Step(TimeSpan.FromSeconds(40));
            TestRunner.Assert(batch.Result==WorkerResult.Done && new StateStore(f.Cache).Find(f.Inspect().Generation).VerifyAfter==0,"verification not completed");
        }});
    }
}
}
