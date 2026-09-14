using System;
using System.Security.Principal;
using System.Threading;
namespace VencordAutoUpdate {
internal static class InstanceTests {
    static string Name(string purpose,string key) {
        using(var user=WindowsIdentity.GetCurrent()) {
            return "Global\\VencordAutoUpdate-"+user.User.Value+"-"+purpose+"-"+SafePath.TextHash(key.ToUpperInvariant());
        }
    }
    internal static void Run() {
        TestRunner.Run("stop waits for startup event and confirmed resident release",delegate {
            string key="startup-fixture-"+Guid.NewGuid().ToString("N");
            using(var resident=new Mutex(true,Name("instance",key)))
            using(var entered=new ManualResetEvent(false))
            using(var returned=new ManualResetEvent(false)) {
                bool result=false;
                Exception error=null;
                Thread stopper=new Thread(delegate() {
                    entered.Set();
                    try { result=InstanceControl.Stop(key); }
                    catch(Exception e) { error=e; }
                    finally { returned.Set(); }
                });
                stopper.Start();
                try {
                    TestRunner.Assert(entered.WaitOne(3000),"stop thread did not start");
                    TestRunner.Assert(!returned.WaitOne(300),"missing startup IPC falsely acknowledged shutdown");
                    using(var signal=new EventWaitHandle(false,EventResetMode.ManualReset,Name("stop",key))) {
                        TestRunner.Assert(signal.WaitOne(3000),"startup event did not receive stop");
                        TestRunner.Assert(!returned.WaitOne(100),"signal receipt was treated as mutex release");
                    }
                } finally {
                    resident.ReleaseMutex();
                    TestRunner.Assert(stopper.Join(5000),"owned stop thread did not finish after release");
                }
                TestRunner.Assert(error==null && result,"confirmed resident release not acknowledged");
            }
        });
        TestRunner.Run("startup never resets an already delivered stop signal",delegate {
            string key="startup-signal-fixture-"+Guid.NewGuid().ToString("N");
            using(var signal=new EventWaitHandle(true,EventResetMode.ManualReset,Name("stop",key)))
            using(var instance=new InstanceControl(key)) {
                TestRunner.Assert(instance.Primary && instance.StopRequested,"startup erased delivered shutdown request");
            }
        });
        TestRunner.Run("absent instance stop remains idempotent",delegate {
            TestRunner.Assert(InstanceControl.Stop("absent-fixture-"+Guid.NewGuid().ToString("N")),"absent helper stop failed");
        });
    }
}
}
