using System;
namespace VencordAutoUpdate {
internal static partial class ServiceTests {
    static int count;
    internal static void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
    static int Main(string[] args) {if(args.Length==1 && args[0]=="--fixture"){Console.ReadLine();return 0;}try {PendingDrainDeadlineTests();ShutdownDrainTests();FinalSchedulerTests();SchedulerTests();SecurityTests();NativeTests();EnrollmentTests();NativeRegistrationTests();DeferredEnrollmentTests();Console.WriteLine("PASS service assertions="+count);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
    static void SchedulerTests() {
        DateTime tick=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);var pending=new BrokerSchedule(tick);pending.Signal(tick);
        int armed=-2,arms=0;var timer=new ScheduledDispatch(pending,delegate{return tick;},delegate(int delay){armed=delay;arms++;});timer.Arm();
        tick=tick.AddTicks(99985000);Check(!timer.Take(),"early one-shot does not dispatch");Check(arms==2 && armed==2,"early one-shot rearms rounded-up remaining deadline");tick=tick.AddTicks(15000);Check(timer.Take(),"rearmed one-shot dispatches at deadline");pending.Complete(0,0,tick);timer.Arm();Check(armed==-1,"completed deadline disarms timer");
        DateTime now=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        BrokerSchedule s=new BrokerSchedule();s.Signal(now);Check(s.Take(now),"startup launches");
        s.Complete(65,0,now);Check(!s.Take(now.AddSeconds(9)),"deferred empty respects ten second cap");Check(s.Take(now.AddSeconds(10)),"deferred empty closes exit-before-wait race");
        s.Complete(65,1,now.AddSeconds(10));Check(s.Due==null,"live deferred has no polling timer");s.ProcessesExited(now.AddSeconds(11));Check(!s.Take(now.AddSeconds(19)),"ten second rate cap");Check(s.Take(now.AddSeconds(20)),"last process exit runs");
        s.Signal(now.AddSeconds(71));s.Signal(now.AddSeconds(72));s.Complete(0,0,now.AddSeconds(73));Check(s.Take(now.AddSeconds(80)),"dirty event preserved once");s.Complete(0,0,now.AddSeconds(81));Check(s.Due==null,"followup ends idle");
        s.Signal(now.AddSeconds(90));Check(s.Take(now.AddSeconds(90)),"new burst");s.Complete(21,0,now.AddSeconds(90));Check(!s.Take(now.AddDays(1)) && s.Due==null,"refusal remains idle");
        s=new BrokerSchedule();s.Signal(now);Check(s.Take(now),"retry first launch");for(int i=1;i<=3;i++){s.Complete(22,0,now.AddSeconds((i-1)*60));Check(!s.Take(now.AddSeconds(i*60-1)),"retry spacing");Check(s.Take(now.AddSeconds(i*60)),"bounded retry");}s.Complete(22,0,now.AddSeconds(180));Check(s.Due==null,"retry burst exhausted");s.Signal(now.AddSeconds(181));Check(s.Take(now.AddSeconds(190)),"new event renews opportunity");s.Stop();Check(s.Due==null && !s.Take(now.AddDays(1)),"stopped cannot dispatch");
    }
    static void FinalSchedulerTests() {
        foreach(int code in new[]{0,21,22,65,66,67,68,69,70,71})Check(WorkerProtocol.Normalize(code)==code,"exact protocol accepted "+code);
        foreach(int code in new[]{-1,1,20,64,72,129,321,0x10041,Int32.MinValue,Int32.MaxValue})Check(WorkerProtocol.Normalize(code)==22,"invalid protocol becomes Retry "+code);
        DateTime now=new DateTime(2026,1,1);var s=new BrokerSchedule();s.Signal(now);Check(s.Take(now),"mask initial launch");
        s.Complete(68,0,now);Check(s.Take(now.AddSeconds(10)),"hinted Canary already quiet gets immediate capped reconciliation");
        for(int i=1;i<=3;i++){s.Complete(68,0,now.AddSeconds(10+(i-1)*60));Check(!s.Take(now.AddSeconds(10+i*60-1)),"empty hint retry spacing");Check(s.Take(now.AddSeconds(10+i*60)),"empty hint timed retry");}
        s.Complete(68,0,now.AddSeconds(190));Check(s.Due==null && !s.Take(now.AddHours(1)),"empty hints bounded at five total launches; absent PTB never wakes");
        s=new BrokerSchedule();s.Signal(now);Check(s.Take(now),"only deferred Canary launches");s.Complete(68,4,now);
        Check(s.Due==null && !s.Take(now.AddHours(1)),"live hinted Canary with absent unhinted PTB has no timer across one hour");
        s.ProcessesExited(now.AddHours(1));Check(s.Take(now.AddHours(1)),"full hinted Canary exit wakes after hour idle");s.Complete(0,0,now.AddHours(1));Check(s.Due==null,"Done clears root waits and timers");
    }
    static partial void NativeTests();
}
}
