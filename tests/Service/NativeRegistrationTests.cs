using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    sealed class NativeFailureProcess:IWorkerProcess {
        [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        readonly object gate=new object();readonly Process process;readonly WorkerProcess owned;internal bool Disposed,ForcedTermination;
        internal NativeFailureProcess(string executable) {
            process=Process.Start(new ProcessStartInfo(executable,"--fixture"){UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true});
            IntPtr handle=OpenProcess(0x101000,false,process.Id);
            if(handle==IntPtr.Zero){Finish();process.Dispose();throw new Win32Exception();}
            owned=new WorkerProcess(handle);
        }
        public void OnExit(Action<int> ignored){throw new InvalidOperationException("Native fixture registration failed before storing callback.");}
        public bool TryGetExitCode(out int result){return owned.TryGetExitCode(out result);}
        internal void Finish(){lock(gate){if(!Disposed && !process.HasExited){process.StandardInput.WriteLine();if(!process.WaitForExit(5000)){ForcedTermination=true;process.Kill();process.WaitForExit();}}}}
        public void Dispose(){lock(gate){if(Disposed)return;Disposed=true;owned.Dispose();process.Dispose();}}
    }
    sealed class NativeFailureLauncher:IWorkerLauncher {
        readonly string executable;internal NativeFailureProcess Worker,StopWorker;internal readonly AutoResetEvent Launched=new AutoResetEvent(false);
        internal NativeFailureLauncher(string executable){this.executable=executable;}
        public IWorkerProcess Launch(UserSession user,bool stop){var process=new NativeFailureProcess(executable);if(stop)StopWorker=process;else{Worker=process;Launched.Set();}return process;}
    }
    static void NativeRegistrationTests() {
        string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vencord-Service-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        string executable=Path.Combine(fixture,"fixture.exe");File.Copy(typeof(ServiceTests).Assembly.Location,executable);
        var launcher=new NativeFailureLauncher(executable);UserEnrollment enrollment=null;var retirement=new ManualResetEvent(false);
        try {
            var user=new UserSession(1,"fixture-only",fixture,new[]{Path.Combine(fixture,"Discord"),Path.Combine(fixture,"DiscordPTB"),Path.Combine(fixture,"DiscordCanary"),Path.Combine(fixture,"Vencord","dist")});
            enrollment=new UserEnrollment(user,launcher,delegate{},null,delegate{retirement.Set();});enrollment.Signal();Check(launcher.Launched.WaitOne(5000),"native registration-failure worker launched");DateTime? observed=enrollment.LastLaunch;
            enrollment.StopDispatch();Check(launcher.StopWorker!=null,"native registration-failure stop invocation launched");Check(!enrollment.WaitForDrain(0),"live native handles cannot acknowledge drain");
            launcher.StopWorker.Finish();Check(!retirement.WaitOne(100),"native stop exit alone does not acknowledge retirement");
            launcher.Worker.Finish();Check(retirement.WaitOne(5000),"native retirement autonomously wakes after both callback-free processes exit");
            Check(launcher.Worker.Disposed && launcher.StopWorker.Disposed,"unregistered native worker and stop handles released");Check(!launcher.Worker.ForcedTermination && !launcher.StopWorker.ForcedTermination,"native failure recovery did not force terminate fixtures");enrollment.Dispose();enrollment=null;
        }finally {
            if(launcher.Worker!=null)launcher.Worker.Finish();if(launcher.StopWorker!=null)launcher.StopWorker.Finish();
            if(enrollment!=null){enrollment.StopDispatch();enrollment.WaitForDrain(5000);enrollment.Dispose();}launcher.Launched.Dispose();retirement.Dispose();Directory.Delete(fixture,true);
        }
    }
}
}
