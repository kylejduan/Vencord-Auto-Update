using System;
using System.IO;
namespace VencordAutoUpdate {
internal static class RuntimeBoundaryTests {
    internal static void Run() {
        TestRunner.Run("state write failure disables all further saves",delegate {using(var f=new Fixtures()){File.WriteAllText(f.Cache,"blocking file");var store=new StateStore(f.Cache);store.Value.Paused=true;TestRunner.Assert(!store.Save() && !store.Writable,"write failure ignored");File.Delete(f.Cache);TestRunner.Assert(!store.Save(),"failed store silently recovered");}});
        TestRunner.Run("state pause and restart preference persist atomically",delegate {using(var f=new Fixtures()){var store=new StateStore(f.Cache);store.Value.Paused=true;store.Value.Automatic=false;TestRunner.Assert(store.Save(),"first save failed");store.Value.LastRestart=123;TestRunner.Assert(store.Save(),"replace save failed");var read=new StateStore(f.Cache);TestRunner.Assert(read.Writable && read.Value.Paused && !read.Value.Automatic && read.Value.LastRestart==123,"preferences lost");TestRunner.Assert(Directory.GetFiles(f.Cache).Length==1,"successful state staging retained");}});
        TestRunner.Run("portable args cannot silently target real Discord",delegate {using(var f=new Fixtures()){bool rejected=false;try{new AppPaths(new [] {"--worker","--data-dir",f.Cache});}catch(ArgumentException){rejected=true;}TestRunner.Assert(rejected,"unpaired worker accepted");var stop=new AppPaths(new [] {"--stop","--data-dir",f.Cache});TestRunner.Assert(stop.Command=="--stop" && SafePath.Same(stop.Data,f.Cache),"exact stop rejected");var portable=new AppPaths(new [] {"--status","--data-dir",f.Cache,"--discord-root",f.Root});TestRunner.Assert(portable.Channels().Count==1 && SafePath.Same(portable.Dist,f.Dist),"portable fell back to real installation");}});
        TestRunner.Run("successful restart repairs actual files and launches once",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("new"));
            var host=new FakeHost();host.Running(f.Root,7,1);var engine=SupervisorTests.Make(f,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();host.Finish(true);
            TestRunner.Assert(f.Inspect().Kind==InstallationKind.Healthy && host.Stops==1 && host.Launches==1,"restart transaction incomplete");var state=new StateStore(f.Cache).Find(f.Inspect().Generation);TestRunner.Assert(state.Attempts==1 && state.Reserved,"restart allowance not persisted");
        }});
        TestRunner.Run("final live process guard retains original during transaction",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("new"));string hash=f.Inspect().OriginalHash;
            var host=new FakeHost();host.OnSnapshot=()=>{if(File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName)))host.Processes.Known=false;};var engine=SupervisorTests.Make(f,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();
            TestRunner.Assert(f.Inspect().Kind==InstallationKind.Stock && f.Inspect().OriginalHash==hash && File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName)),"late process guard did not stop archive mutation");
        }});
        TestRunner.Run("delayed post-launch failure persists and disables automatic restart",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("new"));
            var host=new FakeHost();host.Running(f.Root,7,1);var engine=SupervisorTests.Make(f,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();host.Finish(true);string key=f.Inspect().Generation;
            TestRunner.Assert(new StateStore(f.Cache).Find(key).VerifyAfter!=0,"verification decision not persisted");File.Delete(Path.Combine(f.Resources,"app.asar"));var restarted=SupervisorTests.Make(f,host);host.Time=host.Time.AddSeconds(30);restarted.Tick();var persisted=new StateStore(f.Cache);TestRunner.Assert(persisted.Find(key).VerificationFailed && !persisted.Value.Automatic && host.Stops==1,"post-launch failure did not stop automatic restarts");
        }});
        TestRunner.Run("channels repair independently with shared validated cache",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("stable"));
            string ptb=Path.Combine(f.Base,"DiscordPTB"),resources=Path.Combine(ptb,"app-1.0.10","resources");Directory.CreateDirectory(resources);File.WriteAllText(Path.Combine(ptb,"Update.exe"),"fixture");File.WriteAllText(Path.Combine(ptb,"app-1.0.10","DiscordPTB.exe"),"fixture");File.WriteAllBytes(Path.Combine(resources,"app.asar"),Fixtures.Stock("ptb"));
            var host=new FakeHost();var engine=new Supervisor(new [] {new Channel(f.Root,"Discord.exe"),new Channel(ptb,"DiscordPTB.exe")},f.Dist,f.Cache,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();
            TestRunner.Assert(f.Inspect().Kind==InstallationKind.Healthy && Installation.Inspect(ptb,"DiscordPTB.exe",f.Dist).Kind==InstallationKind.Healthy && new StateStore(f.Cache).Value.Generations.Count==2,"channels did not repair independently");
        }});
        TestRunner.Run("shutdown rejects late acceptance of manual confirmation",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("new"));
            var host=new FakeHost();host.Running(f.Root,7,181);var engine=SupervisorTests.Make(f,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();
            bool accepted=engine.ManualRepair(engine.Channels[0],delegate {engine.Shutdown();return true;});
            TestRunner.Assert(!accepted && host.Stops==0 && host.Launches==0 && f.Inspect().Kind==InstallationKind.Stock,"late confirmation acceptance survived shutdown");
        }});
        TestRunner.Run("shutdown after real journal mutation lets transaction finish",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());File.Delete(Path.Combine(f.Resources,"_app.asar"));f.Write("app.asar",Fixtures.Stock("new"));
            var host=new FakeHost();var engine=SupervisorTests.Make(f,host);bool shutdown=false;
            host.OnSnapshot=delegate {
                if(!shutdown && File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName))) {
                    shutdown=true;TestRunner.Assert(engine.Busy,"transaction was not marked busy");engine.Shutdown();
                }
            };
            engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();
            TestRunner.Assert(shutdown && f.Inspect().Kind==InstallationKind.Healthy && !File.Exists(Path.Combine(f.Resources,RepairTransaction.JournalName)),"shutdown interrupted an active archive transaction");
        }});
        TestRunner.Run("supervisor recovers interrupted original without restart",delegate {using(var f=new Fixtures()) {
            f.Write("app.asar",f.Loader(""));f.Write("_app.asar",Fixtures.Stock("old"));new LoaderCache(Path.Combine(f.Cache,"cache")).Capture(f.Inspect());File.Delete(Path.Combine(f.Resources,"app.asar"));
            var host=new FakeHost();var engine=SupervisorTests.Make(f,host);engine.Tick();host.Time=host.Time.AddSeconds(10);engine.Tick();TestRunner.Assert(f.Inspect().Kind==InstallationKind.Healthy && host.Stops==0,"interrupted state not recovered");
        }});
    }
}
}
