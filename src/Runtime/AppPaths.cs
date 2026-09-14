using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace VencordAutoUpdate {
internal sealed class Channel {
    internal readonly string Root, Executable;
    internal Channel(string root,string executable) { Root=Canonical(root); Executable=executable; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetLongPathName(string path,StringBuilder buffer,uint length);
    internal static string Canonical(string path) {
        string value=SafePath.Local(path);
        if(Directory.Exists(value) || File.Exists(value)) {
            StringBuilder result=new StringBuilder(32768);
            uint count=GetLongPathName(value,result,(uint)result.Capacity);
            if(count==0 || count>=result.Capacity) throw new IOException("Cannot resolve canonical local path.");
            value=SafePath.Local(result.ToString());
        } else if(value.IndexOf('~')>=0) throw new IOException("Unresolved short path aliases are unsupported.");
        return value;
    }
}
internal sealed class AppPaths {
    internal readonly string Data, Dist, CustomRoot;
    internal readonly string Command;
    internal bool Portable { get { return CustomRoot!=null; } }
    internal AppPaths(string[] args) {
        string data=null,root=null,dist=null,command=null;
        for(int i=0;i<args.Length;i++) {
            string a=args[i];
            if(a=="--worker-host" || a=="--worker" || a=="--status" || a=="--stop") { if(command!=null) throw new ArgumentException("Choose one command.");command=a; }
            else if(a=="--data-dir" || a=="--discord-root" || a=="--vencord-dist") {
                if(++i==args.Length) throw new ArgumentException(a+" needs a fully qualified path.");
                if(a=="--data-dir") { if(data!=null) throw new ArgumentException("Duplicate data directory.");data=args[i]; }
                else if(a=="--discord-root") { if(root!=null) throw new ArgumentException("Duplicate Discord root.");root=args[i]; }
                else { if(dist!=null) throw new ArgumentException("Duplicate Vencord dist.");dist=args[i]; }
            } else throw new ArgumentException("Unknown argument: "+a);
        }
        if(((data==null)!=(root==null) && !(command=="--stop" && data!=null && root==null)) || (dist!=null && root==null)) throw new ArgumentException("Portable use requires both --data-dir and --discord-root; --vencord-dist is optional only with them.");
        Data=Channel.Canonical(data ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VencordAutoUpdate","data"));
        CustomRoot=root==null?null:Channel.Canonical(root);
        Dist=Channel.Canonical(dist ?? (root==null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Vencord","dist"):Path.Combine(Path.GetDirectoryName(CustomRoot),"Vencord","dist")));
        Command=command ?? "ui";
        if(CustomRoot!=null) {
            ChannelFor(CustomRoot);
            if(Under(Data,CustomRoot) || Under(CustomRoot,Data) || Under(Data,Dist) || Under(Dist,Data)) throw new ArgumentException("Portable helper data must be separate from Discord and Vencord directories.");
        }
    }
    internal static bool Under(string path,string root) { return SafePath.Same(path,root) || path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase); }
    static Channel ChannelFor(string root) {
        string name=Path.GetFileName(root);
        foreach(string known in new [] { "Discord","DiscordPTB","DiscordCanary" }) if(String.Equals(name,known,StringComparison.OrdinalIgnoreCase)) return new Channel(root,known+".exe");
        throw new ArgumentException("Discord root must be named Discord, DiscordPTB or DiscordCanary.");
    }
    internal IList<Channel> Channels() {
        if(CustomRoot!=null) return new [] { ChannelFor(CustomRoot) };
        List<Channel> result=new List<Channel>();
        string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach(string name in new [] { "Discord","DiscordPTB","DiscordCanary" }) {
            string root=Path.Combine(local,name);if(Directory.Exists(root)) result.Add(ChannelFor(root));
        }
        return result;
    }
    internal static string SetupScript(string name) {
        if(name!="install.ps1" && name!="uninstall.ps1") throw new ArgumentException("Unknown setup action.");
        string location=AppDomain.CurrentDomain.BaseDirectory;
        foreach(string candidate in new [] {Path.Combine(location,name),Path.Combine(location,"scripts",name),Path.Combine(location,"..","scripts",name)}) {
            if(!File.Exists(candidate))continue;
            string directory=Path.GetDirectoryName(candidate);
            foreach(string module in new[]{"install.ps1","uninstall.ps1","setup-common.ps1","setup-native.ps1","setup-files.ps1","setup-service.ps1","setup-recovery.ps1"})
                if(!File.Exists(Path.Combine(directory,module)))throw new FileNotFoundException("Extract the complete release; setup module is missing: "+module);
            return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Setup script is missing. Extract the full release ZIP or run scripts/build.ps1 from the source checkout.");
    }
}
}
