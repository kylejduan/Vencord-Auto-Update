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
    // Returns false when an eligible directory could not be subscribed; the caller retries later.
    internal bool Refresh() {
        if(disposed)return true;
        var desired=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        foreach(string root in roots) {
            // An unsupported root (for example a reparse point) gets no subscription of its own.
            // The ordinary worker refuses only that channel; the remaining roots stay protected.
            var subscriptions=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            try {
                string safe=TrustedPaths.WithinProfile(profile,root);
                if(Directory.Exists(safe))subscriptions[safe]=true;
                for(string parent=Path.GetDirectoryName(safe);TrustedPaths.IsUnder(parent,profile);parent=Path.GetDirectoryName(parent)) {
                    TrustedPaths.RequireLocal(parent);if(Directory.Exists(parent))subscriptions[parent]=false;
                    if(String.Equals(parent,profile,StringComparison.OrdinalIgnoreCase))break;
                }
            }catch(Exception e){if(!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is ArgumentException) && !(e is NotSupportedException))throw;continue;}
            foreach(var entry in subscriptions){bool recursive;desired.TryGetValue(entry.Key,out recursive);desired[entry.Key]=recursive || entry.Value;}
        }
        // Rebuild on reconciliation, including overflow/error and rapid delete/recreate.
        // New subscriptions exist before old ones are released or a worker snapshots.
        var replacement=new Dictionary<string,FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        Exception first=null;
        foreach(var entry in desired) {
            FileSystemWatcher watcher=null;
            try {
                watcher=new FileSystemWatcher(entry.Key){IncludeSubdirectories=entry.Value,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size};
                watcher.Created+=Changed;watcher.Changed+=Changed;watcher.Deleted+=Changed;watcher.Renamed+=Changed;watcher.Error+=Error;watcher.EnableRaisingEvents=true;
                replacement.Add(entry.Key,watcher);
            }catch(Exception e) {
                // One vanished or inaccessible directory loses only its own subscription.
                if(watcher!=null)watcher.Dispose();
                if(!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is ArgumentException) && !(e is System.ComponentModel.Win32Exception))throw;
                if(first==null)first=e;
            }
        }
        // With no subscription at all, keep the previous set and let the caller retry.
        if(replacement.Count==0 && first!=null)throw first;
        foreach(var watch in watchers.Values)watch.Dispose();watchers.Clear();
        foreach(var entry in replacement)watchers.Add(entry.Key,entry.Value);
        return first==null;
    }
    public void Dispose(){if(disposed)return;disposed=true;foreach(var watch in watchers.Values)watch.Dispose();watchers.Clear();}
}
}
