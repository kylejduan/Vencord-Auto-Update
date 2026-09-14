using System;
using System.Collections.Generic;
namespace VencordAutoUpdate {
// A cohort belongs to exactly one worker-hinted OS-derived root.
internal sealed class DeferredProcessWaits:IDisposable {
    readonly Dictionary<int,OwnedProcessWaits> roots=new Dictionary<int,OwnedProcessWaits>();
    internal bool Known {get;private set;}
    internal int LiveMask {get {int mask=0;foreach(var root in roots)if(root.Value.Count>0 && !root.Value.AllExited)mask|=root.Key;return mask;}}
    internal bool AnyExited {get {foreach(var root in roots)if(root.Value.AllExited)return true;return false;}}
    internal static DeferredProcessWaits Capture(string[] paths,int mask,Action exit) {
        var result=new DeferredProcessWaits();result.Known=true;
        try {
            for(int i=0;i<3;i++)if((mask & (1<<i))!=0) {
                var cohort=OwnedProcessWaits.Capture(new[]{paths[i]},exit);
                result.roots.Add(1<<i,cohort);result.Known &= cohort.Known;
            }
            return result;
        }catch{result.Dispose();throw;}
    }
    public void Dispose(){foreach(var root in roots)root.Value.Dispose();roots.Clear();}
}
}
