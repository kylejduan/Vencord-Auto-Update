using System;
using System.IO;

namespace VencordAutoUpdate {
internal sealed class RepairResult {
    internal bool Success { get; private set; }
    internal bool Changed { get; private set; }
    internal string Reason { get; private set; }
    internal Installation Installation { get; private set; }
    internal RepairResult(bool success, bool changed, string reason, Installation installation) { Success = success; Changed = changed; Reason = reason; Installation = installation; }
}
internal static class RepairTransaction {
    internal const string JournalName = ".vencord-auto-update.transaction.json";
    // Checkpoints only observe transaction boundaries. Production callers normally pass null.
    internal static RepairResult Repair(Installation candidate, ValidatedLoader loader, Func<bool> quiescent, Action<string> checkpoint) {
        bool changed = false;
        try {
            if (candidate == null) throw new IOException("A candidate is required.");
            Installation fresh = Installation.Inspect(candidate.Root,candidate.Executable,candidate.Dist);
            if (!Equivalent(candidate,fresh)) throw new IOException("Candidate changed; observe and inspect again.");
            if (fresh.Kind == InstallationKind.Healthy) return new RepairResult(true,false,"Already healthy.",fresh);
            if (fresh.Kind != InstallationKind.Stock && fresh.Kind != InstallationKind.Interrupted) throw new IOException("Repair refused: " + fresh.Reason);
            if (loader == null || !SafePath.Same(loader.Dist,candidate.Dist)) throw new IOException("A validated loader for the expected Vencord dist is required.");
            loader.Validate();
            string app = Path.Combine(candidate.Resources,"app.asar"), original = Path.Combine(candidate.Resources,"_app.asar"), journalPath = Path.Combine(candidate.Resources,JournalName);
            SafePath.Local(journalPath);
            RepairJournal journal;
            if (File.Exists(journalPath)) journal = RepairJournal.Read(journalPath,candidate,loader);
            else {
                journal = RepairJournal.Create(candidate,loader);
                Gate(quiescent); VerifyState(candidate,loader,false);
                SafePath.WriteNew(journalPath,journal.Bytes(),delegate { changed = true; Checkpoint(checkpoint,"JournalCreated"); }); Checkpoint(checkpoint,"JournalWritten");
            }
            string temporary = Path.Combine(candidate.Resources,journal.Temporary); SafePath.Local(temporary);
            if (!File.Exists(temporary)) {
                Gate(quiescent); VerifyState(candidate,loader,false); VerifyJournal(journalPath,candidate,loader,journal);
                SafePath.WriteNew(temporary,loader.Bytes,delegate { changed = true; Checkpoint(checkpoint,"LoaderCreated"); }); Checkpoint(checkpoint,"LoaderWritten");
            }
            VerifyTemporary(temporary,loader);
            fresh = VerifyState(candidate,loader,false);
            if (fresh.Kind == InstallationKind.Stock) {
                Checkpoint(checkpoint,"BeforeOriginalMove"); Gate(quiescent);
                fresh = VerifyState(candidate,loader,false); VerifyJournal(journalPath,candidate,loader,journal); VerifyTemporary(temporary,loader);
                if (fresh.Kind != InstallationKind.Stock) throw new IOException("Source changed before original rename.");
                NativeFiles.MoveNew(app,original); changed = true; Checkpoint(checkpoint,"OriginalMoved");
            }
            Checkpoint(checkpoint,"BeforeLoaderMove"); Gate(quiescent);
            fresh = VerifyState(candidate,loader,false); VerifyJournal(journalPath,candidate,loader,journal); VerifyTemporary(temporary,loader);
            if (fresh.Kind != InstallationKind.Interrupted) throw new IOException("Destination changed before loader rename.");
            NativeFiles.MoveNew(temporary,app); changed = true; Checkpoint(checkpoint,"LoaderMoved");
            fresh = VerifyState(candidate,loader,true);
            Gate(quiescent); VerifyState(candidate,loader,true); VerifyJournal(journalPath,candidate,loader,journal);
            SafePath.Local(journalPath); File.Delete(journalPath);
            return new RepairResult(true,true,"Loader and preserved original verified.",fresh);
        } catch (Exception e) {
            if (!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is ArgumentException) && !(e is NotSupportedException) && !(e is InvalidOperationException)) throw;
            // No automatic rollback: preserved original, staging file and journal remain evidence.
            return new RepairResult(false,changed,e.Message,candidate == null ? null : Installation.Inspect(candidate.Root,candidate.Executable,candidate.Dist));
        }
    }
    static bool Equivalent(Installation a, Installation b) {
        return a.Kind == b.Kind && a.Resources == b.Resources && a.MetadataSignature == b.MetadataSignature && a.EnvironmentSignature == b.EnvironmentSignature && a.OriginalHash == b.OriginalHash && a.LoaderHash == b.LoaderHash;
    }
    static Installation VerifyState(Installation candidate, ValidatedLoader loader, bool completed) {
        loader.Validate(); Installation fresh = Installation.Inspect(candidate.Root,candidate.Executable,candidate.Dist);
        if (fresh.Resources != candidate.Resources || fresh.OriginalHash != candidate.OriginalHash || fresh.EnvironmentSignature != candidate.EnvironmentSignature) throw new IOException("Generation or original hash changed; all evidence retained.");
        if (completed) {
            if (fresh.Kind != InstallationKind.Healthy || fresh.LoaderHash != loader.Hash) throw new IOException("Post-repair verification failed; evidence retained.");
        } else if (fresh.Kind != InstallationKind.Stock && fresh.Kind != InstallationKind.Interrupted) throw new IOException("Concurrent or unsupported archive state; evidence retained.");
        return fresh;
    }
    static void VerifyJournal(string path, Installation candidate, ValidatedLoader loader, RepairJournal journal) {
        if (RepairJournal.Read(path,candidate,loader).Temporary != journal.Temporary) throw new IOException("Journal changed; evidence retained.");
    }
    static void VerifyTemporary(string path, ValidatedLoader loader) {
        SafePath.RequireFile(path); if (SafePath.Hash(path) != loader.Hash) throw new IOException("Staging loader changed; evidence retained.");
    }
    static void Gate(Func<bool> quiescent) { if (quiescent == null || !quiescent()) throw new IOException("Discord or its updater is not quiescent; deferred."); }
    static void Checkpoint(Action<string> checkpoint,string point) { if (checkpoint != null) checkpoint(point); }
}
}
