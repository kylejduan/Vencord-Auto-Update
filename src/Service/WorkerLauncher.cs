using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
namespace VencordAutoUpdate {
internal interface IWorkerProcess:IDisposable {
    void OnExit(Action<int> callback);
    bool TryGetExitCode(out int result);
}
internal interface IWorkerLauncher {
    IWorkerProcess Launch(UserSession user,bool stop);
}
internal sealed class WorkerProcess:IWorkerProcess {
    sealed class ProcessWait:WaitHandle {internal ProcessWait(IntPtr handle){SafeWaitHandle=new SafeWaitHandle(handle,false);}}
    readonly object gate=new object();readonly TokenHandle handle;readonly WaitHandle wait;RegisteredWaitHandle registration;bool disposed;
    internal WorkerProcess(IntPtr raw){handle=new TokenHandle(raw);wait=new ProcessWait(raw);}
    public void OnExit(Action<int> callback){registration=ThreadPool.RegisterWaitForSingleObject(wait,delegate{int code;if(TryGetExitCode(out code))callback(code);},null,Timeout.Infinite,true);}
    public bool TryGetExitCode(out int result) {
        lock(gate){result=22;if(disposed || !wait.WaitOne(0))return false;if(!SessionNative.GetExitCodeProcess(handle,out result))result=22;return true;}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;if(registration!=null)registration.Unregister(null);wait.Dispose();handle.Dispose();}}
}
internal sealed class WorkerLauncher:IWorkerLauncher {
    readonly string executable;
    internal WorkerLauncher(string servicePath) {
        TrustedPaths.RequireProtectedBinary(servicePath);
        executable=TrustedPaths.RequireProtectedBinary(Path.Combine(Path.GetDirectoryName(servicePath),"VencordAutoUpdate.exe"));
    }
    public IWorkerProcess Launch(UserSession user,bool stop) {
        if(!stop && !UserSessions.IsInteractive(user.Id))throw new UnauthorizedAccessException("Worker session is not active and unlocked.");
        TrustedPaths.RequireProtectedBinary(executable);
        // Reacquire and revalidate; no token or user environment persists in the service.
        using(var token=UserSessions.OrdinaryToken(user.Id,user.Sid)) {
            IntPtr environment;if(!SessionNative.CreateEnvironmentBlock(out environment,token,false))throw new Win32Exception();
            try {
                var startup=new SessionNative.Startup();startup.Size=System.Runtime.InteropServices.Marshal.SizeOf(startup);startup.Desktop="winsta0\\default";
                SessionNative.ProcessInfo process;
                if(!SessionNative.CreateProcessAsUser(token,executable,new StringBuilder("\""+executable+"\" "+(stop?"--stop":"--worker")),IntPtr.Zero,IntPtr.Zero,false,0x400,environment,Path.GetDirectoryName(executable),ref startup,out process))throw new Win32Exception();
                SessionNative.CloseHandle(process.Thread);return new WorkerProcess(process.Process);
            }finally{SessionNative.DestroyEnvironmentBlock(environment);}
        }
    }
}
}
