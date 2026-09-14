using System;
namespace VencordAutoUpdate {
// One bounded reconciliation. The caller supplies monotonic elapsed time.
internal sealed class WorkerBatch {
    readonly Supervisor supervisor;
    TimeSpan deadline,observed;
    readonly Func<TimeSpan> clock;
    bool expired;
    internal bool Running {get;private set;}
    internal WorkerResult? Result {get;private set;}
    internal WorkerBatch(Supervisor engine,Func<TimeSpan> monotonicClock=null) {
        supervisor=engine;clock=monotonicClock ?? (()=>observed);
        supervisor.WorkExpired=()=>Running && clock()>=deadline;
    }
    internal void Start(TimeSpan now) {
        if(Running) return;
        supervisor.BeginWork();deadline=now+TimeSpan.FromSeconds(180);
        expired=false;Result=null;Running=true;
    }
    internal void Abort() {expired=true;supervisor.Shutdown();}
    internal void Step(TimeSpan now) {
        observed=now;
        if(!Running) return;
        if(now>=deadline) {expired=true;supervisor.Shutdown();}
        if(supervisor.Busy) return;
        if(expired) Result=WorkerResult.Retry;
        else {
            supervisor.Tick();
            if(clock()>=deadline) {expired=true;supervisor.Shutdown();}
            if(!supervisor.Busy)Result=expired?WorkerResult.Retry:supervisor.Outcome;
        }
        if(Result.HasValue) Running=false;
    }
}
}
