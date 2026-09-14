using System;
using System.IO.Pipes;
using System.Threading;
namespace VencordAutoUpdate {
internal static class WorkPipeTests {
    internal static void Run() {
        TestRunner.Run("native user pipe rejects raw retired aliases and EOF from peer",delegate {
            foreach(int value in new[]{20,64,72,129,255,-1}) {
                string key="raw-pipe-fixture-"+Guid.NewGuid().ToString("N"),name=InstanceControl.Name("work",key).Replace("Global\\","");
                using(var done=new ManualResetEvent(false))using(var pipe=new NamedPipeServerStream(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)) {
                    Exception failure=null;
                    pipe.BeginWaitForConnection(delegate(IAsyncResult connection){try{pipe.EndWaitForConnection(connection);if(value>=0)pipe.WriteByte((byte)value);pipe.Dispose();}catch(Exception e){failure=e;}finally{done.Set();}},null);
                    TestRunner.Assert((int)WorkRequests.Request(key)==22,"raw invalid peer value accepted: "+value);
                    TestRunner.Assert(done.WaitOne(5000) && failure==null,"raw fixture peer did not drain");
                }
            }
        });
        TestRunner.Run("native user pipe relays exact masks and normalizes full invalid values",delegate {
            int[] values={0,21,22,65,66,67,68,69,70,71,20,64,72,321,-1};
            int[] wanted={0,21,22,65,66,67,68,69,70,71,22,22,22,22,22};
            for(int i=0;i<values.Length;i++) {
                int value=values[i];string key="pipe-fixture-"+Guid.NewGuid().ToString("N");
                using(var pipe=new WorkRequests(key,reply=>reply((WorkerResult)value)))
                    TestRunner.Assert((int)WorkRequests.Request(key)==wanted[i],"wrong real pipe outcome "+value);
            }
        });
    }
}
}
