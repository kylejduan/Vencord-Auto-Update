using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    sealed class FakeWorker:IWorkerProcess {
        Action<int> callback;volatile bool exited;int exitCode;internal bool Disposed,FailRegistration;
        public void OnExit(Action<int> value){if(FailRegistration)throw new InvalidOperationException("fixture registration failure");callback=value;}
        internal void Complete(int result){exitCode=result;exited=true;if(callback!=null)callback(result);}
        public bool TryGetExitCode(out int result){result=exitCode;return exited;}
        public void Dispose(){Disposed=true;}
    }
    sealed class FakeLauncher:IWorkerLauncher {
        internal FakeWorker Worker,StopWorker;internal readonly AutoResetEvent Launched=new AutoResetEvent(false);internal int Count;internal bool FailRegistration;
        public IWorkerProcess Launch(UserSession user,bool stop) {var result=new FakeWorker();if(stop)StopWorker=result;else{Worker=result;result.FailRegistration=FailRegistration;Count++;Launched.Set();}return result;}
    }
    static void EnrollmentTests() {
        string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vencord-Service-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var launcher=new FakeLauncher();UserEnrollment enrollment=null;var retirement=new ManualResetEvent(false);
        try {
            var roots=new[]{Path.Combine(fixture,"Discord"),Path.Combine(fixture,"DiscordPTB"),Path.Combine(fixture,"DiscordCanary"),Path.Combine(fixture,"Vencord","dist")};Directory.CreateDirectory(roots[0]);
            var user=new UserSession(1,"fixture-only",fixture,roots);
            enrollment=new UserEnrollment(user,launcher,delegate(string error){throw new Exception(error);},null,delegate{});
            enrollment.Signal();Check(launcher.Launched.WaitOne(5000),"enrollment subscribes then dispatches fake worker");
            // Wait until OnExit is registered under the enrollment lock, then signal real IO.
            DateTime? launched=enrollment.LastLaunch;Check(launched.HasValue,"enrollment records launch");
            File.WriteAllText(Path.Combine(roots[0],"app.asar"),"changed during worker");Thread.Sleep(300);launcher.Worker.Complete(0);
            Check(launcher.Launched.WaitOne(12000),"native event during worker produces followup");
            launched=enrollment.LastLaunch;Check(launcher.Count==2,"burst has only one followup");
            enrollment.StopDispatch();Check(launcher.StopWorker!=null,"shutdown launches fixed ordinary stop through boundary");
            Check(!enrollment.WaitForDrain(0),"shutdown cannot finish before entered worker drains");
            launcher.StopWorker.Complete(0);Check(!enrollment.WaitForDrain(0),"stop invoker exit alone is not repair completion");
            launcher.Worker.Complete(0);Check(enrollment.WaitForDrain(5000),"worker drain observed");Check(launcher.Worker.Disposed && launcher.StopWorker.Disposed,"owned process boundary disposed");
            enrollment.Dispose();enrollment=null;
            enrollment=new UserEnrollment(user,launcher,delegate(string error){throw new Exception(error);},null,delegate{});
            enrollment.Signal();Check(launcher.Launched.WaitOne(5000),"fixture idle host starts");launched=enrollment.LastLaunch;launcher.Worker.Complete(0);
            using(var self=Process.GetCurrentProcess()) {
                self.Refresh();TimeSpan cpu=self.TotalProcessorTime;int launches=launcher.Count;Thread.Sleep(2000);self.Refresh();
                Check(launcher.Count==launches && launcher.Worker.Disposed,"healthy enrollment has no worker or repeated launch");
                Console.WriteLine("Nonprivileged enrollment with fake launcher idle: cpu_ms="+(self.TotalProcessorTime-cpu).TotalMilliseconds+" launch_delta="+(launcher.Count-launches)+" worker_absent="+launcher.Worker.Disposed+" working_set="+self.WorkingSet64+" handles="+self.HandleCount);
            }
            enrollment.Dispose();enrollment=null;
            launcher.FailRegistration=true;string failure=null;
            enrollment=new UserEnrollment(user,launcher,delegate(string error){failure=error;},null,delegate{retirement.Set();});
            enrollment.Signal();Check(launcher.Launched.WaitOne(5000),"registration failure fixture launched");launched=enrollment.LastLaunch;
            Check(failure!=null && !launcher.Worker.Disposed,"registration failure cannot discard a successfully launched worker");
            enrollment.StopDispatch();launcher.StopWorker.Complete(0);Check(!retirement.WaitOne(0),"stop-first retirement waits for worker");launcher.Worker.Complete(0);Check(retirement.WaitOne(5000),"retirement autonomously observes unregistered worker exit");enrollment.Dispose();enrollment=null;
        }finally{if(enrollment!=null){enrollment.StopDispatch();if(launcher.StopWorker!=null && !launcher.StopWorker.Disposed)launcher.StopWorker.Complete(0);if(launcher.Worker!=null)launcher.Worker.Complete(0);try{enrollment.Dispose();}catch(InvalidOperationException){}}launcher.Launched.Dispose();retirement.Dispose();Directory.Delete(fixture,true);}
    }
}
}
