using System;
namespace VencordAutoUpdate {
internal static class PolicyTests {
    static readonly DateTime Now = new DateTime(2026,9,13,0,0,0,DateTimeKind.Utc);
    static GenerationState Fresh(double age, bool interactive) { return RestartPolicy.First("generation", "pid@start", Now, Now.AddSeconds(-age), interactive); }
    static Decision Eval(GenerationState s, double seconds, bool running, bool interactive, long last) {
        return RestartPolicy.Evaluate(s,Now.AddSeconds(seconds),Now,running,"pid@start",interactive,true,last);
    }
    internal static void Run() {
        TestRunner.Run("settling needs ten seconds",delegate { var s=Fresh(0,true); TestRunner.Assert(Eval(s,9,false,true,0)==Decision.Wait,"repaired early"); TestRunner.Assert(Eval(s,10,false,true,0)==Decision.Repair,"did not settle"); });
        TestRunner.Run("logon first observation preserves old session",delegate { var s=Fresh(181,true); TestRunner.Assert(Eval(s,10,true,true,0)==Decision.Deferred,"old session not deferred"); });
        TestRunner.Run("first observation age anchored through countdown",delegate { var s=Fresh(179,true); TestRunner.Assert(Eval(s,30,true,true,0)==Decision.Countdown,"age eligibility lost"); });
        TestRunner.Run("locked first observation remains deferred",delegate { var s=Fresh(1,false); TestRunner.Assert(Eval(s,10,true,true,0)==Decision.Deferred,"unlock rearmed session"); });
        TestRunner.Run("cancel and reservation never rearm running generation",delegate { var s=Fresh(1,true); s.Deferred=true; TestRunner.Assert(Eval(s,10,true,true,0)==Decision.Deferred,"cancel ignored"); s.Deferred=false;s.Reserved=true;TestRunner.Assert(Eval(s,10,true,true,0)==Decision.Deferred,"reserved generation rearmed"); });
        TestRunner.Run("restart global ten minute cap",delegate { var s=Fresh(1,true);TestRunner.Assert(Eval(s,10,true,true,Now.AddSeconds(-589).Ticks)==Decision.Deferred,"global cap ignored");TestRunner.Assert(Eval(s,10,true,true,Now.AddSeconds(-590).Ticks)==Decision.Countdown,"cap never expires"); });
        TestRunner.Run("attempts bounded with sixty second retry",delegate {var s=Fresh(1,true);s.Attempts=1;s.LastAttempt=Now.Ticks;TestRunner.Assert(Eval(s,59,false,true,0)==Decision.Wait,"early retry");TestRunner.Assert(Eval(s,60,false,true,0)==Decision.Repair,"retry unavailable");s.Attempts=3;TestRunner.Assert(Eval(s,120,false,true,0)==Decision.Exhausted,"unbounded retries");});
        TestRunner.Run("changed cohort cannot inherit consent",delegate { var s=Fresh(1,true);TestRunner.Assert(RestartPolicy.Evaluate(s,Now.AddSeconds(10),Now,true,"fresh-pid",true,true,0)==Decision.Deferred,"fresh process inherited eligibility");});
    }
}
}
