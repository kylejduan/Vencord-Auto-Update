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
    // A main process has no live, earlier Discord parent under the same root. Child processes
    // (GPU, renderer, utility, crash handler) start and exit freely during one session.
    internal readonly bool Main;
    internal readonly int Parent;
    internal ProcessIdentity(int pid,long start,string path):this(pid,start,path,true,0) {}
    internal ProcessIdentity(int pid,long start,string path,bool main,int parent) {Pid=pid;StartTicks=start;Path=path;Main=main;Parent=parent;}
    internal string Key { get {return Pid+"@"+StartTicks+"@"+Path.ToUpperInvariant();} }
}
internal sealed class ProcessSnapshot {
    internal bool Known=true, Updater;
    internal readonly List<ProcessIdentity> Discord=new List<ProcessIdentity>();
    // The session identity: a bounded digest of the main processes only, so child process churn
    // does not look like a new session. Persisted per generation.
    internal string Cohort { get {
        var values=new List<string>();foreach(var p in Discord) if(p.Main) values.Add(p.Key);
        if(values.Count==0) foreach(var p in Discord) values.Add(p.Key);
        if(values.Count==0) return "";
        values.Sort(StringComparer.Ordinal);return SafePath.TextHash(String.Join(";",values));
    } }
    internal DateTime? Oldest { get { DateTime? result=null;foreach(var p in Discord) {DateTime t=new DateTime(p.StartTicks,DateTimeKind.Utc);if(!result.HasValue || t<result.Value)result=t;}return result; } }
    internal bool Quiet { get {return Known && !Updater && Discord.Count==0;} }
}
internal sealed class ProcessInspector {
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] struct BasicInformation { internal IntPtr ExitStatus,Peb,Affinity,Priority,Id,Parent; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process,int information,ref BasicInformation value,int size,out int returned);
    static string ImagePath(int pid) {int parent;return Describe(pid,out parent);}
    static string Describe(int pid,out int parent) {
        IntPtr h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero) throw new Win32Exception();
        try {
            StringBuilder value=new StringBuilder(32768);int size=value.Capacity;if(!QueryFullProcessImageName(h,0,value,ref size))throw new Win32Exception();
            var basic=new BasicInformation();int returned;
            if(NtQueryInformationProcess(h,0,ref basic,Marshal.SizeOf(typeof(BasicInformation)),out returned)!=0)throw new Win32Exception("Parent process query failed.");
            parent=(int)basic.Parent.ToInt64();
            return Channel.Canonical(value.ToString());
        }
        finally {CloseHandle(h);}
    }
    internal ProcessSnapshot Snapshot(Channel channel) {
        ProcessSnapshot result=new ProcessSnapshot();
        var found=new List<ProcessIdentity>();var parents=new Dictionary<int,int>();
        foreach(string name in new [] {"Discord","DiscordPTB","DiscordCanary","Update"}) {
            Process[] processes;
            try {processes=Process.GetProcessesByName(name);} catch(Win32Exception) {result.Known=false;continue;}
            foreach(Process p in processes) using(p) {
                try {
                    int parent;string path=Describe(p.Id,out parent);
                    if(!AppPaths.Under(path,channel.Root)) continue;
                    if(name=="Update") result.Updater=true;
                    else {found.Add(new ProcessIdentity(p.Id,p.StartTime.ToUniversalTime().Ticks,path));parents[p.Id]=parent;}
                } catch(Exception e) {
                    if(!(e is Win32Exception) && !(e is InvalidOperationException) && !(e is IOException) && !(e is UnauthorizedAccessException) && !(e is ArgumentException)) throw;
                    // A process which disappeared is harmless. Inaccessible relevant names fail closed,
                    // even when their root cannot be established.
                    try {if(!p.HasExited) result.Known=false;}catch {result.Known=false;}
                }
            }
        }
        // A parent counts only if it is a Discord process under this root that started first,
        // so a reused parent PID cannot demote a new main process.
        var started=new Dictionary<int,long>();foreach(var p in found) started[p.Pid]=p.StartTicks;
        foreach(var p in found) {
            long parentStart;bool child=started.TryGetValue(parents[p.Pid],out parentStart) && parentStart<=p.StartTicks && parents[p.Pid]!=p.Pid;
            result.Discord.Add(new ProcessIdentity(p.Pid,p.StartTicks,p.Path,!child,parents[p.Pid]));
        }
        return result;
    }
    // Stops the captured session only: its main processes must be unchanged when termination begins.
    // Later rounds end only stragglers whose parent belonged to that session (children spawned while
    // it was stopping) and that existed before termination began. Any other process is a different
    // session and is never chased.
    internal bool Stop(Channel channel,ProcessSnapshot captured) {
        DateTime began=DateTime.UtcNow,deadline=began.AddSeconds(15);
        ProcessSnapshot current=Snapshot(channel);
        if(!captured.Known || !current.Known || current.Updater || captured.Cohort!=current.Cohort) return false;
        var session=new HashSet<int>();foreach(var identity in current.Discord) session.Add(identity.Pid);
        try {
            for(int round=0;round<3;round++) {
                if(round>0) {
                    current=Snapshot(channel);
                    if(!current.Known || current.Updater) return false;
                    if(current.Discord.Count==0) return true;
                    foreach(var identity in current.Discord) if(identity.StartTicks>=began.Ticks || !session.Contains(identity.Parent)) return false;
                    foreach(var identity in current.Discord) session.Add(identity.Pid);
                }
                if(!Terminate(channel,current,deadline)) return false;
            }
            return Snapshot(channel).Quiet;
        } catch(Exception e) {
            if(!(e is Win32Exception) && !(e is InvalidOperationException) && !(e is ArgumentException) && !(e is IOException)) throw;
            return false;
        }
    }
    bool Terminate(Channel channel,ProcessSnapshot snapshot,DateTime deadline) {
        // Revalidate all held handles before the first termination.
        List<Process> held=new List<Process>();
        try {
            foreach(var identity in snapshot.Discord) {
                Process p;
                try {p=Process.GetProcessById(identity.Pid);} catch(ArgumentException) {continue;}
                held.Add(p);
                // Opening Handle pins this process object against PID reuse.
                IntPtr handle=p.Handle;
                if(p.HasExited) continue;
                if(handle==IntPtr.Zero || p.StartTime.ToUniversalTime().Ticks!=identity.StartTicks || !SafePath.Same(ImagePath(p.Id),identity.Path) || !AppPaths.Under(identity.Path,channel.Root)) return false;
            }
            foreach(Process p in held) {try {if(!p.HasExited) p.Kill();} catch(InvalidOperationException) {} catch(Win32Exception) {if(!p.HasExited) return false;}}
            foreach(Process p in held) {int remaining=(int)Math.Max(0,(deadline-DateTime.UtcNow).TotalMilliseconds);if(!p.WaitForExit(remaining))return false;}
            return true;
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
