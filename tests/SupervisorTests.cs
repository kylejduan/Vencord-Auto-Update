using System;
using System.Collections.Generic;
using System.IO;
namespace VencordAutoUpdate {
internal sealed class FakeHost : ISupervisorHost {
    internal DateTime Time=new DateTime(2026,9,13,0,0,0,DateTimeKind.Utc);
    internal bool Unlocked=true,StopResult=true,LaunchResult=true;
    internal ProcessSnapshot Processes=new ProcessSnapshot();
    internal int Countdowns,Stops,Launches,Failures;
    internal Action<bool> Finish;
    internal Func<bool> Valid;
    internal Action BeforeStop,OnSnapshot;
    public DateTime Now {get{return Time;}}
    public bool Interactive {get{return Unlocked;}}
    public ProcessSnapshot Snapshot(Channel c){if(OnSnapshot!=null)OnSnapshot();return Processes;}
    public bool Stop(Channel c,ProcessSnapshot cohort){Stops++;if(BeforeStop!=null)BeforeStop();if(StopResult)Processes=new ProcessSnapshot();return StopResult;}
    public bool Launch(Channel c){Launches++;return LaunchResult;}
    public void Countdown(string c,Func<bool> valid,Action<bool> finished){Countdowns++;Valid=valid;Finish=finished;}
    public void Notify(string message,bool failure){if(failure)Failures++;}
    internal void Running(string root,int pid,double seconds) {Processes.Discord.Add(new ProcessIdentity(pid,Time.AddSeconds(-seconds).Ticks,Path.Combine(root,"app-1.0.10","Discord.exe")));}
}
internal static class SupervisorTests {
    internal static Supervisor Make(Fixtures f,FakeHost h) {return new Supervisor(new [] {new Channel(f.Root,"Discord.exe")},f.Dist,f.Cache,h);}
    static void Seed(Fixtures f) {
        f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));
        new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());
        File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("new"));
    }
    internal static void Run() {
        TestRunner.Run("persisted reservation cannot rearm after helper restart",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();TestRunner.Assert(h.Countdowns==1,"missing countdown");var loaded=new StateStore(f.Cache);TestRunner.Assert(loaded.Find(f.Inspect().Generation).Reserved,"reservation not persisted before display");var again=Make(f,h);again.Tick();h.Time=h.Time.AddSeconds(10);again.Tick();TestRunner.Assert(h.Countdowns==1,"restart prompted twice");}});
        TestRunner.Run("healthy polling performs no repeated state writes",delegate {using(var f=new Fixtures()){f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock(""));var h=new FakeHost();var s=Make(f,h);s.Tick();string file=Path.Combine(f.Cache,"state.json");TestRunner.Assert(!File.Exists(file),"healthy poll created state");var before=Directory.GetFiles(Path.Combine(f.Cache,"cache"));h.Time=h.Time.AddSeconds(60);s.Tick();TestRunner.Assert(!File.Exists(file) && Directory.GetFiles(Path.Combine(f.Cache,"cache")).Length==before.Length,"healthy writes repeated");}});
        TestRunner.Run("settled full exit invokes real transaction",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();var s=Make(f,h);s.Tick();TestRunner.Assert(f.Inspect().Kind==InstallationKind.Stock,"early repair");h.Time=h.Time.AddSeconds(10);s.Tick();TestRunner.Assert(f.Inspect().Kind==InstallationKind.Healthy,"real transaction failed");TestRunner.Assert(h.Stops==0 && h.Launches==0,"quiet repair launched Discord");}});
        TestRunner.Run("countdown cancellation survives polls and later exit repairs",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();h.Finish(false);h.Time=h.Time.AddSeconds(20);s.Tick();TestRunner.Assert(h.Stops==0 && h.Countdowns==1,"cancellation ignored");h.Processes=new ProcessSnapshot();s.Tick();TestRunner.Assert(f.Inspect().Kind==InstallationKind.Healthy,"deferred exit not repaired");}});
        TestRunner.Run("countdown new cohort prevents termination",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();h.Processes=new ProcessSnapshot();h.Running(f.Root,8,1);h.Finish(true);TestRunner.Assert(h.Stops==0 && f.Inspect().Kind==InstallationKind.Stock,"new cohort stopped");}});
        TestRunner.Run("disabled automatic preference defers that running generation permanently",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);var s=Make(f,h);s.SetPreferences(false,false);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();s.SetPreferences(false,true);h.Time=h.Time.AddSeconds(60);s.Tick();TestRunner.Assert(h.Countdowns==0,"enabling preference rearmed established generation");}});
        TestRunner.Run("missing cache never prompts or stops",delegate {using(var f=new Fixtures()){f.Write("app.asar",Fixtures.Stock(""));var h=new FakeHost();h.Running(f.Root,7,1);var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();TestRunner.Assert(h.Countdowns==0 && h.Stops==0,"pointless restart offered");}});
        TestRunner.Run("old version healthy loader seeds newest stock",delegate {using(var f=new Fixtures()){f.Write("app.asar",Fixtures.Stock("new"));string old=Path.Combine(f.Root,"app-1.0.9");Directory.CreateDirectory(Path.Combine(old,"resources"));File.WriteAllText(Path.Combine(old,"Discord.exe"),"fixture");File.WriteAllBytes(Path.Combine(old,"resources","app.asar"),f.Loader(""));File.WriteAllBytes(Path.Combine(old,"resources","_app.asar"),Fixtures.Stock("old"));var h=new FakeHost();var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();TestRunner.Assert(f.Inspect().Kind==InstallationKind.Healthy,"older healthy loader not seeded");}});
        TestRunner.Run("selected old version capture rejects source changes",delegate {using(var f=new Fixtures()){f.Write("app.asar",Fixtures.Stock("new"));string old=Path.Combine(f.Root,"app-1.0.9");Directory.CreateDirectory(Path.Combine(old,"resources"));File.WriteAllText(Path.Combine(old,"Discord.exe"),"fixture");string app=Path.Combine(old,"resources","app.asar");File.WriteAllBytes(app,f.Loader(""));File.WriteAllBytes(Path.Combine(old,"resources","_app.asar"),Fixtures.Stock("old"));var selected=Installation.InspectVersion(f.Root,"Discord.exe",f.Dist,"1.0.9");File.WriteAllBytes(Path.Combine(old,"resources","_app.asar"),Fixtures.Stock("changed"));bool refused=false;try{new LoaderCache(f.Cache).Capture(selected);}catch(IOException){refused=true;}TestRunner.Assert(refused,"changed old original accepted");File.WriteAllBytes(Path.Combine(old,"resources","_app.asar"),Fixtures.Stock("old"));selected=Installation.InspectVersion(f.Root,"Discord.exe",f.Dist,"1.0.9");File.WriteAllBytes(app,f.Loader(";unexpected()"));refused=false;try{new LoaderCache(f.Cache).Capture(selected);}catch(IOException){refused=true;}TestRunner.Assert(refused,"changed old loader accepted");}});
        TestRunner.Run("unknown process access and updater block all writes",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();h.Processes.Known=false;var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(20);s.Tick();TestRunner.Assert(f.Inspect().Kind==InstallationKind.Stock,"unknown access repaired");h.Processes.Known=true;h.Processes.Updater=true;s.Tick();h.Time=h.Time.AddSeconds(20);s.Tick();TestRunner.Assert(f.Inspect().Kind==InstallationKind.Stock,"updater ignored");}});
        TestRunner.Run("unsupported discovered root is refused without blocking other channels",delegate {using(var f=new Fixtures()){
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock(""));
            string target=Path.Combine(f.Base,"canary-target"),junction=Path.Combine(f.Base,"DiscordCanary");Directory.CreateDirectory(target);
            using(var link=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe","/c mklink /J \""+junction+"\" \""+target+"\""){UseShellExecute=false,CreateNoWindow=true})){link.WaitForExit();TestRunner.Assert(link.ExitCode==0,"fixture junction failed");}
            try {
                var canary=Channel.Discovered(junction,"DiscordCanary.exe");
                TestRunner.Assert(canary.Refusal!=null,"junction root accepted");
                var h=new FakeHost();var s=new Supervisor(new [] {new Channel(f.Root,"Discord.exe"),canary},f.Dist,f.Cache,h);s.Tick();
                TestRunner.Assert(s.Channels[0].Outcome==WorkerResult.Done && s.Channels[1].Outcome==WorkerResult.Refused,"channel outcomes were not independent");
                TestRunner.Assert(s.Outcome==WorkerResult.Refused,"batch did not complete with the refused channel");
            } finally {Directory.Delete(junction);}
        }});
        TestRunner.Run("full generation history retires only obsolete versions",delegate {using(var f=new Fixtures()){
            var store=new StateStore(f.Cache);
            for(int i=0;i<511;i++)store.Value.Generations.Add(new GenerationState{Key=f.Root+"|9.9."+i+"|h",Cohort=""});
            store.Value.Generations.Add(new GenerationState{Key=f.Root+"|1.0.10|current",Cohort=""});
            TestRunner.Assert(store.Save(),"fixture history not saved");
            var refused=new StateStore(f.Cache);
            TestRunner.Assert(!refused.Add(new GenerationState{Key="next",Cohort=""}) && !refused.Writable,"full history accepted without retirement");
            var retiring=new StateStore(f.Cache);
            TestRunner.Assert(retiring.Add(new GenerationState{Key=f.Root+"|1.0.11|next",Cohort=""},g=>Supervisor.Obsolete(g,DateTime.UtcNow)),"obsolete history not retired");
            var reloaded=new StateStore(f.Cache);
            TestRunner.Assert(reloaded.Writable && reloaded.Value.Generations.Count==2 && reloaded.Find(f.Root+"|1.0.10|current")!=null,"retirement removed a generation whose version still exists");
        }});
        TestRunner.Run("recently spent or failed history is never retired",delegate {using(var f=new Fixtures()){
            DateTime now=DateTime.UtcNow;string gone=f.Root+"|9.9.9|h";
            TestRunner.Assert(Supervisor.Obsolete(new GenerationState{Key=gone,Cohort=""},now),"unspent removed version not retired");
            TestRunner.Assert(!Supervisor.Obsolete(new GenerationState{Key=gone,Cohort="",Reserved=true},now),"reserved allowance without attempt time retired");
            TestRunner.Assert(!Supervisor.Obsolete(new GenerationState{Key=gone,Cohort="",Attempts=1,LastAttempt=now.AddDays(-1).Ticks},now),"recent restart allowance retired");
            TestRunner.Assert(Supervisor.Obsolete(new GenerationState{Key=gone,Cohort="",Attempts=1,LastAttempt=now.AddDays(-31).Ticks},now),"stale restart allowance kept forever");
            TestRunner.Assert(!Supervisor.Obsolete(new GenerationState{Key=gone,Cohort="",VerificationFailed=true},now),"failed verification retired");
            TestRunner.Assert(!Supervisor.Obsolete(new GenerationState{Key=f.Root+"|1.0.10|h",Cohort=""},now),"existing version retired");
            TestRunner.Assert(!Supervisor.Obsolete(new GenerationState{Key=Path.Combine(f.Base,"missing-root")+"|9.9.9|h",Cohort=""},now),"unreadable or missing root treated as proof of absence");
        }});
        TestRunner.Run("corrected root is rediscovered while the host stays open",delegate {using(var f=new Fixtures()){
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock(""));
            string target=Path.Combine(f.Base,"canary-target"),root=Path.Combine(f.Base,"DiscordCanary");Directory.CreateDirectory(target);
            using(var link=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe","/c mklink /J \""+root+"\" \""+target+"\""){UseShellExecute=false,CreateNoWindow=true})){link.WaitForExit();}
            var h=new FakeHost();var s=new Supervisor(new [] {Channel.Discovered(root,"DiscordCanary.exe")},f.Dist,f.Cache,h);s.Tick();
            TestRunner.Assert(s.Channels[0].Outcome==WorkerResult.Refused,"junction not refused");
            Directory.Delete(root);Directory.CreateDirectory(root);
            s.RefreshChannels(new [] {Channel.Discovered(root,"DiscordCanary.exe")});
            TestRunner.Assert(s.Channels.Count==1 && s.Channels[0].Channel.Refusal==null,"corrected root stayed refused");
        }});
        TestRunner.Run("oversized state is refused before it can replace readable state",delegate {using(var f=new Fixtures()){
            var store=new StateStore(f.Cache);store.Value.Generations.Add(new GenerationState{Key="kept",Cohort=""});TestRunner.Assert(store.Save(),"seed not saved");
            for(int i=0;i<500;i++)store.Value.Generations.Add(new GenerationState{Key="large"+i,Cohort=new string('c',2200)});
            TestRunner.Assert(!store.Save() && !store.Writable,"oversized state accepted");
            var reloaded=new StateStore(f.Cache);
            TestRunner.Assert(reloaded.Writable && reloaded.Value.Generations.Count==1,"previous state was not preserved");
        }});
        TestRunner.Run("process cohorts persist as bounded digests",delegate {
            var snapshot=new ProcessSnapshot();for(int i=0;i<20;i++)snapshot.Discord.Add(new ProcessIdentity(i,i,@"C:\"+new string('p',200)+@"\Discord.exe"));
            var same=new ProcessSnapshot();for(int i=19;i>=0;i--)same.Discord.Add(new ProcessIdentity(i,i,@"C:\"+new string('p',200)+@"\Discord.exe"));
            TestRunner.Assert(snapshot.Cohort.Length==64 && snapshot.Cohort==same.Cohort && new ProcessSnapshot().Cohort=="","cohort digest is not bounded and order independent");
        });
        TestRunner.Run("locked countdown fails closed",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();h.Unlocked=false;h.Finish(true);TestRunner.Assert(h.Stops==0,"locked session stopped");}});
        TestRunner.Run("exit metadata change preserves archives and reopens once",delegate {using(var f=new Fixtures()){Seed(f);var h=new FakeHost();h.Running(f.Root,7,1);h.BeforeStop=()=>File.WriteAllText(Path.Combine(f.Root,"module-update.bin"),"update");var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(10);s.Tick();h.Finish(true);TestRunner.Assert(h.Stops==1 && h.Launches==1 && f.Inspect().Kind==InstallationKind.Stock,"failed exit did not preserve/reopen");TestRunner.Assert(s.Store.Find(f.Inspect().Generation).VerificationFailed,"failed verification not persisted");}});
        TestRunner.Run("incomplete generation state cannot reset attempts",delegate {using(var f=new Fixtures()){Seed(f);File.WriteAllText(Path.Combine(f.Cache,"state.json"),"{\"Schema\":1,\"Paused\":false,\"Automatic\":true,\"LastRestart\":0,\"Generations\":[{\"Key\":\"key\",\"Cohort\":\"cohort\"}]}");TestRunner.Assert(!new StateStore(f.Cache).Writable,"partial generation reset attempts");}});
        TestRunner.Run("string primitives cannot impersonate typed restart state",delegate {using(var f=new Fixtures()) {
            string path=Path.Combine(f.Cache,"state.json");
            Directory.CreateDirectory(f.Cache);
            string valid="{\"Schema\":1,\"Paused\":false,\"Automatic\":true,\"LastRestart\":0,\"Generations\":[{\"Key\":\"k\",\"Cohort\":\"c\",\"Eligible\":false,\"Deferred\":false,\"Reserved\":false,\"VerificationFailed\":false,\"Attempts\":1,\"LastAttempt\":0,\"VerifyAfter\":0}]}";
            foreach(string bad in new[]{
                valid.Replace("\"Paused\":false","\"Paused\":\"false\""),
                valid.Replace("\"LastRestart\":0","\"LastRestart\":\"0\""),
                valid.Replace("\"Attempts\":1","\"Attempts\":\"1\"")
            }) {
                File.WriteAllText(path,bad);
                TestRunner.Assert(!new StateStore(f.Cache).Writable,"coerced string state accepted");
            }
        }});
        TestRunner.Run("corrupt state blocks automatic repair",delegate {using(var f=new Fixtures()){Seed(f);File.WriteAllText(Path.Combine(f.Cache,"state.json"),"{}");var h=new FakeHost();var s=Make(f,h);s.Tick();h.Time=h.Time.AddSeconds(20);s.Tick();TestRunner.Assert(!s.Store.Writable && f.Inspect().Kind==InstallationKind.Stock,"corrupt state reset allowance");}});
        TestRunner.Run("confirmed manual recovery works after automatic exhaustion",delegate {using(var f=new Fixtures()) {
            Seed(f);
            var host=new FakeHost();host.Running(f.Root,7,181);
            var engine=Make(f,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();
            var state=engine.Store.Find(f.Inspect().Generation);state.Attempts=3;state.Reserved=true;state.LastAttempt=host.Time.Ticks;
            TestRunner.Assert(engine.Store.Save(),"fixture state not saved");
            bool confirmed=false;
            bool started=engine.ManualRepair(engine.Channels[0],delegate {confirmed=true;return true;});
            TestRunner.Assert(confirmed && started && host.Stops==1 && host.Launches==1 && f.Inspect().Kind==InstallationKind.Healthy,"explicit exhaustion recovery unavailable");
            var saved=new StateStore(f.Cache).Find(f.Inspect().Generation);
            TestRunner.Assert(saved.Attempts==3 && saved.Reserved,"manual recovery reset automatic allowances");
            File.Delete(Path.Combine(f.Resources,"app.asar"));host.Time=host.Time.AddSeconds(60);engine.Tick();
            TestRunner.Assert(host.Stops==1 && new StateStore(f.Cache).Find(f.Inspect().Generation).Attempts==3,"automatic exhaustion rearmed");
        }});
        TestRunner.Run("confirmed manual recovery preserves failed verification prohibition",delegate {using(var f=new Fixtures()) {
            Seed(f);
            var host=new FakeHost();host.Running(f.Root,7,181);
            var engine=Make(f,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();
            string key=f.Inspect().Generation;
            var state=engine.Store.Find(key);state.VerificationFailed=true;state.Reserved=true;state.Attempts=1;
            engine.Store.Value.Automatic=false;TestRunner.Assert(engine.Store.Save(),"fixture state not saved");
            TestRunner.Assert(engine.ManualRepair(engine.Channels[0],()=>true) && f.Inspect().Kind==InstallationKind.Healthy,"manual verification-failure recovery unavailable");
            host.Time=host.Time.AddSeconds(30);engine.Tick();
            var persisted=new StateStore(f.Cache);
            TestRunner.Assert(persisted.Find(key).VerificationFailed && persisted.Find(key).Attempts==1 && persisted.Find(key).Reserved && !persisted.Value.Automatic,"successful manual recovery rearmed automatic work");
            File.Delete(Path.Combine(f.Resources,"app.asar"));host.Time=host.Time.AddSeconds(60);engine.Tick();
            TestRunner.Assert(f.Inspect().Kind==InstallationKind.Interrupted && host.Stops==1,"failed generation repaired automatically");
        }});
        TestRunner.Run("repair failure retries are persisted and bounded",delegate {using(var f=new Fixtures()){Seed(f);File.WriteAllText(Path.Combine(f.Resources,RepairTransaction.JournalName),"foreign");var h=new FakeHost();var s=Make(f,h);s.Tick();for(int i=0;i<6;i++){h.Time=h.Time.AddSeconds(60);s.Tick();}TestRunner.Assert(s.Store.Find(f.Inspect().Generation).Attempts==3,"retry limit failed");TestRunner.Assert(File.ReadAllText(Path.Combine(f.Resources,RepairTransaction.JournalName))=="foreign","evidence changed");}});
    }
}
}
