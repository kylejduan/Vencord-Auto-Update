using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
namespace VencordAutoUpdate {
// Global objects coordinate this user across logon sessions. Explicit ACLs exclude other users.
internal sealed class InstanceControl : IDisposable {
    readonly Mutex mutex;
    readonly string data;
    EventWaitHandle stop,show,shown;
    bool owned;
    internal bool Primary { get { return owned; } }
    static SecurityIdentifier User {
        get { using(var identity=WindowsIdentity.GetCurrent()) return identity.User; }
    }
    internal static string Name(string purpose,string key) {
        return "Global\\VencordAutoUpdate-"+User.Value+"-"+purpose+"-"+SafePath.TextHash(key.ToUpperInvariant());
    }
    internal static Mutex NewRootMutex(string root) {
        MutexSecurity acl=new MutexSecurity();
        acl.AddAccessRule(new MutexAccessRule(User,MutexRights.FullControl,AccessControlType.Allow));
        bool created;
        return new Mutex(false,Name("root",root),out created,acl);
    }
    internal InstanceControl(string data) {
        this.data=data;
        MutexSecurity acl=new MutexSecurity();
        acl.AddAccessRule(new MutexAccessRule(User,MutexRights.FullControl,AccessControlType.Allow));
        bool created;
        // A newly created primary mutex is owned atomically, before publishing any IPC events.
        mutex=new Mutex(true,Name("instance",data),out created,acl);
        owned=created;
        if(!owned)TryOwn();
        if(owned)Publish();
    }
    bool TryOwn() {
        if(!owned) {
            try {owned=mutex.WaitOne(0);}
            catch(AbandonedMutexException){owned=true;}
        }
        return owned;
    }
    void Publish() {
        bool created;
        try {
            EventWaitHandleSecurity events=new EventWaitHandleSecurity();
            events.AddAccessRule(new EventWaitHandleAccessRule(User,EventWaitHandleRights.FullControl,AccessControlType.Allow));
            // Initial state applies only to a new object. Never reset an existing delivered stop.
            stop=new EventWaitHandle(false,EventResetMode.ManualReset,Name("stop",data),out created,events);
            show=new EventWaitHandle(false,EventResetMode.AutoReset,Name("show",data),out created,events);
            shown=new EventWaitHandle(false,EventResetMode.ManualReset,Name("shown",data),out created,events);
            shown.Reset();
        } catch {
            Dispose();
            throw;
        }
    }
    internal WaitHandle ShowSignal {get {return show;}}
    internal WaitHandle StopSignal {get {return stop;}}
    internal void StatusShown(){shown.Set();}
    internal bool ShowOrAcquire() {
        // A fire-and-forget Show can disappear during automatic host completion.
        // Wait for actual UI acknowledgement, or take ownership after release.
        var elapsed=Stopwatch.StartNew();
        do {
            if(TryOwn()){Publish();return true;}
            try {
                using(var signal=EventWaitHandle.OpenExisting(Name("show",data),EventWaitHandleRights.Modify))
                using(var acknowledgement=EventWaitHandle.OpenExisting(Name("shown",data),EventWaitHandleRights.Modify|EventWaitHandleRights.Synchronize))
                using(var shutdown=EventWaitHandle.OpenExisting(Name("stop",data),EventWaitHandleRights.Synchronize)) {
                    acknowledgement.Reset();signal.Set();
                    do {
                        if(shutdown.WaitOne(0))return false;
                        if(acknowledgement.WaitOne(50))return false;
                        if(shutdown.WaitOne(0))return false;
                        if(TryOwn()){Publish();return true;}
                        signal.Set();
                    }while(elapsed.Elapsed<TimeSpan.FromSeconds(5));
                }
            }catch(WaitHandleCannotBeOpenedException){Thread.Sleep(50);}
        }while(elapsed.Elapsed<TimeSpan.FromSeconds(5));
        throw new TimeoutException("Status was not acknowledged by the active helper.");
    }
    internal bool StopRequested { get { return stop!=null && stop.WaitOne(0); } }
    internal static bool Stop(string data) {
        Mutex running;
        try {
            running=Mutex.OpenExisting(Name("instance",data),MutexRights.Synchronize|MutexRights.Modify);
        } catch(WaitHandleCannotBeOpenedException) {
            // This is the only absence acknowledgement: no primary mutex was published.
            return true;
        }
        using(running) {
            Stopwatch elapsed=Stopwatch.StartNew();
            do {
                bool released;
                try { released=running.WaitOne(0); }
                catch(AbandonedMutexException) { released=true; }
                if(released) {
                    running.ReleaseMutex();
                    return true;
                }
                try {
                    using(var signal=EventWaitHandle.OpenExisting(Name("stop",data),EventWaitHandleRights.Modify)) signal.Set();
                } catch(WaitHandleCannotBeOpenedException) {
                    // The mutex is still owned: startup may not have published its event yet.
                    // Retry initialization, then keep waiting for actual primary release.
                }
                Thread.Sleep(50);
            } while(elapsed.Elapsed<TimeSpan.FromSeconds(45));
            return false;
        }
    }
    public void Dispose() {
        if(stop!=null) stop.Dispose();
        if(show!=null) show.Dispose();
        if(shown!=null) shown.Dispose();
        if(owned) {
            mutex.ReleaseMutex();
            owned=false;
        }
        mutex.Dispose();
    }
}
}
