using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
namespace VencordAutoUpdate {
internal sealed class OwnedProcessWaits:IDisposable {
    sealed class ProcessWait:WaitHandle {internal ProcessWait(IntPtr handle){SafeWaitHandle=new SafeWaitHandle(handle,true);}}
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr handle,int flags,StringBuilder path,ref int count);
    readonly List<WaitHandle> handles=new List<WaitHandle>();readonly List<RegisteredWaitHandle> waits=new List<RegisteredWaitHandle>();
    internal bool Known {get;private set;}
    internal int Count {get{return handles.Count;}}
    internal bool AllExited {get{foreach(var handle in handles)if(!handle.WaitOne(0))return false;return true;}}
    internal static OwnedProcessWaits Capture(string[] roots,Action exit) {
        var result=new OwnedProcessWaits();result.Known=true;
        try {
            foreach(string name in new[]{"Discord","DiscordPTB","DiscordCanary","Update"}) {
                foreach(Process process in Process.GetProcessesByName(name))using(process) {
                    ProcessWait handle=null;
                    try {
                        IntPtr raw=OpenProcess(0x1000|0x100000,false,process.Id);if(raw==IntPtr.Zero)throw new Win32Exception();handle=new ProcessWait(raw);
                        var image=new StringBuilder(32768);int length=image.Capacity;if(!QueryFullProcessImageName(raw,0,image,ref length))throw new Win32Exception();
                        string path=TrustedPaths.RequireLocal(image.ToString());bool match=false;
                        foreach(string root in roots)if(TrustedPaths.IsUnder(path,TrustedPaths.RequireLocal(root))){match=true;break;}
                        if(!match)continue;
                        result.handles.Add(handle);handle=null;
                    }catch(Exception e){if(!(e is Win32Exception) && !(e is IOException) && !(e is InvalidOperationException) && !(e is UnauthorizedAccessException))throw;try{if(!process.HasExited)result.Known=false;}catch{result.Known=false;}}
                    finally{if(handle!=null)handle.Dispose();}
                }
            }
            foreach(var handle in result.handles)result.waits.Add(ThreadPool.RegisterWaitForSingleObject(handle,delegate{exit();},null,Timeout.Infinite,true));
            return result;
        }catch{result.Dispose();throw;}
    }
    public void Dispose(){foreach(var wait in waits)wait.Unregister(null);waits.Clear();foreach(var handle in handles)handle.Dispose();handles.Clear();}
}
}
