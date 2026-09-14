using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
namespace VencordAutoUpdate {
internal interface ISupervisorHost {
    DateTime Now {get;}
    bool Interactive {get;}
    ProcessSnapshot Snapshot(Channel channel);
    bool Stop(Channel channel,ProcessSnapshot cohort);
    bool Launch(Channel channel);
    void Countdown(string channel,Func<bool> valid,Action<bool> finished);
    void Notify(string message);
}
internal sealed class ChannelStatus {
    internal readonly Channel Channel;
    internal Installation Installation;
    internal string Metadata, Settling, Message="Waiting for first scan.";
    internal DateTime SettledSince;
    internal bool Seeded;
    internal WorkerResult? Outcome;
    internal string RefusalMetadata;
    internal DateTime RefusalSince;
    internal ChannelStatus(Channel channel) {Channel=channel;}
}
internal sealed class PendingRestart {
    internal ChannelStatus Status;
    internal GenerationState State;
    internal ProcessSnapshot Processes;
    internal string Metadata, Settling;
    internal bool Manual;
}
internal sealed class Supervisor {
    readonly string dist;
    readonly LoaderCache cache;
    readonly StateStore store;
    readonly ISupervisorHost host;
    readonly List<ChannelStatus> channels=new List<ChannelStatus>();
    PendingRestart pending;
    bool busy,stopping,transactionActive;
    internal Func<bool> WorkExpired;
    bool CanWork() {
        if(!stopping && WorkExpired!=null && WorkExpired())Shutdown();
        return !stopping;
    }
    internal IList<ChannelStatus> Channels {get {return channels.AsReadOnly();} }
    internal StateStore Store {get {return store;} }
    internal WorkerResult? Outcome {
        get {
            if(busy || pending!=null) return null;
            WorkerResult result=WorkerResult.Done;int mask=0;
            foreach(var status in channels) {
                if(!status.Outcome.HasValue) return null;
                if(WorkerProtocol.DeferredMask((int)status.Outcome.Value)!=0) mask|=WorkerProtocol.DeferredMask((int)status.Outcome.Value);
                else if(status.Outcome==WorkerResult.Retry) result=WorkerResult.Retry;
                else if(status.Outcome==WorkerResult.Refused && result==WorkerResult.Done) result=WorkerResult.Refused;
            }
            return mask!=0?(WorkerResult)(64|mask):result;
        }
    }
    internal void BeginWork() {
        if(busy || pending!=null) throw new InvalidOperationException("Work has not drained.");
        stopping=false;
        foreach(var status in channels) {status.Outcome=null;status.Metadata=null;status.RefusalMetadata=null;}
    }
    internal bool Busy {get {return busy;} }
    internal bool Stopping {get {return stopping;} }
    internal Supervisor(IList<Channel> roots,string vencordDist,string data,ISupervisorHost boundary) {
        dist=vencordDist;cache=new LoaderCache(Path.Combine(data,"cache"));store=new StateStore(data);host=boundary;
        foreach(var channel in roots)channels.Add(new ChannelStatus(channel));
    }
    internal void RefreshChannels(IList<Channel> roots) {
        foreach(var channel in roots)if(!channels.Exists(s=>SafePath.Same(s.Channel.Root,channel.Root)))channels.Add(new ChannelStatus(channel));
    }
    internal void Shutdown() {
        // Cancel consent immediately. An entered disk transaction keeps its live safety guards
        // and finishes before the outer busy operation can release the resident instance.
        stopping=true;
        if(!transactionActive) Cancel();
    }
    internal bool SetPreferences(bool paused,bool automatic) {store.Value.Paused=paused;store.Value.Automatic=automatic;if(paused || !automatic)Cancel();return store.Save();}
    internal void Rescan() {foreach(var s in channels){s.Metadata=null;s.Settling=null;} }
    internal void Cancel() {
        if(pending==null)return;pending.State.Deferred=true;store.Save();pending=null;
    }
    internal void Tick() {
        if(busy || stopping)return;busy=true;
        try {
            if(pending!=null && !Valid(pending)) Cancel();
            foreach(var status in channels) {
                if(!CanWork()) break;
                try {Poll(status);}catch(Exception e) {status.Message="Inspection failed safely: "+e.GetType().Name;status.Metadata=null;status.Settling=null;status.Outcome=null;}
            }
        }finally {busy=false;}
    }
    void Poll(ChannelStatus status) {
        Channel channel=status.Channel;
        status.Outcome=null;
        ProcessSnapshot processes=host.Snapshot(channel);
        // A delayed, persisted post-launch check catches changes made after Update.exe starts.
        var verification=store.Value.Generations.Find(g=>g.VerifyAfter!=0 && g.Key.StartsWith(channel.Root+"|",StringComparison.OrdinalIgnoreCase));
        if(verification!=null) {
            if(host.Now.Ticks<verification.VerifyAfter){status.Message="Waiting for post-restart file verification.";return;}
            var checkedInstall=Installation.Inspect(channel.Root,channel.Executable,dist);
            verification.VerifyAfter=0;
            bool failed=checkedInstall.Kind!=InstallationKind.Healthy || checkedInstall.Generation!=verification.Key;
            // A successful explicit recovery must not erase an earlier automatic prohibition.
            verification.VerificationFailed |= failed;
            if(failed) store.Value.Automatic=false;
            if(!store.Save()){status.Message=store.Error;status.Outcome=WorkerResult.Refused;return;}
            status.Metadata=null;
            if(failed) {
                status.Message="Post-restart verification failed; automatic restarts disabled. Repair & Restart remains available.";
                host.Notify(status.Message);
                status.Outcome=WorkerResult.Refused;
                return;
            }
        }
        string metadata=Installation.GetMetadataSignature(channel.Root,channel.Executable,dist);
        if(status.Metadata!=metadata || status.Installation==null) {
            status.Installation=Installation.Inspect(channel.Root,channel.Executable,dist);status.Metadata=metadata;status.Seeded=false;
        }
        Installation installation=status.Installation;
        if(installation.Kind==InstallationKind.Healthy) {
            status.Message=store.Writable?"Healthy: loader and original verified.":"Healthy files; automatic work disabled: "+store.Error;
            if(store.Writable && !store.Value.Paused && !status.Seeded) {cache.Capture(installation);status.Seeded=true;}
            status.Outcome=WorkerResult.Done;
            return;
        }
        if(installation.Kind!=InstallationKind.Stock && installation.Kind!=InstallationKind.Interrupted) {status.Message=installation.Reason;StableRefusal(status,metadata);return;}
        if(!store.Writable){status.Message=store.Error;status.Outcome=WorkerResult.Refused;return;}
        GenerationState generation=store.Find(installation.Generation);
        if(generation==null) {
            generation=RestartPolicy.First(installation.Generation,processes.Cohort,host.Now,processes.Oldest,host.Interactive && processes.Known);
            if(!store.Add(generation)){status.Message=store.Error;status.Outcome=WorkerResult.Refused;return;}
        }
        if(processes.Discord.Count>0 && !generation.Deferred &&
            (store.Value.Paused || !store.Value.Automatic || !host.Interactive || generation.Cohort!=processes.Cohort)) {
            generation.Deferred=true;
            if(!store.Save()){status.Message=store.Error;status.Outcome=WorkerResult.Refused;return;}
        }
        if(store.Value.Paused){status.Message="Paused.";status.Outcome=WorkerResult.Refused;return;}
        if(!processes.Known || processes.Updater){status.Settling=null;status.Message="Waiting: updater active or process access is unknown.";return;}
        string settling=Installation.GetSettlingMetadataSignature(channel.Root);
        if(status.Settling!=settling){status.Settling=settling;status.SettledSince=host.Now;}
        string reason;ValidatedLoader loader=cache.Load(dist,out reason);
        if(loader==null && !status.Seeded) {
            status.Seeded=true;
            foreach(var previous in Installation.InspectVersions(channel.Root,channel.Executable,dist)) {
                if(previous.Kind==InstallationKind.Healthy) {cache.Capture(previous);break;}
            }
            loader=cache.Load(dist,out reason);
        }
        if(loader==null){status.Message=reason;StableRefusal(status,metadata);return;}
        if(pending!=null && pending.Status==status){status.Message="Restart countdown: choose Not now to defer.";return;}
        Decision decision=RestartPolicy.Evaluate(generation,host.Now,status.SettledSince,processes.Discord.Count>0,processes.Cohort,host.Interactive,store.Value.Automatic,store.Value.LastRestart);
        if(decision==Decision.Deferred && !generation.Deferred){generation.Deferred=true;if(!store.Save()){status.Message=store.Error;status.Outcome=WorkerResult.Refused;return;}}
        status.Message=decision==Decision.Exhausted?"Automatic attempts stopped; inspect logs or choose Repair & Restart.":decision==Decision.Deferred?"Deferred until Discord exits; Repair & Restart is available.":"Waiting for stable files and full Discord exit.";
        if(decision==Decision.Deferred) status.Outcome=WorkerProtocol.Deferred(status.Channel.Executable);
        if(decision==Decision.Exhausted) status.Outcome=WorkerResult.Refused;
        if(decision==Decision.Repair && pending==null) Repair(status,generation,false);
        else if(decision==Decision.Countdown && pending==null) {
            generation.Reserved=true;store.Value.LastRestart=host.Now.Ticks;
            if(!store.Save()){status.Message=store.Error;status.Outcome=WorkerResult.Refused;return;}
            var request=new PendingRestart {Status=status,State=generation,Processes=processes,Metadata=metadata,Settling=settling};pending=request;
            status.Message="Restart countdown: choose Not now to defer.";
            host.Countdown(channel.Executable,()=>Valid(request),accepted=>Complete(request,accepted));
        }
    }
    void StableRefusal(ChannelStatus status,string metadata) {
        if(status.RefusalMetadata!=metadata) {status.RefusalMetadata=metadata;status.RefusalSince=host.Now;}
        if(host.Now-status.RefusalSince>=TimeSpan.FromSeconds(10)) status.Outcome=WorkerResult.Refused;
    }
    bool Valid(PendingRestart request) {
        try {
            if(!CanWork() || pending!=request || store.Value.Paused || !store.Writable || !host.Interactive || (!request.Manual && !store.Value.Automatic))return false;
            var p=host.Snapshot(request.Status.Channel);
            string reason;var loader=cache.Load(dist,out reason);
            return loader!=null && p.Known && !p.Updater && p.Cohort==request.Processes.Cohort &&
                request.Metadata==Installation.GetMetadataSignature(request.Status.Channel.Root,request.Status.Channel.Executable,dist) &&
                request.Settling==Installation.GetSettlingMetadataSignature(request.Status.Channel.Root);
        }catch{return false;}
    }
    internal bool ManualRepair(ChannelStatus status,Func<bool> confirm) {
        if(busy || stopping || pending!=null || !store.Writable || store.Value.Paused || !host.Interactive) return false;
        busy=true;
        try {
            // Manual recovery bypasses only automatic attempt limits. All safety gates and a
            // fresh user confirmation still apply; no persisted automatic allowance is reset.
            Channel channel=status.Channel;
            Installation fresh=Installation.Inspect(channel.Root,channel.Executable,dist);
            string reason;
            ValidatedLoader loader=cache.Load(dist,out reason);
            ProcessSnapshot processes=host.Snapshot(channel);
            if(loader==null || (fresh.Kind!=InstallationKind.Stock && fresh.Kind!=InstallationKind.Interrupted) ||
                !processes.Known || processes.Updater || status.Settling==null ||
                host.Now-status.SettledSince<TimeSpan.FromSeconds(10) ||
                status.Settling!=Installation.GetSettlingMetadataSignature(channel.Root)) {
                status.Message="Repair is not ready: wait for settled files, no updater, and a validated cached loader.";
                return false;
            }
            GenerationState generation=store.Find(fresh.Generation);
            if(generation==null) {
                status.Message="Wait for this generation to be observed before requesting repair.";
                return false;
            }
            PendingRestart request=new PendingRestart {
                Status=status, State=generation, Processes=processes,
                Metadata=fresh.MetadataSignature, Settling=status.Settling, Manual=true
            };
            pending=request;
            bool accepted=confirm();
            // Shutdown can run while confirmation pumps messages. Complete checks that this
            // exact request is still pending, so a late acceptance cannot authorize any stop.
            return Complete(request,accepted);
        } finally {
            busy=false;
        }
    }
    bool Complete(PendingRestart request,bool accepted) {
        if(pending!=request || stopping) return false;
        bool wasBusy=busy;
        busy=true;
        try {
            if(!accepted || !Valid(request)) {
                Cancel();
                return false;
            }
            Channel channel=request.Status.Channel;
            Installation candidate=Installation.Inspect(channel.Root,channel.Executable,dist);
            if(candidate.Generation!=request.State.Key ||
                (candidate.Kind!=InstallationKind.Stock && candidate.Kind!=InstallationKind.Interrupted)) {
                Cancel();
                request.Status.Metadata=null;
                return false;
            }
            request.State.Reserved=true;
            request.State.Deferred=true;
            store.Value.LastRestart=host.Now.Ticks;
            if(!request.Manual) {
                // Charge automatic stop attempts before their first process side effect.
                request.State.Attempts++;
                request.State.LastAttempt=host.Now.Ticks;
            }
            if(!store.Save()) {
                pending=null;
                return false;
            }
            if(!CanWork())return false;
            bool stopped=false;
            try {
                stopped=host.Stop(channel,request.Processes);
                if(!stopped) {
                    request.Status.Message="Full exit could not be established; repair deferred.";
                    return true;
                }
                Repair(request.Status,request.State,true);
                return true;
            } finally {
                pending=null;
                // Restore a session we stopped even if shutdown was requested meanwhile.
                // A new/remaining process cohort blocks launching a duplicate session.
                Reopen(request,stopped);
                host.Notify(request.Status.Message);
            }
        } finally {
            busy=wasBusy;
        }
    }
    void Reopen(PendingRestart request,bool stopped) {
        Channel channel=request.Status.Channel;
        if(!host.Snapshot(channel).Quiet) return;
        request.State.VerifyAfter=host.Now.AddSeconds(30).Ticks;
        if(!store.Save()) request.Status.Message=store.Error;
        // One bounded reopening attempt, including when state persistence failed after stop.
        if(!host.Launch(channel)) {
            request.Status.Message="Discord could not be reopened through Update.exe. Start Discord normally.";
            FailedVerification(request.State);
            return;
        }
        if(stopped) {
            Installation after=Installation.Inspect(channel.Root,channel.Executable,dist);
            if(after.Kind!=InstallationKind.Healthy) {
                request.Status.Message="Post-restart file verification failed; automatic restarts disabled. Repair & Restart remains available.";
                FailedVerification(request.State);
            }
        }
    }
    void FailedVerification(GenerationState state) {
        state.VerifyAfter=0;
        state.VerificationFailed=true;
        store.Value.Automatic=false;
        store.Save();
    }
    void Repair(ChannelStatus status,GenerationState generation,bool charged) {
        if(!CanWork()) return;
        bool acquired=false;
        using(Mutex gate=InstanceControl.NewRootMutex(status.Channel.Root)) {
            try {
                try { acquired=gate.WaitOne(0); }
                catch(AbandonedMutexException) { acquired=true; }
                if(!acquired) {
                    status.Message="Another helper is inspecting this Discord root.";
                    return;
                }
                if(!host.Snapshot(status.Channel).Quiet) return;
                string reason;
                ValidatedLoader loader=cache.Load(dist,out reason);
                if(loader==null) {
                    status.Message=reason;
                    return;
                }
                Installation fresh=Installation.Inspect(status.Channel.Root,status.Channel.Executable,dist);
                if(fresh.Generation!=generation.Key || (fresh.Kind!=InstallationKind.Stock && fresh.Kind!=InstallationKind.Interrupted)) {
                    status.Metadata=null;
                    return;
                }
                // Exit can alter native updater metadata. Require the observed tree to match.
                if(status.Settling!=Installation.GetSettlingMetadataSignature(status.Channel.Root)) {
                    status.Settling=null;
                    status.Message="Files changed during exit; waiting for a new settled observation.";
                    return;
                }
                if(!charged) {
                    generation.Attempts++;
                    generation.LastAttempt=host.Now.Ticks;
                    if(!store.Save()) return;
                }
                if(!CanWork()) return;
                RepairResult result;
                transactionActive=true;
                try {
                    // Shutdown prevents new work; an entered transaction finishes normally.
                    // Process, updater, pause and state-write guards remain active at each step.
                    result=RepairTransaction.Repair(fresh,loader,
                        ()=>store.Writable && !store.Value.Paused && host.Snapshot(status.Channel).Quiet,null);
                } finally {
                    transactionActive=false;
                }
                status.Message=result.Success?"Repaired: loader and original verified.":"Repair failed safely: "+result.Reason;
                status.Metadata=null;
                if(!result.Success) host.Notify(status.Message);
            } finally {
                if(acquired) gate.ReleaseMutex();
            }
        }
    }
}
}
