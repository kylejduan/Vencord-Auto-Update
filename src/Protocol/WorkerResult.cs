namespace VencordAutoUpdate {
internal enum WorkerResult { Done=0, Refused=21, Retry=22 }
// Pure scheduling protocol. No paths, policy or repair authority cross this boundary.
internal static class WorkerProtocol {
    internal static int Normalize(int result) {return result==0 || result==21 || result==22 || (result>=65 && result<=71)?result:22;}
    internal static int DeferredMask(int result) {return result>=65 && result<=71?result-64:0;}
    internal static WorkerResult Deferred(string executable) {
        switch(executable.ToLowerInvariant()) {
            case "discord.exe":return (WorkerResult)65;
            case "discordptb.exe":return (WorkerResult)66;
            case "discordcanary.exe":return (WorkerResult)68;
            default:return WorkerResult.Retry;
        }
    }
}
}
