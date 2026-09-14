using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
namespace VencordAutoUpdate {
internal sealed class WorkerApplication : ApplicationContext,ISupervisorHost {
    readonly AppPaths paths;
    readonly InstanceControl instance;
    readonly ProcessInspector inspector=new ProcessInspector();
    readonly Supervisor supervisor;
    readonly WorkerBatch batch;
    readonly RotatingLog log;
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly Stopwatch elapsed=Stopwatch.StartNew();
    readonly Control dispatch=new Control();
    readonly List<Action<WorkerResult>> replies=new List<Action<WorkerResult>>();
    readonly RegisteredWaitHandle stopWait,showWait;
    readonly WorkRequests work;
    StatusForm form;
    RestartDialog dialog;
    bool locked,exiting,manual,awaitingWork;
    internal int ExitCode {get;private set;}
    internal WorkerApplication(AppPaths config,InstanceControl control) {
        awaitingWork=config.Command=="--worker-host";
        dispatch.CreateControl();paths=config;instance=control;log=new RotatingLog(paths.Data);
        supervisor=new Supervisor(paths.Channels(),paths.Dist,paths.Data,this);batch=new WorkerBatch(supervisor,()=>elapsed.Elapsed);
        stopWait=ThreadPool.RegisterWaitForSingleObject(instance.StopSignal,(s,t)=>Post(RequestExit),null,Timeout.Infinite,true);
        showWait=ThreadPool.RegisterWaitForSingleObject(instance.ShowSignal,(s,t)=>Post(ShowStatus),null,Timeout.Infinite,false);
        work=new WorkRequests(paths.Data,reply=>Post(()=>ReceiveWork(reply)));
        SystemEvents.SessionSwitch+=SessionChanged;SystemEvents.PowerModeChanged+=PowerChanged;
        timer.Interval=500;timer.Tick+=(s,e)=>Pulse();log.Write("HelperStarted");
        if(paths.Command=="ui")ShowStatus();
        if(awaitingWork)timer.Start();else Post(()=>StartWork());
    }
    public DateTime Now {get{return DateTime.UtcNow;}}
    public bool Interactive {get{return !locked && WorkerIdentity.Ordinary && SessionState.Interactive;}}
    public ProcessSnapshot Snapshot(Channel channel) {return inspector.Snapshot(channel);}
    public bool Stop(Channel channel,ProcessSnapshot cohort) {log.Write("CapturedProcessStopRequested");return inspector.Stop(channel,cohort);}
    public bool Launch(Channel channel) {log.Write("LauncherRequested");return inspector.Launch(channel);}
    public void Countdown(string channel,Func<bool> valid,Action<bool> finished) {
        dialog=new RestartDialog(channel,valid,accepted=>{dialog=null;finished(accepted);Refresh();Post(Pulse);});dialog.Show();
    }
    public void Notify(string message) {log.Write("SupervisorNotification");Refresh();}
    void Post(Action action) {
        try {if(!dispatch.IsDisposed)dispatch.BeginInvoke(action);}
        catch(InvalidOperationException) { }
    }
    void ReceiveWork(Action<WorkerResult> reply) {
        if(exiting) {reply(WorkerResult.Retry);return;}
        awaitingWork=false;replies.Add(reply);
        // Reconcile after receipt even if this request coalesces with an active batch.
        supervisor.Rescan();StartWork();
    }
    void StartWork() {
        if(exiting)return;
        if(!WorkerIdentity.Ordinary) {Finish(WorkerResult.Refused);return;}
        supervisor.RefreshChannels(paths.Channels());
        if(!batch.Running)batch.Start(elapsed.Elapsed);
        timer.Start();Pulse();
    }
    void Pulse() {
        if(instance.StopRequested || exiting){RequestExit();return;}
        if(!WorkerIdentity.Ordinary){Finish(WorkerResult.Refused);return;}
        if(awaitingWork){if(elapsed.Elapsed>=TimeSpan.FromSeconds(5))Finish(WorkerResult.Retry);return;}
        try {
            batch.Step(elapsed.Elapsed);
            if(supervisor.Stopping)CancelConsent();
            Refresh();
            if(batch.Result.HasValue)Finish(batch.Result.Value);
        }catch(Exception e) {
            log.Write("ScanError:"+e.GetType().Name);batch.Abort();CancelConsent();
            batch.Step(elapsed.Elapsed);
            if(batch.Result.HasValue)Finish(batch.Result.Value);
        }
    }
    void Finish(WorkerResult result) {
        timer.Stop();
        foreach(var reply in replies)reply(result);replies.Clear();
        if(!manual) {ExitCode=(int)result;RequestExit();}
    }
    void Refresh(){if(form!=null && !form.IsDisposed)form.RefreshStatus();}
    void ShowStatus() {
        if(exiting)return;manual=true;
        if(form==null || form.IsDisposed) {
            form=new StatusForm(supervisor,()=>Setup("install.ps1"),()=>Setup("uninstall.ps1"),OpenLogs,About,RequestExit,!paths.Portable);
            form.BeforeRepair=BeginInteraction;
            form.AfterRepair=()=>{timer.Start();Post(Pulse);};
            form.PreferencesChanged=()=>Post(StartWork);
            form.FormClosing+=(s,e)=>{if(!exiting){e.Cancel=true;RequestExit();}};
        }
        form.Show();form.Activate();instance.StatusShown();
        if(awaitingWork){awaitingWork=false;Post(StartWork);}
    }
    void BeginInteraction() {
        // Explicit manual interaction gets a new budget, but retains the settled snapshot
        // and requires StatusForm's fresh confirmation before any process stop.
        if(!batch.Running)batch.Start(elapsed.Elapsed);
        timer.Start();
    }
    internal static void Open(string target) {
        try {using(Process p=Process.Start(new ProcessStartInfo(target){UseShellExecute=true})) {}}
        catch(Exception e){MessageBox.Show("Could not open the requested resource: "+e.GetType().Name,"Vencord Auto Update");}
    }
    void OpenLogs(){try{Directory.CreateDirectory(log.DirectoryPath);Open(log.DirectoryPath);}catch(IOException){Notify("Cannot open logs.");}}
    void About(){MessageBox.Show("Vencord Auto Update 0.1.0\r\nIndependent community helper, licensed under GNU GPL version 3.\r\nNo telemetry or automatic downloads. File verification does not prove runtime injection.\r\nSee LICENSE in the release or source checkout.","About Vencord Auto Update");}
    void Setup(string name) {
        if(paths.Portable){MessageBox.Show("Portable fixture mode does not activate installation. Use the setup scripts explicitly with their documented test options.","Portable mode");return;}
        try {
            string script=AppPaths.SetupScript(name);
            using(Process process=Process.Start(SetupStartInfo(script))) {
                if(process==null)throw new InvalidOperationException("Windows did not start setup.");
            }
            // Setup waits for mapped binaries to become free. Drain this UI's work
            // after UAC succeeds; cancellation leaves the current status window open.
            RequestExit();
        }catch(Exception e){MessageBox.Show(e.Message,"Setup could not start");}
    }
    internal static ProcessStartInfo SetupStartInfo(string script) {
        string command="& '"+script.Replace("'","''")+"'";
        string encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        return new ProcessStartInfo(powershell,"-NoProfile -NoExit -ExecutionPolicy Bypass -EncodedCommand "+encoded){UseShellExecute=true,Verb="runas"};
    }
    void SessionChanged(object sender,SessionSwitchEventArgs e) {
        if(e.Reason==SessionSwitchReason.SessionLock || e.Reason==SessionSwitchReason.SessionLogoff)locked=true;
        if(e.Reason==SessionSwitchReason.SessionUnlock || e.Reason==SessionSwitchReason.SessionLogon)locked=false;
        Post(()=>{if(batch.Running){supervisor.Rescan();Pulse();}});
    }
    void PowerChanged(object sender,PowerModeChangedEventArgs e) {if(e.Mode==PowerModes.Resume)Post(()=>{if(batch.Running){supervisor.Rescan();Pulse();}});}
    void CancelConsent() {if(dialog!=null)dialog.Close();if(form!=null)form.CancelConfirmation();}
    void RequestExit() {
        exiting=true;supervisor.Shutdown();CancelConsent();
        if(supervisor.Busy) {timer.Start();return;}
        timer.Stop();
        foreach(var reply in replies)reply(WorkerResult.Retry);replies.Clear();
        ExitThread();
    }
    protected override void ExitThreadCore() {
        SystemEvents.SessionSwitch-=SessionChanged;SystemEvents.PowerModeChanged-=PowerChanged;
        stopWait.Unregister(null);showWait.Unregister(null);work.Dispose();
        timer.Dispose();dispatch.Dispose();if(form!=null)form.Dispose();log.Write("HelperStopped");base.ExitThreadCore();
    }
}
}
