using System;
using System.IO;
using System.Text;
namespace VencordAutoUpdate {
internal sealed class RotatingLog {
    readonly string root;
    internal RotatingLog(string data) {root=Path.Combine(data,"logs");}
    internal string DirectoryPath {get {return root;}}
    // Only fixed lifecycle categories and enum/type names belong in logs. No installation paths,
    // account names, Discord profile contents, or exception messages are persisted here.
    internal void Write(string category) {
        try {
            SafePath.Local(root);Directory.CreateDirectory(root);
            string path=Path.Combine(root,"helper.log");SafePath.Local(path);
            if(File.Exists(path) && new FileInfo(path).Length>256*1024) {
                string previous=Path.Combine(root,"helper.previous.log");SafePath.Local(previous);
                if(File.Exists(previous))File.Delete(previous);File.Move(path,previous);
            }
            File.AppendAllText(path,DateTime.UtcNow.ToString("o")+" "+category+Environment.NewLine,Encoding.UTF8);
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
}
