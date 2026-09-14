using System;
namespace VencordAutoUpdate {
// Called under the enrollment lock. Only actionable work has a one-shot deadline.
internal sealed class BrokerSchedule {
    DateTime? due,lastLaunch;
    bool running,dirty,stopped,waiting;
    int retries,emptySeen;
    internal BrokerSchedule():this(null){}
    internal BrokerSchedule(DateTime? previousLaunch){lastLaunch=previousLaunch;}
    internal DateTime? LastLaunch {get{return lastLaunch;}}
    internal DateTime? Due {get{return stopped || running?null:due;}}
    void Queue(DateTime when) {if(lastLaunch.HasValue && when<lastLaunch.Value.AddSeconds(10))when=lastLaunch.Value.AddSeconds(10);due=when;}
    internal void Signal(DateTime now) {if(stopped)return;retries=0;emptySeen=0;waiting=false;if(running)dirty=true;else Queue(now);}
    internal bool Take(DateTime now) {if(stopped || running || !due.HasValue || due.Value>now)return false;running=true;due=null;lastLaunch=now;return true;}
    internal void Complete(int result,int liveMask,DateTime now) {
        if(!running)return;running=false;if(stopped)return;
        result=WorkerProtocol.Normalize(result);int mask=WorkerProtocol.DeferredMask(result);
        liveMask&=mask;int empty=mask & ~liveMask;
        emptySeen &= mask & ~liveMask;
        int firstEmpty=empty & ~emptySeen;emptySeen|=empty;
        waiting=liveMask!=0;
        if(dirty){dirty=false;Queue(now);return;}
        // Only one immediate reconciliation per inconsistent empty root. Internal
        // followups never reset the three delayed retries for this event burst.
        if(firstEmpty!=0){Queue(now);return;}
        if(result==22 || empty!=0){if(retries<3){retries++;Queue(now.AddSeconds(60));}}
    }
    internal void ProcessesExited(DateTime now) {if(!stopped && waiting){waiting=false;Queue(now);}}
    internal void Stop() {stopped=true;dirty=false;due=null;waiting=false;}
}
}
namespace VencordAutoUpdate {
// One-shot adapter keeps deadline delivery separate from scheduler transitions.
internal sealed class ScheduledDispatch {
    readonly BrokerSchedule schedule;readonly Func<DateTime> now;readonly Action<int> arm;
    internal ScheduledDispatch(BrokerSchedule schedule,Func<DateTime> now,Action<int> arm){this.schedule=schedule;this.now=now;this.arm=arm;}
    internal void Arm(){DateTime? due=schedule.Due;arm(due.HasValue?(int)Math.Min(Int32.MaxValue,Math.Max(1,Math.Ceiling((due.Value-now()).TotalMilliseconds))):-1);}
    internal bool Take(){if(schedule.Take(now()))return true;Arm();return false;}
}
}

namespace VencordAutoUpdate {
internal static class BrokerClock {
    static readonly DateTime origin=DateTime.UtcNow;
    static readonly System.Diagnostics.Stopwatch elapsed=System.Diagnostics.Stopwatch.StartNew();
    internal static DateTime Now {get{return origin+elapsed.Elapsed;}}
}
}
