using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.ExceptionServices;

namespace LcPlayerMarket;

internal sealed record NavigationBundleProgress(int VerifiedFiles, int TotalFiles, bool Complete);

// The expected dependency set is independent of the files found in a packaging directory.
// A future backend update must review and update this pinned manifest together with its payload.
internal static class NavigationBundleContract
{
    internal const string ManifestSha256 = "42CDAAE522C19D9960D2F343E98C04E078DD1D2A23E1BD8E18002D14E536EA2D";
    internal const int ExpectedFileCount = 3223;
    internal const int MaximumHashConcurrency = 8;
    internal const int HashReadBufferBytes = 64 * 1024;

    internal static int Verify(string executable, CancellationToken cancellationToken = default,
        Action<NavigationBundleProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        executable = Path.GetFullPath(executable);
        string directory = Path.GetDirectoryName(executable)!;
        string manifest = Path.Combine(directory, "bundle-manifest.json");
        Need(File.Exists(manifest), "NAV_BUNDLE_MANIFEST_MISSING", manifest);
        Need(new FileInfo(manifest).Length <= 1024 * 1024, "NAV_BUNDLE_MANIFEST_SIZE", manifest);
        byte[] bytes = File.ReadAllBytes(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        Need(Convert.ToHexString(SHA256.HashData(bytes)) == ManifestSha256,
            "NAV_BUNDLE_MANIFEST_MISMATCH", "導航依賴清單與本版核准內容不同。");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Need(root.GetProperty("programId").GetString() == "LC.PlayerMarket.Navigation.V1"
            && root.GetProperty("protocol").GetInt32() == 1, "NAV_BUNDLE_PROTOCOL", manifest);
        var entries = new List<(string Relative, string Path, string Hash)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = item.GetProperty("path").GetString() ?? "";
            string path = Path.GetFullPath(Path.Combine(directory, relative));
            Need(!Path.IsPathRooted(relative) && path.StartsWith(directory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && seen.Add(path), "NAV_BUNDLE_PATH", relative);
            entries.Add((relative, path, item.GetProperty("sha256").GetString() ?? ""));
        }
        Need(entries.Count == ExpectedFileCount, "NAV_BUNDLE_FILE_COUNT", entries.Count.ToString());
        foreach (var required in new[] { executable, Path.Combine(directory, "_internal", "base_library.zip"),
            Path.Combine(directory, "_internal", "python314.dll") })
            Need(seen.Contains(required), "NAV_BUNDLE_REQUIRED_ENTRY", required);
        VerifyFiles(entries, cancellationToken, progress: progress);
        return entries.Count;
    }

    // Every admitted entry is hashed on each invocation. The test opener only
    // supplies offline streams; production opens the validated pinned paths.
    internal static void VerifyFiles(IReadOnlyList<(string Relative, string Path, string Hash)> entries,
        CancellationToken cancellationToken = default, Func<string, Stream>? openStream = null,
        Action<NavigationBundleProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new Exception?[entries.Count];
        var progressGate = new object();
        int verified = 0;
        ReportProgress(progress, new(0, entries.Count, false));
        Parallel.For(0, entries.Count, new ParallelOptions
        { MaxDegreeOfParallelism = MaximumHashConcurrency, CancellationToken = cancellationToken }, index =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            try
            {
                using var stream = openStream is null
                    ? new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        HashReadBufferBytes, FileOptions.SequentialScan) : openStream(entry.Path);
                Need(HashStream(stream, cancellationToken).Equals(entry.Hash, StringComparison.OrdinalIgnoreCase),
                    "NAV_BUNDLE_FILE_HASH", entry.Relative);
                cancellationToken.ThrowIfCancellationRequested();
                lock (progressGate)
                {
                    verified++;
                    // Keep UI and LOG traffic bounded even for the full pinned bundle.
                    if (verified % 64 == 0 || verified == entries.Count)
                        ReportProgress(progress, new(verified, entries.Count, false));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (openStream is null && error is (FileNotFoundException or DirectoryNotFoundException))
            { failures[index] = new InvalidDataException("NAV_BUNDLE_FILE_MISSING：" + entry.Relative + "；請使用完整發布資料夾。", error); }
            catch (Exception error) { failures[index] = error; }
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (verified != entries.Count) ReportProgress(progress, new(verified, entries.Count, false));
        // Parallel's aggregate must not hide the original code or relative path.
        // Preserve manifest order when more than one file fails verification.
        foreach (var failure in failures)
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(progress, new(verified, entries.Count, true));
    }

    private static void ReportProgress(Action<NavigationBundleProgress>? progress, NavigationBundleProgress value)
    {
        try { progress?.Invoke(value); }
        catch { /* Presentation cannot authorize startup or weaken bundle verification. */ }
    }

    internal static string HashStream(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashReadBufferBytes];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Need(bool okay, string code, string detail)
    {
        if (!okay) throw new InvalidDataException(code + "：" + detail + "；請使用完整發布資料夾。");
    }
}
