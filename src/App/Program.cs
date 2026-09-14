using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
namespace VencordAutoUpdate {
internal static class Program {
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")]static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")]static extern uint GetFileType(IntPtr handle);
    static void ConsoleReady() {
        if(GetFileType(GetStdHandle(-11))==0)AttachConsole(UInt32.MaxValue);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true});
        Console.SetError(new StreamWriter(Console.OpenStandardError(),new UTF8Encoding(false)){AutoFlush=true});
    }
    [STAThread]static int Main(string[] args) {
        AppPaths paths;
        try {paths=new AppPaths(args);}
        catch(ArgumentException e){ConsoleReady();Console.Error.WriteLine(e.Message);return 2;}
        catch(Exception e){ConsoleReady();Console.Error.WriteLine("Cannot resolve startup paths: "+e.Message);return 1;}
        try {
            if(paths.Command=="--status") {
                ConsoleReady();
                foreach(var channel in paths.Channels()) {
                    var value=Installation.Inspect(channel.Root,channel.Executable,paths.Dist);
                    Console.WriteLine(channel.Executable+": "+value.Kind+" — "+value.Reason);
                }
                return 0;
            }
            if(paths.Command=="--stop")return InstanceControl.Stop(paths.Data)?0:4;
            if(!WorkerIdentity.Ordinary)return (int)WorkerResult.Refused;
            if(paths.Command=="--worker")return RequestWork(paths);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var instance=new InstanceControl(paths.Data)) {
                if(!instance.Primary && (paths.Command!="ui" || !instance.ShowOrAcquire()))return 0;
                using(var app=new WorkerApplication(paths,instance)) {Application.Run(app);return app.ExitCode;}
            }
        }catch(Exception e){ConsoleReady();Console.Error.WriteLine("Helper could not start safely: "+e.GetType().Name+": "+e.Message);return paths.Command=="--worker" || paths.Command=="--worker-host"?(int)WorkerResult.Retry:1;}
    }
    static int RequestWork(AppPaths paths) {
        // The service-facing process owns only this acknowledgement. The ordinary
        // host owns the instance, consent, transactions and any user-opened Status.
        // Every candidate host competes for the exact SID/data instance. Losing
        // hosts exit; callers all connect to the winner's pipe. The relay never
        // publishes an instance/Show/Stop endpoint that it cannot actually serve.
        string arguments="--worker-host";
        if(paths.Portable)arguments+=" --data-dir "+Quote(paths.Data)+" --discord-root "+Quote(paths.CustomRoot)+" --vencord-dist "+Quote(paths.Dist);
        using(var host=Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,arguments){UseShellExecute=false})) {
            if(host==null)return (int)WorkerResult.Retry;
            return (int)WorkRequests.Request(paths.Data);
        }
    }
    static string Quote(string path) {return "\""+path.Replace("\"","\\\"")+"\"";}
}
}
