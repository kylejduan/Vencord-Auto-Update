using System;
using System.Threading;
namespace VencordAutoUpdate {
internal sealed class UserEnrollment:IDisposable {
    readonly object gate=new object();readonly BrokerSchedule schedule;
    readonly RootNotifications notifications;readonly IWorkerLauncher launcher;readonly Action<string> report;readonly Action retiredDrained;
    readonly ScheduledDispatch deadline;readonly Timer dispatch,watchdog,completionProbe;readonly ManualResetEvent drained=new ManualResetEvent(true);
    IWorkerProcess worker,stopWorker;DeferredProcessWaits processes;
    bool stopping,disposed,workerUnobserved,stopUnobserved;int queued;
    internal readonly UserSession User;
    internal DateTime? LastLaunch {get{lock(gate)return schedule.LastLaunch;}}
    internal UserEnrollment(UserSession user,IWorkerLauncher launcher,Action<string> report,DateTime? previousLaunch,Action retiredDrained) {
        User=user;this.launcher=launcher;this.report=report;this.retiredDrained=retiredDrained;schedule=new BrokerSchedule(previousLaunch);
        notifications=new RootNotifications(user.Profile,user.Roots,Signal);
        dispatch=new Timer(Dispatch,null,Timeout.Infinite,Timeout.Infinite);deadline=new ScheduledDispatch(schedule,delegate{return BrokerClock.Now;},delegate(int delay){dispatch.Change(delay,Timeout.Infinite);});watchdog=new Timer(Watchdog,null,Timeout.Infinite,Timeout.Infinite);completionProbe=new Timer(ProbeCompletion,null,Timeout.Infinite,Timeout.Infinite);
    }
    internal void Signal() {
        // Collapse concurrent native callbacks before entering the serialized scheduler.
        if(Interlocked.Exchange(ref queued,1)==0)ThreadPool.QueueUserWorkItem(delegate {
            lock(gate){Interlocked.Exchange(ref queued,0);if(stopping)return;DropProcesses();schedule.Signal(BrokerClock.Now);Arm();}
        });
    }
    void Arm(){deadline.Arm();}
    void DropProcesses(){if(processes!=null){processes.Dispose();processes=null;}}
    void Dispatch(object unused) {
        lock(gate) {
            if(stopping || !deadline.Take())return;
            try {
                // Subscribe before starting a worker's snapshot. Failure retains bounded pending work.
                notifications.Refresh();worker=launcher.Launch(User,false);drained.Reset();
                IWorkerProcess launched=worker;worker.OnExit(delegate(int result){Completed(launched,result);});watchdog.Change(225000,Timeout.Infinite);
            }catch(Exception e) {
                report("Worker dispatch failed for "+User.Sid+": "+e.Message);
                // A successful process creation is ownership, even if callback registration
                // fails. Retain the handle and block replacement until drain is observed.
                if(worker!=null){workerUnobserved=true;ArmCompletionProbe();watchdog.Change(225000,Timeout.Infinite);return;}
                schedule.Complete(22,0,BrokerClock.Now);Arm();
            }
        }
    }
    void Completed(IWorkerProcess process,int result) {
        lock(gate) {
            if(!Object.ReferenceEquals(worker,process))return;workerUnobserved=false;
            watchdog.Change(Timeout.Infinite,Timeout.Infinite);
            if(worker!=null){worker.Dispose();worker=null;}ArmCompletionProbe();
            if(stopWorker==null)MarkDrained();if(stopping)return;
            result=WorkerProtocol.Normalize(result);int mask=WorkerProtocol.DeferredMask(result),live=0;
            DropProcesses();
            if(mask!=0) {
                try {
                    processes=DeferredProcessWaits.Capture(User.Roots,mask,Exited);
                    if(!processes.Known){DropProcesses();result=22;}
                    else live=processes.LiveMask;
                }catch(Exception e){report("Process exit enrollment failed: "+e.Message);DropProcesses();result=22;}
            }
            schedule.Complete(result,live,BrokerClock.Now);if(schedule.Due.HasValue)DropProcesses();Arm();
        }
    }
    void Exited() {
        lock(gate){if(stopping || processes==null || !processes.AnyExited)return;DropProcesses();schedule.ProcessesExited(BrokerClock.Now);Arm();}
    }
    void MarkDrained(){drained.Set();if(stopping)retiredDrained();}
    void RequestStop() {
        if(worker==null || stopWorker!=null)return;
        try {
            stopWorker=launcher.Launch(User,true);drained.Reset();
            IWorkerProcess launched=stopWorker;stopWorker.OnExit(delegate(int result){StopCompleted(launched,result);});
        }catch(Exception e){if(stopWorker!=null){stopUnobserved=true;ArmCompletionProbe();}report("Unable to request ordinary-user stop for "+User.Sid+": "+e.Message);}
    }
    void StopCompleted(IWorkerProcess process,int result) {
        lock(gate){if(!Object.ReferenceEquals(stopWorker,process))return;stopUnobserved=false;if(result!=0)report("Orderly stop invocation failed for "+User.Sid+", exit "+result);stopWorker.Dispose();stopWorker=null;ArmCompletionProbe();ObserveWorkerExit();if(worker==null)MarkDrained();}
    }
    void ArmCompletionProbe() {
        // Registration failure must not lose a later exit after session retirement.
        // One bounded handle check per second exists only while owned work lacks a wait.
        bool pending=(workerUnobserved && worker!=null) || (stopUnobserved && stopWorker!=null);
        completionProbe.Change(pending?1000:Timeout.Infinite,Timeout.Infinite);
    }
    void ProbeCompletion(object unused){lock(gate){if(disposed)return;ObserveUnregisteredExits();ArmCompletionProbe();}}
    void ObserveWorkerExit(){int result;if(workerUnobserved && worker!=null && worker.TryGetExitCode(out result))Completed(worker,result);}
    void ObserveUnregisteredExits(){ObserveWorkerExit();int result;if(stopUnobserved && stopWorker!=null && stopWorker.TryGetExitCode(out result))StopCompleted(stopWorker,result);}
    void Watchdog(object unused){lock(gate){ObserveUnregisteredExits();if(worker==null)return;report("Worker exceeded 225 seconds for "+User.Sid+"; requesting orderly stop, never terminating its repair transaction.");RequestStop();}}
    internal void StopDispatch(){lock(gate){if(stopping)return;stopping=true;schedule.Stop();dispatch.Change(Timeout.Infinite,Timeout.Infinite);watchdog.Change(Timeout.Infinite,Timeout.Infinite);notifications.Dispose();DropProcesses();RequestStop();}}
    internal bool WaitForDrain(int milliseconds) {
        // Explicit shutdown waits can also observe owned handles promptly within
        // the caller budget; they are not required for retirement wakeups.
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        do {
            lock(gate){ObserveUnregisteredExits();if(drained.WaitOne(0))return true;}
            int remaining=milliseconds-(int)elapsed.ElapsedMilliseconds;if(remaining<=0)return false;
            drained.WaitOne(Math.Min(remaining,100));
        }while(true);
    }
    public void Dispose() {
        StopDispatch();lock(gate){if(disposed)return;if(!drained.WaitOne(0))throw new InvalidOperationException("Owned worker remains active; binary replacement must be refused.");disposed=true;dispatch.Dispose();watchdog.Dispose();completionProbe.Dispose();drained.Dispose();}
    }
}
}
