using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
namespace VencordAutoUpdate {
internal sealed class BrokerService:ServiceBase {
    readonly object gate=new object(),stopGate=new object();readonly Dictionary<string,UserEnrollment> users=new Dictionary<string,UserEnrollment>(StringComparer.Ordinal);
    readonly Dictionary<string,DateTime?> lastLaunch=new Dictionary<string,DateTime?>(StringComparer.Ordinal);
    readonly List<UserEnrollment> retired=new List<UserEnrollment>();WorkerLauncher launcher;bool stopping;int queued;
    internal BrokerService(){ServiceName="VencordAutoUpdate";CanHandleSessionChangeEvent=true;CanHandlePowerEvent=true;CanShutdown=true;AutoLog=true;}
    static void Main(){ServiceBase.Run(new BrokerService());}
    void Report(string message){try{EventLog.WriteEntry(message,EventLogEntryType.Warning);}catch{try{Trace.TraceError(message);}catch{}}}
    protected override void OnStart(string[] args) {
        using(var identity=WindowsIdentity.GetCurrent())if(!identity.IsSystem)throw new UnauthorizedAccessException("SCM broker requires LocalSystem.");
        if(args.Length!=0)throw new ArgumentException("Service takes no runtime path or command arguments.");
        launcher=new WorkerLauncher(Assembly.GetExecutingAssembly().Location);Reconcile();
    }
    void Reconcile() {
        if(Interlocked.Exchange(ref queued,1)!=0)return;
        ThreadPool.QueueUserWorkItem(delegate {
            lock(gate) {
                Interlocked.Exchange(ref queued,0);if(stopping)return;
                try {
                    var eligible=UserSessions.Eligible(Report);var keep=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var user in eligible) {
                        keep.Add(user.Sid);UserEnrollment current;
                        if(users.TryGetValue(user.Sid,out current) && current.User.Id!=user.Id){current.StopDispatch();lastLaunch[current.User.Sid]=current.LastLaunch;retired.Add(current);users.Remove(user.Sid);}
                        if(!users.TryGetValue(user.Sid,out current)) {
                            // A previous session must drain before another worker for this SID is allowed.
                            bool blocked=false;foreach(var old in retired)if(old.User.Sid==user.Sid && !old.WaitForDrain(0)){blocked=true;break;}
                            if(blocked)continue;
                            DateTime? previous;lastLaunch.TryGetValue(user.Sid,out previous);current=new UserEnrollment(user,launcher,Report,previous,Reconcile);users.Add(user.Sid,current);
                        }
                        current.Signal();
                    }
                    foreach(string sid in new List<string>(users.Keys))if(!keep.Contains(sid)){var user=users[sid];user.StopDispatch();lastLaunch[user.User.Sid]=user.LastLaunch;retired.Add(user);users.Remove(sid);}
                    for(int i=retired.Count-1;i>=0;i--)if(retired[i].WaitForDrain(0)){retired[i].Dispose();retired.RemoveAt(i);}
                }catch(Exception e){Report("Session reconciliation deferred until the next session event: "+e.Message);}
            }
        });
    }
    protected override void OnSessionChange(SessionChangeDescription change){Reconcile();base.OnSessionChange(change);}
    protected override bool OnPowerEvent(PowerBroadcastStatus status){if(status==PowerBroadcastStatus.ResumeAutomatic || status==PowerBroadcastStatus.ResumeSuspend || status==PowerBroadcastStatus.ResumeCritical)Reconcile();return true;}
    protected override void OnStop() {
        // Shutdown and stop callbacks can already be queued together. Serialize
        // ownership disposal without holding the reconciliation gate while waiting.
        lock(stopGate) {
            List<UserEnrollment> pending;
            lock(gate){stopping=true;pending=new List<UserEnrollment>(users.Values);pending.AddRange(retired);foreach(var user in pending)user.StopDispatch();}
            bool observed=false;int released=0;
            ServiceDrain.Wait(delegate {
                if(!observed){foreach(var user in pending)if(!user.WaitForDrain(0))return false;observed=true;}
                // Remember positive exit proof and completed cleanup if a later
                // disposal fails, so retry never touches an already-disposed wait.
                while(released<pending.Count){pending[released].Dispose();released++;}
                return true;
            },RequestAdditionalTime,
                delegate{Report("SERVICE STOP OVERRUN: owned work or cleanup remains after 225 seconds; retaining STOP_PENDING and prohibiting binary replacement until actual exit and release.");});
            lock(gate){users.Clear();retired.Clear();}
            ExitCode=0;
        }
    }
    // Framework OnShutdown starts in RUNNING. Stop() first enters STOP_PENDING,
    // making RequestAdditionalTime valid; OnStop alone does not do so.
    protected override void OnShutdown(){Stop();base.OnShutdown();}
}
}
