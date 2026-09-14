using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
namespace VencordAutoUpdate {
// Connections carry no commands, paths or policy: connect requests one reconciliation,
// one response byte acknowledges its actual batch outcome. Only this SID has access.
internal sealed class WorkRequests : IDisposable {
    readonly object gate=new object();
    readonly string name;
    readonly Action<Action<WorkerResult>> received;
    readonly List<NamedPipeServerStream> clients=new List<NamedPipeServerStream>();
    NamedPipeServerStream listener;
    bool disposed;
    internal WorkRequests(string data,Action<Action<WorkerResult>> dispatch) {
        name=InstanceControl.Name("work",data).Replace("Global\\","");received=dispatch;
        Listen();
    }
    void Listen() {
        PipeSecurity acl=new PipeSecurity();
        using(var user=WindowsIdentity.GetCurrent())
            acl.AddAccessRule(new PipeAccessRule(user.User,PipeAccessRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        listener=new NamedPipeServerStream(name,PipeDirection.Out,NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,1,acl);
        listener.BeginWaitForConnection(Connected,listener);
    }
    void Connected(IAsyncResult result) {
        var pipe=(NamedPipeServerStream)result.AsyncState;
        lock(gate) {
            if(disposed) return;
            try {pipe.EndWaitForConnection(result);}
            catch(IOException) {pipe.Dispose();Listen();return;}
            clients.Add(pipe);Listen();
        }
        try {received(value=>Reply(pipe,value));}
        catch {Reply(pipe,WorkerResult.Retry);}
    }
    void Reply(NamedPipeServerStream pipe,WorkerResult result) {
        lock(gate) {
            if(!clients.Remove(pipe)) return;
            try {pipe.WriteByte((byte)WorkerProtocol.Normalize((int)result));}
            catch(IOException) { }
            finally {pipe.Dispose();}
        }
    }
    internal static WorkerResult Request(string data) {
        string name=InstanceControl.Name("work",data).Replace("Global\\","");
        try {
            using(var client=new NamedPipeClientStream(".",name,PipeDirection.In,PipeOptions.None)) {
                // A primary may still be publishing its IPC. No false Done on that race.
                client.Connect(5000);
                int result=client.ReadByte();
                return (WorkerResult)WorkerProtocol.Normalize(result);
            }
        }catch(IOException) {return WorkerResult.Retry;}
        catch(TimeoutException) {return WorkerResult.Retry;}
    }
    public void Dispose() {
        lock(gate) {
            disposed=true;if(listener!=null)listener.Dispose();
            foreach(var client in clients)client.Dispose();clients.Clear();
        }
    }
}
}
