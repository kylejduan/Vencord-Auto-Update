// Compiled ONLY by Service.Scm.Tests.ps1 with an explicit /main. Never packaged.
using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
namespace VencordAutoUpdate {
internal sealed class ScmFixtureHost:ServiceBase {
    string root,probe;UserEnrollment enrollment;readonly object output=new object(),wtsGate=new object();
    IWorkerProcess nativeWorker,nativeStop;UserSession nativeUser;bool stopping;
    ScmFixtureHost(){ServiceName="VencordAutoUpdate";AutoLog=false;}
    static void Main(string[] args){
        if(args.Length==1 && args[0]=="--held-worker"){
            string failure=Path.Combine(FixtureRoot(),"Probe","fail-stop");while(File.Exists(failure))Thread.Sleep(100);return;
        }
        if(args.Length!=0)throw new ArgumentException("Unknown fixture mode.");ServiceBase.Run(new ScmFixtureHost());
    }
    static string FixtureRoot(){
        string fixture=Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VencordAutoUpdate.Tests");
        if(!String.Equals(Path.GetDirectoryName(fixture),expected,StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(fixture),"^[0-9a-f]{32}$"))throw new IOException("Test host outside exact protected fixture.");
        TrustedPaths.RequireProtectedBinary(Assembly.GetExecutingAssembly().Location);return fixture;
    }
    void Report(string text){lock(output)File.AppendAllText(Path.Combine(probe,"receipt.txt"),text+Environment.NewLine);}
    protected override void OnStart(string[] args) {
        if(args.Length!=0)throw new ArgumentException("Fixture host takes no arguments.");
        stopping=false;
        root=FixtureRoot();
        probe=Path.Combine(root,"Probe");Directory.CreateDirectory(probe);File.WriteAllText(Path.Combine(probe,"receipt.txt"),"");
        string profile=Path.Combine(probe,"Profile");Directory.CreateDirectory(profile);
        var roots=new[]{Path.Combine(profile,"Discord"),Path.Combine(profile,"DiscordPTB"),Path.Combine(profile,"DiscordCanary"),Path.Combine(profile,"Vencord","dist")};
        Directory.CreateDirectory(roots[0]);
        enrollment=new UserEnrollment(new UserSession(0,"test-only",profile,roots),new ImmediateLauncher(Report,Path.Combine(probe,"fail-stop")),Report,null,delegate{});
        enrollment.Signal();Report("HOST STARTED");
        ThreadPool.QueueUserWorkItem(delegate {
            try {
                var users=UserSessions.Eligible(Report);Report("WTS_ELIGIBLE="+users.Count);
                if(users.Count==0){Report("WTS_USER_LAUNCH_UNVERIFIED: no active unlocked ordinary token session");return;}
                foreach(var user in users) {
                    bool clean=true;foreach(string userRoot in user.Roots)if(Directory.Exists(userRoot))clean=false;
                    if(!clean){Report("WTS_USER_LAUNCH_UNVERIFIED: session has existing Discord/Vencord roots; fixture refuses activation");continue;}
                    lock(wtsGate) {
                        if(stopping)return;
                        nativeUser=user;nativeWorker=new WorkerLauncher(Assembly.GetExecutingAssembly().Location).Launch(user,false);
                    }
                    DateTime end=DateTime.UtcNow.AddSeconds(225);bool completed=false;
                    while(DateTime.UtcNow<end) {
                        lock(wtsGate) {
                            if(stopping)return;
                            int code;if(nativeWorker.TryGetExitCode(out code)) {
                                Report("WTS_ORDINARY_WORKER_EXIT="+code+" SESSION="+user.Id);
                                nativeWorker.Dispose();nativeWorker=null;
                                if(code!=0){Report("WTS_USER_LAUNCH_FAILED: eligible clean worker must return Done0");return;}
                                completed=true;break;
                            }
                        }
                        Thread.Sleep(100);
                    }
                    if(!completed){Report("WTS_USER_LAUNCH_FAILED: worker did not drain; ownership retained for orderly stop");return;}
                }
            }catch(Exception e){Report("WTS_USER_LAUNCH_FAILED: "+e.Message);}
            finally{Report("WTS_PROBE_COMPLETE");}
        });
    }
    protected override void OnStop() {
        lock(wtsGate) {
            stopping=true;
            if(nativeWorker!=null && nativeStop==null)nativeStop=new WorkerLauncher(Assembly.GetExecutingAssembly().Location).Launch(nativeUser,true);
        }
        if(enrollment!=null)enrollment.StopDispatch();
        bool observed=false;
        ServiceDrain.Wait(delegate {
            if(!observed) {
            lock(wtsGate) {
                int code;
                if(nativeWorker!=null && !nativeWorker.TryGetExitCode(out code))return false;
                if(nativeStop!=null && !nativeStop.TryGetExitCode(out code))return false;
            }
            if(enrollment!=null && !enrollment.WaitForDrain(0))return false;
            observed=true;
            }
            lock(wtsGate) {
                if(nativeWorker!=null){nativeWorker.Dispose();nativeWorker=null;}
                if(nativeStop!=null){nativeStop.Dispose();nativeStop=null;}
            }
            if(enrollment!=null){enrollment.Dispose();enrollment=null;}
            Report("HOST STOPPED");return true;
        },RequestAdditionalTime,delegate{Report("HOST STOP OVERRUN: owned work retained in STOP_PENDING");});
        ExitCode=0;
    }
    sealed class ImmediateLauncher:IWorkerLauncher {
        readonly Action<string> report;readonly string failure;
        internal ImmediateLauncher(Action<string> report,string failure){this.report=report;this.failure=failure;}
        public IWorkerProcess Launch(UserSession user,bool stop){
            report(stop?"STOP INVOKER":"NOTIFICATION WORK");
            if(!stop && File.Exists(failure)){report("HELD WORKER STARTED");return new HeldWorker(report);}
            return new CompletedWorker();
        }
    }
    // Test-only child reads only its protected fixture marker. It never runs
    // user repair code; actual process exit feeds the real enrollment drain.
    sealed class HeldWorker:IWorkerProcess {
        readonly Process process;readonly Action<string> report;
        internal HeldWorker(Action<string> report){this.report=report;process=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--held-worker"){UseShellExecute=false,CreateNoWindow=true});try{report("HELD WORKER PID="+process.Id);}catch{}}
        public void OnExit(Action<int> callback){ThreadPool.QueueUserWorkItem(delegate{
            process.WaitForExit();try{report("HELD WORKER EXITED");}finally{callback(process.ExitCode);}
        });}
        public bool TryGetExitCode(out int result){result=0;if(!process.HasExited)return false;result=process.ExitCode;return true;}
        public void Dispose(){if(!process.HasExited)throw new InvalidOperationException("Active fixture work cannot be disposed.");process.Dispose();report("HELD WORKER DISPOSED");}
    }
    sealed class CompletedWorker:IWorkerProcess {
        public void OnExit(Action<int> callback){ThreadPool.QueueUserWorkItem(delegate{callback(0);});}
        public bool TryGetExitCode(out int result){result=0;return true;}
        public void Dispose(){}
    }
}
}
