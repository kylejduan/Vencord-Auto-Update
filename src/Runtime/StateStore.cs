using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
namespace VencordAutoUpdate {
internal sealed class PersistentState {
    public int Schema { get; set; }
    public bool Paused { get; set; }
    public bool Automatic { get; set; }
    public long LastRestart { get; set; }
    public List<GenerationState> Generations { get; set; }
    public PersistentState() { Schema=1; Automatic=true; Generations=new List<GenerationState>(); }
}
internal sealed class StateStore {
    readonly string directory, path;
    internal PersistentState Value { get; private set; }
    internal bool Writable { get; private set; }
    internal string Error { get; private set; }
    internal StateStore(string root) {
        directory=root; path=Path.Combine(root,"state.json"); Value=new PersistentState();
        try {
            SafePath.Local(path);
            if (File.Exists(path)) {
                string json=new UTF8Encoding(false,true).GetString(SafePath.ReadBounded(path,1024*1024));
                var shape=StrictJson.Parse(json) as Dictionary<string,object>;
                if(shape==null || shape.Count!=5 || !shape.ContainsKey("Schema") || !shape.ContainsKey("Paused") || !shape.ContainsKey("Automatic") || !shape.ContainsKey("LastRestart") || !shape.ContainsKey("Generations")) throw new IOException("Incomplete state.");
                RequireInteger(shape,"Schema");
                RequireBoolean(shape,"Paused");
                RequireBoolean(shape,"Automatic");
                RequireInteger(shape,"LastRestart");
                var generations=shape["Generations"] as System.Collections.Generic.List<object>;
                if(generations==null) throw new IOException("Invalid generations.");
                foreach(var item in generations) {
                    var fields=item as Dictionary<string,object>;
                    if(fields==null || fields.Count!=9) throw new IOException("Incomplete generation.");
                    foreach(string name in new [] {"Key","Cohort","Eligible","Deferred","Reserved","VerificationFailed","Attempts","LastAttempt","VerifyAfter"}) if(!fields.ContainsKey(name)) throw new IOException("Incomplete generation.");
                    StrictJson.String(fields,"Key");
                    StrictJson.String(fields,"Cohort");
                    foreach(string name in new [] {"Eligible","Deferred","Reserved","VerificationFailed"}) RequireBoolean(fields,name);
                    foreach(string name in new [] {"Attempts","LastAttempt","VerifyAfter"}) RequireInteger(fields,name);
                }
                Value=new JavaScriptSerializer().Deserialize<PersistentState>(json);
                Validate(Value);
            }
            Writable=true;
        } catch(Exception e) { Value=new PersistentState(); Writable=false; Error="State cannot be loaded; preserve state.json and inspect logs. " + e.GetType().Name; }
    }
    static void RequireBoolean(Dictionary<string,object> fields,string name) {
        if(!(fields[name] is bool)) throw new IOException("Invalid state Boolean: "+name);
    }
    static void RequireInteger(Dictionary<string,object> fields,string name) {
        object value=fields[name];
        if(!(value is decimal) || Decimal.Truncate((decimal)value)!=(decimal)value) throw new IOException("Invalid state integer: "+name);
    }
    static void Validate(PersistentState state) {
        if(state==null || state.Schema!=1 || state.Generations==null || state.Generations.Count>512 || state.LastRestart<0 || state.LastRestart>DateTime.MaxValue.Ticks) throw new IOException("Invalid state schema.");
        HashSet<string> keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var g in state.Generations) {
            if(g==null || String.IsNullOrEmpty(g.Key) || g.Cohort==null || !keys.Add(g.Key) || g.Attempts<0 || g.Attempts>3 || g.LastAttempt<0 || g.LastAttempt>DateTime.MaxValue.Ticks || g.VerifyAfter<0 || g.VerifyAfter>DateTime.MaxValue.Ticks) throw new IOException("Invalid generation state.");
        }
    }
    internal GenerationState Find(string key) { return Value.Generations.Find(g=>String.Equals(g.Key,key,StringComparison.OrdinalIgnoreCase)); }
    internal bool Add(GenerationState state) {
        if(Value.Generations.Count>=512) { Writable=false;Error="Generation history is full; automatic work is disabled. Preserve state for review.";return false; }
        Value.Generations.Add(state); return Save();
    }
    internal bool Save() {
        if(!Writable) return false;
        string temporary=Path.Combine(directory,"state."+Guid.NewGuid().ToString("N")+".tmp");
        try {
            Validate(Value); SafePath.Local(directory); Directory.CreateDirectory(directory);
            SafePath.WriteNew(temporary,Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(Value)));
            SafePath.Local(path);
            if(File.Exists(path)) File.Replace(temporary,path,null);
            else NativeFiles.MoveNew(temporary,path);
            return true;
        } catch(Exception e) { Writable=false;Error="State cannot be saved; automatic work disabled. "+e.GetType().Name;return false; }
    }
}
}
