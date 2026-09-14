using System;
namespace VencordAutoUpdate {
internal enum Decision { Wait, Deferred, Countdown, Repair, Exhausted }
internal sealed class GenerationState {
    public string Key { get; set; }
    public string Cohort { get; set; }
    public bool Eligible { get; set; }
    public bool Deferred { get; set; }
    public bool Reserved { get; set; }
    public bool VerificationFailed { get; set; }
    public int Attempts { get; set; }
    public long LastAttempt { get; set; }
    public long VerifyAfter { get; set; }
}
internal static class RestartPolicy {
    internal static GenerationState First(string key, string cohort, DateTime now, DateTime? oldest, bool interactive) {
        bool eligible=interactive && oldest.HasValue && oldest.Value <= now && now-oldest.Value <= TimeSpan.FromMinutes(3);
        return new GenerationState { Key=key,Cohort=cohort,Eligible=eligible,Deferred=oldest.HasValue && !eligible };
    }
    internal static Decision Evaluate(GenerationState state, DateTime now, DateTime settledSince,
        bool running, string cohort, bool interactive, bool automatic, long lastRestart) {
        if (state.Attempts>=3 || state.VerificationFailed) return Decision.Exhausted;
        if (now-settledSince < TimeSpan.FromSeconds(10) ||
            (state.LastAttempt!=0 && now.Ticks-state.LastAttempt < TimeSpan.FromSeconds(60).Ticks)) return Decision.Wait;
        if (!running) return Decision.Repair;
        if (!interactive || !automatic || !state.Eligible || state.Deferred || state.Reserved || state.Cohort!=cohort ||
            (lastRestart!=0 && now.Ticks-lastRestart < TimeSpan.FromMinutes(10).Ticks)) return Decision.Deferred;
        return Decision.Countdown;
    }
}
}
