using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
namespace VencordAutoUpdate {
// Parent notifications maintain subscriptions when an expected directory does not exist.
internal sealed class RootNotifications:IDisposable {
    readonly string profile;readonly string[] roots;readonly Action signal;
    readonly Dictionary<string,FileSystemWatcher> watchers=new Dictionary<string,FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
    long events;bool disposed;
    internal RootNotifications(string profile,string[] roots,Action signal){this.profile=profile;this.roots=roots;this.signal=signal;}
    internal long Events {get{return Interlocked.Read(ref events);}}
    void Changed(object sender,FileSystemEventArgs e) {
        foreach(string root in roots)if(TrustedPaths.IsUnder(e.FullPath,root) || TrustedPaths.IsUnder(root,e.FullPath)){Interlocked.Increment(ref events);signal();return;}
        RenamedEventArgs renamed=e as RenamedEventArgs;if(renamed!=null)foreach(string root in roots)if(TrustedPaths.IsUnder(renamed.OldFullPath,root) || TrustedPaths.IsUnder(root,renamed.OldFullPath)){Interlocked.Increment(ref events);signal();return;}
    }
    void Error(object sender,ErrorEventArgs e){Interlocked.Increment(ref events);signal();}
    internal void Refresh() {
        if(disposed)return;
        var desired=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        foreach(string root in roots) {
            string safe=TrustedPaths.WithinProfile(profile,root);
            if(Directory.Exists(safe))desired[safe]=true;
            for(string parent=Path.GetDirectoryName(safe);TrustedPaths.IsUnder(parent,profile);parent=Path.GetDirectoryName(parent)) {
                TrustedPaths.RequireLocal(parent);if(Directory.Exists(parent) && !desired.ContainsKey(parent))desired[parent]=false;
                if(String.Equals(parent,profile,StringComparison.OrdinalIgnoreCase))break;
            }
        }
        // Rebuild on reconciliation, including overflow/error and rapid delete/recreate.
        // New subscriptions exist before old ones are released or a worker snapshots.
        var replacement=new Dictionary<string,FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach(var entry in desired) {
                var watcher=new FileSystemWatcher(entry.Key){IncludeSubdirectories=entry.Value,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size};
                replacement.Add(entry.Key,watcher);
                watcher.Created+=Changed;watcher.Changed+=Changed;watcher.Deleted+=Changed;watcher.Renamed+=Changed;watcher.Error+=Error;watcher.EnableRaisingEvents=true;
            }
        }catch{foreach(var watch in replacement.Values)watch.Dispose();throw;}
        foreach(var watch in watchers.Values)watch.Dispose();watchers.Clear();
        foreach(var entry in replacement)watchers.Add(entry.Key,entry.Value);
    }
    public void Dispose(){if(disposed)return;disposed=true;foreach(var watch in watchers.Values)watch.Dispose();watchers.Clear();}
}
}
