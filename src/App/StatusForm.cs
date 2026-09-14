using System;
using System.Drawing;
using System.Windows.Forms;
namespace VencordAutoUpdate {
internal sealed class StatusForm : Form {
    readonly ListBox status=new ListBox();
    readonly CheckBox paused=new CheckBox {Text="Pause repairs",AccessibleName="Pause repairs"};
    readonly CheckBox automatic=new CheckBox {Text="Allow visible automatic restart countdowns",AccessibleName="Allow automatic restarts"};
    readonly Supervisor supervisor;
    internal Action BeforeRepair,AfterRepair,PreferencesChanged;
    bool updating;
    RepairConfirmation confirmation;
    internal StatusForm(Supervisor engine,Action install,Action uninstall,Action logs,Action about,Action exit,bool setupEnabled) {
        supervisor = engine;
        Text = "Vencord Auto Update";
        AccessibleName = "Vencord Auto Update status and setup";
        AutoScaleDimensions = new SizeF(6F, 13F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(780, 440);
        // Fixed control positions need this full client area; include window chrome.
        // WinForms scales the client dimensions and minimum with font/DPI scaling.
        MinimumSize = SizeFromClientSize(ClientSize);
        Label intro=new Label {Text="Restores the local Vencord loader after Discord updates. Protection runs through the Windows service.\r\nThe helper works offline; first install Vencord using its official installer. Closing this window exits status; installed service protection continues.",AutoSize=false};intro.SetBounds(16,12,748,53);intro.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(intro);
        Label channelsLabel=new Label {Text="Discord channel repair status",AutoSize=true};channelsLabel.SetBounds(16,70,748,20);Controls.Add(channelsLabel);
        status.SetBounds(16,94,748,147);status.HorizontalScrollbar=true;status.AccessibleName="Discord channel repair status";status.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(status);
        paused.SetBounds(16,254,200,26);automatic.SetBounds(220,254,490,26);Controls.Add(paused);Controls.Add(automatic);
        paused.CheckedChanged+=(s,e)=>Preferences();automatic.CheckedChanged+=(s,e)=>Preferences();
        AddButton("Repair && Restart",16,294,()=>RepairSelected(),"Repair and restart selected Discord channel");
        AddButton("Open logs",206,294,logs,"Open helper logs");AddButton("Official Vencord",396,294,()=>WorkerApplication.Open("https://vencord.dev/download/"),"Open official Vencord download");
        AddButton("Install",16,346,install,"Install Windows service").Enabled=setupEnabled;AddButton("Uninstall",206,346,uninstall,"Uninstall Windows service").Enabled=setupEnabled;AddButton("About / License",396,346,about,"About and GPL license");AddButton("Exit helper",586,346,exit,"Exit helper");
        RefreshStatus();
    }
    Button AddButton(string label,int x,int y,Action action,string accessible) {Button b=new Button {Text=label,AccessibleName=accessible};b.SetBounds(x,y,178,36);b.Click+=(s,e)=>action();Controls.Add(b);return b;}
    void Preferences() {if(!updating) {if(!supervisor.SetPreferences(paused.Checked,automatic.Checked))MessageBox.Show(this,supervisor.Store.Error,"Preferences could not be saved");if(PreferencesChanged!=null)PreferencesChanged();}}
    internal void RefreshStatus() {
        int selected=status.SelectedIndex;status.BeginUpdate();status.Items.Clear();
        foreach(var s in supervisor.Channels)status.Items.Add(s.Channel.Executable+": "+s.Message);
        if(status.Items.Count==0)status.Items.Add("No supported Discord installation found. Install Discord and Vencord first.");
        status.SelectedIndex=Math.Min(Math.Max(selected,0),status.Items.Count-1);status.EndUpdate();
        updating=true;paused.Checked=supervisor.Store.Value.Paused;automatic.Checked=supervisor.Store.Value.Automatic;updating=false;
    }
    internal void CancelConfirmation() {
        if(confirmation==null || confirmation.IsDisposed) return;
        confirmation.DialogResult=DialogResult.Cancel;
        confirmation.Close();
    }
    bool ConfirmRepair(string executable) {
        using(var prompt=new RepairConfirmation(executable)) {
            confirmation=prompt;
            try { return prompt.ShowDialog(this)==DialogResult.Yes; }
            finally { confirmation=null; }
        }
    }
    internal void RepairSelected() {
        int selected=status.SelectedIndex;
        if(selected<0 || selected>=supervisor.Channels.Count) return;
        if(BeforeRepair!=null)BeforeRepair();
        if(supervisor.Stopping)return;
        var channel=supervisor.Channels[selected];
        bool started;
        try {started=supervisor.ManualRepair(channel,()=>ConfirmRepair(channel.Channel.Executable));}
        finally {if(AfterRepair!=null)AfterRepair();}
        // Do not open another modal status box while an IPC shutdown is unwinding consent.
        if(supervisor.Stopping) return;
        RefreshStatus();
        if(!started) MessageBox.Show(this,channel.Message,"Repair status");
    }
}
}
