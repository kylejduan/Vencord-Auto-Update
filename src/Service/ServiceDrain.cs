using System;
using System.Diagnostics;
using System.Threading;
namespace VencordAutoUpdate {
internal static class ServiceDrain {
    internal static void Wait(Func<bool> drained,Action<int> pendingHint,Action overrun) {
        var clock=Stopwatch.StartNew();Wait(drained,pendingHint,overrun,delegate{return clock.ElapsedMilliseconds;},Thread.Sleep);
    }
    internal static void Wait(Func<bool> drained,Action<int> pendingHint,Action overrun,Func<long> clock,Action<int> wait) {
        long start=clock();bool reported=false;
        // One clock and one bounded wait for the entire ownership set. A soft
        // overrun never abandons an entered repair or returns a false STOPPED.
        while(true) {
            // Failed observation or cleanup is not proof of completion. Retain
            // ownership and retry; diagnostic failure must not unwind OnStop.
            try{if(drained())return;}catch(Exception){}
            long remaining=225000-(clock()-start);
            if(remaining<=0 && !reported){reported=true;try{overrun();}catch(Exception){}}
            try{pendingHint(10000);}catch(Exception){}
            wait(remaining>0?(int)Math.Min(5000,remaining):5000);
        }
    }
}
}
