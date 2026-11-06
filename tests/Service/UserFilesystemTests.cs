using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Runtime.Remoting.Messaging;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    const string ContextKey="VencordFixtureUserContext";
    sealed class MarkedContext:IDisposable {
        readonly object previous=CallContext.LogicalGetData(ContextKey);
        internal MarkedContext(){CallContext.LogicalSetData(ContextKey,"user-scope");}
        public void Dispose(){if(previous==null)CallContext.FreeNamedDataSlot(ContextKey);else CallContext.LogicalSetData(ContextKey,previous);}
    }
    sealed class ScopeLauncher:IWorkerLauncher {
        internal readonly AutoResetEvent Launched=new AutoResetEvent(false);
        internal FakeWorker Worker,StopWorker;internal int Leaks;
        public IWorkerProcess Launch(UserSession user,bool stop) {
            if(CallContext.LogicalGetData(ContextKey)!=null)Interlocked.Increment(ref Leaks);
            var result=new FakeWorker();if(stop)StopWorker=result;else{Worker=result;Launched.Set();}return result;
        }
    }
    static void UserFilesystemTests() {
        using(var identity=WindowsIdentity.GetCurrent()) {
            bool suppressed=ExecutionContext.IsFlowSuppressed(),failed=false;
            try {UserSessions.WithIdentity<bool>(identity,delegate {Check(ExecutionContext.IsFlowSuppressed(),"user IO suppresses asynchronous identity flow");throw new IOException("fixture");});}
            catch(IOException){failed=true;}
            using(var after=WindowsIdentity.GetCurrent())Check(failed && after.User==identity.User && ExecutionContext.IsFlowSuppressed()==suppressed,"user IO restores identity and flow after failure");
            string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vencord-Scope-Test-"+Guid.NewGuid().ToString("N"));
            string root=Path.Combine(fixture,"Discord");Directory.CreateDirectory(root);
            var launcher=new ScopeLauncher();UserEnrollment enrollment=null;string error=null;
            try {
                var user=new UserSession(1,"fixture-only",fixture,new[]{root,Path.Combine(fixture,"PTB"),Path.Combine(fixture,"Canary"),Path.Combine(fixture,"dist")});
                enrollment=new UserEnrollment(user,launcher,message=>error=message,null,delegate{},action=>UserSessions.WithIdentity(identity,delegate {using(new MarkedContext())return action();}));
                enrollment.Signal();Check(launcher.Launched.WaitOne(5000),"scoped subscription restores dispatch context");var first=enrollment.LastLaunch;launcher.Worker.Complete(0);
                File.WriteAllText(Path.Combine(root,"app.asar"),"fixture");
                Check(launcher.Launched.WaitOne(12000),"scoped filesystem event reaches broker dispatch");var next=enrollment.LastLaunch;launcher.Worker.Complete(0);
                Check(error==null && launcher.Leaks==0 && next>first,"filesystem callback does not leak user execution context into dispatch");
                Console.WriteLine("PASS ordinary filesystem scope restores identity and native callback execution context");
            }finally {
                if(enrollment!=null){enrollment.StopDispatch();if(launcher.StopWorker!=null && !launcher.StopWorker.Disposed)launcher.StopWorker.Complete(0);if(launcher.Worker!=null && !launcher.Worker.Disposed)launcher.Worker.Complete(0);if(!enrollment.WaitForDrain(5000))throw new Exception("Scope fixture did not drain.");enrollment.Dispose();}
                launcher.Launched.Dispose();Directory.Delete(fixture,true);
            }
        }
    }
}
}
