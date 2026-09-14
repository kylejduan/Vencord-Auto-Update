using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace VencordAutoUpdate {
internal sealed class ProcessIdentity {
    internal readonly int Pid;
    internal readonly long StartTicks;
    internal readonly string Path;
    internal ProcessIdentity(int pid,long start,string path) {Pid=pid;StartTicks=start;Path=path;}
    internal string Key { get {return Pid+"@"+StartTicks+"@"+Path.ToUpperInvariant();} }
}
internal sealed class ProcessSnapshot {
    internal bool Known=true, Updater;
    internal readonly List<ProcessIdentity> Discord=new List<ProcessIdentity>();
    internal string Cohort { get { var values=new List<string>();foreach(var p in Discord) values.Add(p.Key); values.Sort(StringComparer.Ordinal);return String.Join(";",values); } }
    internal DateTime? Oldest { get { DateTime? result=null;foreach(var p in Discord) {DateTime t=new DateTime(p.StartTicks,DateTimeKind.Utc);if(!result.HasValue || t<result.Value)result=t;}return result; } }
    internal bool Quiet { get {return Known && !Updater && Discord.Count==0;} }
}
internal sealed class ProcessInspector {
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static string ImagePath(int pid) {
        IntPtr h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero) throw new Win32Exception();
        try {StringBuilder value=new StringBuilder(32768);int size=value.Capacity;if(!QueryFullProcessImageName(h,0,value,ref size))throw new Win32Exception();return Channel.Canonical(value.ToString());}
        finally {CloseHandle(h);}
    }
    internal ProcessSnapshot Snapshot(Channel channel) {
        ProcessSnapshot result=new ProcessSnapshot();
        foreach(string name in new [] {"Discord","DiscordPTB","DiscordCanary","Update"}) {
            Process[] processes;
            try {processes=Process.GetProcessesByName(name);} catch(Win32Exception) {result.Known=false;continue;}
            foreach(Process p in processes) using(p) {
                try {
                    string path=ImagePath(p.Id);
                    if(!AppPaths.Under(path,channel.Root)) continue;
                    if(name=="Update") result.Updater=true;
                    else result.Discord.Add(new ProcessIdentity(p.Id,p.StartTime.ToUniversalTime().Ticks,path));
                } catch(Exception e) {
                    if(!(e is Win32Exception) && !(e is InvalidOperationException) && !(e is IOException) && !(e is UnauthorizedAccessException) && !(e is ArgumentException)) throw;
                    // A process which disappeared is harmless. Inaccessible relevant names fail closed,
                    // even when their root cannot be established.
                    try {if(!p.HasExited) result.Known=false;}catch {result.Known=false;}
                }
            }
        }
        return result;
    }
    internal bool Stop(Channel channel,ProcessSnapshot captured) {
        ProcessSnapshot current=Snapshot(channel);
        if(!captured.Known || !current.Known || current.Updater || captured.Cohort!=current.Cohort) return false;
        // Revalidate all held handles before the first termination. Never chase a new cohort.
        List<Process> held=new List<Process>();
        try {
            foreach(var identity in captured.Discord) {
                Process p=Process.GetProcessById(identity.Pid);held.Add(p);
                // Opening Handle pins this process object against PID reuse.
                IntPtr handle=p.Handle;
                if(handle==IntPtr.Zero || p.StartTime.ToUniversalTime().Ticks!=identity.StartTicks || !SafePath.Same(ImagePath(p.Id),identity.Path) || !AppPaths.Under(identity.Path,channel.Root)) return false;
            }
            current=Snapshot(channel);if(!current.Known || current.Updater || current.Cohort!=captured.Cohort)return false;
            foreach(Process p in held) if(!p.HasExited) p.Kill();
            DateTime deadline=DateTime.UtcNow.AddSeconds(15);
            foreach(Process p in held) {int remaining=(int)Math.Max(0,(deadline-DateTime.UtcNow).TotalMilliseconds);if(!p.WaitForExit(remaining))return false;}
            return Snapshot(channel).Quiet;
        } catch(Exception e) {
            if(!(e is Win32Exception) && !(e is InvalidOperationException) && !(e is ArgumentException) && !(e is IOException)) throw;
            return false;
        } finally {foreach(Process p in held)p.Dispose();}
    }
    internal bool Launch(Channel channel) {
        try {
            string updater=Path.Combine(channel.Root,"Update.exe");SafePath.RequireFile(updater);
            Process p=Process.Start(new ProcessStartInfo(updater,"--processStart "+channel.Executable) {UseShellExecute=false,WorkingDirectory=channel.Root});
            if(p==null)return false;p.Dispose();return true;
        } catch(Exception e) {if(!(e is Win32Exception) && !(e is IOException) && !(e is InvalidOperationException))throw;return false;}
    }
}
}
