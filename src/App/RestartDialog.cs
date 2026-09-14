using System;
using System.Drawing;
using System.Windows.Forms;
namespace VencordAutoUpdate {
internal sealed class RestartDialog : Form {
    readonly Timer timer=new Timer();
    readonly Label text=new Label();
    readonly System.Diagnostics.Stopwatch elapsed=new System.Diagnostics.Stopwatch();
    readonly Func<TimeSpan> clock;
    TimeSpan shown;
    readonly Func<bool> valid;
    readonly Action<bool> finished;
    bool accepted,reported;
    internal RestartDialog(string channel,Func<bool> check,Action<bool> done) {
        clock=()=>elapsed.Elapsed;valid=check;finished=done;Text="Discord restart — Vencord Auto Update";AccessibleName="Cancelable Discord restart";
        ClientSize=new Size(490,185);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterScreen;TopMost=true;
        text.SetBounds(20,18,450,100);text.AccessibleName="Restart warning and seconds remaining";Controls.Add(text);
        Button cancel=new Button {Text="Not now",AccessibleName="Defer repair until Discord exits",DialogResult=DialogResult.Cancel};cancel.SetBounds(345,132,120,32);Controls.Add(cancel);CancelButton=cancel;cancel.Click+=(s,e)=>Close();
        timer.Interval=1000;timer.Tick+=(s,e)=>UpdateCountdown(channel);FormClosed+=(s,e)=>{timer.Stop();timer.Dispose();if(!reported){reported=true;finished(accepted);}};
        Shown+=(s,e)=>{elapsed.Start();shown=clock();UpdateCountdown(channel);if(!IsDisposed)timer.Start();};
    }
    internal RestartDialog(string channel,Func<bool> check,Action<bool> done,Func<TimeSpan> monotonic):this(channel,check,done) {clock=monotonic;}
    void UpdateCountdown(string channel) {
        if(!valid()){Close();return;}
        int seconds=(int)Math.Ceiling((TimeSpan.FromSeconds(20)-(clock()-shown)).TotalSeconds);
        text.Text=channel+" will close and restart in "+Math.Max(0,seconds)+" seconds to restore Vencord.\r\n\r\nThis could interrupt a call or draft. Choose Not now or close this window to defer until Discord exits.";
        if(seconds<=0){accepted=true;Close();}
    }
}
}
