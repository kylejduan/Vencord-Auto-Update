using System;
using System.IO;

namespace VencordAutoUpdate {
internal sealed class ValidatedLoader {
    readonly byte[] bytes;
    internal byte[] Bytes { get { return (byte[])bytes.Clone(); } }
    internal string Hash { get; private set; }
    internal string Dist { get; private set; }
    internal ValidatedLoader(byte[] value, string dist) {
        if (value == null || value.Length > 65536) throw new IOException("Loader exceeds cache bounds.");
        bytes = (byte[])value.Clone(); Dist = SafePath.Local(dist);
        using (AsarArchive archive = new AsarArchive(bytes)) if (!archive.IsLoader(Dist)) throw new IOException("Unrecognized loader.");
        Hash = SafePath.Hash(bytes);
    }
    internal void Validate() {
        using (AsarArchive archive = new AsarArchive(bytes)) if (!archive.IsLoader(Dist)) throw new IOException("Cached loader or Vencord assets no longer valid.");
        if (SafePath.Hash(bytes) != Hash) throw new IOException("Loader hash mismatch.");
    }
}
internal sealed class LoaderCache {
    readonly string root;
    internal LoaderCache(string cacheRoot) { root = SafePath.Local(cacheRoot); }
    internal ValidatedLoader Capture(Installation installation) {
        Installation fresh = Installation.InspectVersion(installation.Root,installation.Executable,installation.Dist,installation.Version);
        if (fresh.Kind != InstallationKind.Healthy || fresh.MetadataSignature != installation.MetadataSignature || fresh.LoaderHash != installation.LoaderHash || fresh.OriginalHash != installation.OriginalHash) throw new IOException("A freshly verified healthy installation is required to cache a loader.");
        string path = Path.Combine(fresh.Resources,"app.asar"); SafePath.RequireFile(path);
        if (new FileInfo(path).Length > 65536) throw new IOException("Loader too large.");
        ValidatedLoader loader = new ValidatedLoader(SafePath.ReadBounded(path,65536),fresh.Dist);
        if (loader.Hash != fresh.LoaderHash) throw new IOException("Loader changed while caching.");
        SafePath.Local(root); Directory.CreateDirectory(root); SafePath.Local(root);
        string destination = Path.Combine(root,"loader-" + loader.Hash + ".asar"); SafePath.Local(destination);
        if (File.Exists(destination)) {
            if (SafePath.Hash(destination) != loader.Hash) throw new IOException("Existing cache file is corrupt; retained for inspection.");
            return loader;
        }
        string temporary = Path.Combine(root,"cache-" + Guid.NewGuid().ToString("N") + ".tmp");
        SafePath.WriteNew(temporary,loader.Bytes);
        // A racing creator must not be replaced; retain staging evidence on failure.
        NativeFiles.MoveNew(temporary,destination);
        if (SafePath.Hash(destination) != loader.Hash) throw new IOException("Cache verification failed.");
        return loader;
    }
    internal ValidatedLoader Load(string dist, out string reason) {
        reason = "No validated cached loader exists; run the official Vencord installer once.";
        string[] files;
        try {
            SafePath.Local(root);
            if (!Directory.Exists(root)) return null;
            files = Directory.GetFiles(root, "loader-*.asar", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);
        } catch (Exception e) {
            if (!IsValidationFailure(e)) throw;
            reason = e.Message;
            return null;
        }
        foreach (string file in files) {
            try {
                SafePath.RequireFile(file);
                if (new FileInfo(file).Length > 65536) throw new IOException("Cached loader too large.");
                ValidatedLoader loader = new ValidatedLoader(SafePath.ReadBounded(file, 65536), dist);
                if (Path.GetFileName(file) != "loader-" + loader.Hash + ".asar") throw new IOException("Cached loader hash does not match its immutable filename.");
                reason = null;
                return loader;
            } catch (Exception e) {
                if (!IsValidationFailure(e)) throw;
                // Retain rejected entries as evidence, but allow another immutable candidate.
                reason = "No cached loader validated. Last rejected entry: " + e.Message;
            }
        }
        return null;
    }
    static bool IsValidationFailure(Exception error) {
        return error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException;
    }
}
}
