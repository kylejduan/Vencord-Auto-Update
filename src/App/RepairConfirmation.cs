using System.Drawing;
using System.Windows.Forms;
namespace VencordAutoUpdate {
internal sealed class RepairConfirmation : Form {
    internal RepairConfirmation(string executable) {
        Text="Confirm Repair & Restart";
        AccessibleName=Text;
        ClientSize=new Size(490,180);
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=false;
        MinimizeBox=false;
        StartPosition=FormStartPosition.CenterParent;
        Label warning=new Label {
            Text="Close "+executable+" now, repair Vencord, and reopen Discord?\r\n\r\nThis could interrupt a call or draft.",
            AccessibleName="Discord restart warning"
        };
        warning.SetBounds(20,18,450,92);
        Controls.Add(warning);
        Button confirm=new Button {Text="Repair && Restart",DialogResult=DialogResult.Yes};
        confirm.SetBounds(160,128,165,32);
        Controls.Add(confirm);
        Button cancel=new Button {Text="Cancel",DialogResult=DialogResult.Cancel};
        cancel.SetBounds(345,128,120,32);
        Controls.Add(cancel);
        AcceptButton=cancel;
        CancelButton=cancel;
    }
}
}
