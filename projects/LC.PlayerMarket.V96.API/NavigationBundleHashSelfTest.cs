using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LcPlayerMarket;

/// <summary>Real bounded hash implementation with generated files/streams only.</summary>
internal static class NavigationBundleHashSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Action action)
        {
            try { await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(10)); checks.Add(new(name, true, "純離線假檔完整SHA／取消回歸，沒有LC、子程序或輸入。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("navigation_hash_all_entries_once_with_at_most_eight_owned_streams", () =>
        {
            var f = new Fixture(); f.SlowRead = true;
            NavigationBundleContract.VerifyFiles(f.Entries, openStream: f.Open);
            Need(f.Opened.Count == f.Entries.Count && f.Entries.All(e => f.Opened.GetValueOrDefault(e.Path) == 1)
                && f.Active == 0 && f.Disposed == f.Entries.Count && f.Maximum is >= 1 and <= 8,
                "並行完整驗證漏檔、重複開檔、超過8條stream或未釋放檔案。");
            // Verify the production file opener too, against the independent
            // SHA256.HashData values generated before the implementation runs.
            NavigationBundleContract.VerifyFiles(f.Entries);
        });
        await Check("navigation_hash_mismatch_keeps_typed_code_relative_path_and_checks_rest", () =>
        {
            var f = new Fixture(); var entries = f.Entries.ToArray();
            entries[3] = (entries[3].Relative, entries[3].Path, new string('0', 64));
            entries[9] = (entries[9].Relative, entries[9].Path, new string('1', 64));
            InvalidDataException? rejected = null;
            try { NavigationBundleContract.VerifyFiles(entries, openStream: f.Open); }
            catch (InvalidDataException error) { rejected = error; }
            Need(rejected?.Message.Contains("NAV_BUNDLE_FILE_HASH", StringComparison.Ordinal) == true
                && rejected.Message.Contains(entries[3].Relative, StringComparison.Ordinal)
                && f.Opened.Count == entries.Length && f.Disposed == entries.Length && f.Active == 0,
                "SHA失配被aggregate遮住／錯用絕對檔名或漏驗後續檔案。");
        });
        await Check("navigation_hash_precancelled_never_opens_any_file", () =>
        {
            var f = new Fixture(); using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { NavigationBundleContract.VerifyFiles(f.Entries, stop.Token, f.Open); }
            catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { cancelled = true; }
            Need(cancelled && f.Opened.IsEmpty && f.Active == 0 && f.Disposed == 0, "預先取消仍開檔或丟失取消token。");
        });
        await Check("navigation_hash_mid_cancel_closes_owned_streams_without_finishing_remaining_files", () =>
        {
            var f = new Fixture(); using var stop = new CancellationTokenSource(); int reads = 0; bool cancelled = false;
            f.OnRead = _ => { if (Interlocked.Increment(ref reads) == 3) stop.Cancel(); };
            try { NavigationBundleContract.VerifyFiles(f.Entries, stop.Token, f.Open); }
            catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { cancelled = true; }
            Need(cancelled && f.Opened.Count is > 0 && f.Opened.Count < f.Entries.Count && f.Active == 0
                && f.Disposed == f.Opened.Count && f.Maximum <= 8, "途中取消仍等全部檔案或遺留開啟stream。");
        });
        await Check("navigation_hash_large_stream_cancels_at_first_64kb_chunk", () =>
        {
            using var stop = new CancellationTokenSource(); int reads = 0, largest = 0, bytesRead = 0;
            using var stream = new ObservedStream(new byte[4 * 65536 + 17], amount =>
            { reads++; bytesRead += amount; stop.Cancel(); }, () => { }, requested => largest = Math.Max(largest, requested));
            bool cancelled = false;
            try { NavigationBundleContract.HashStream(stream, stop.Token); }
            catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { cancelled = true; }
            Need(cancelled && reads == 1 && bytesRead == 65536 && largest == 65536,
                "大檔取消未在64KB邊界停止，仍同步讀完整大檔。");
        });
        await Check("navigation_hash_empty_and_multiple_chunks_equal_standard_sha256", () =>
        {
            foreach (int length in new[] { 0, 17, 65536, 2 * 65536 + 19 })
            {
                byte[] bytes = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
                using var stream = new MemoryStream(bytes, writable: false);
                Need(NavigationBundleContract.HashStream(stream) == Convert.ToHexString(SHA256.HashData(bytes)),
                    "IncrementalHash與原SHA256結果不同，長度=" + length);
            }
        });
        foreach (bool missingDirectory in new[] { false, true })
            await Check("navigation_hash_production_missing_" + (missingDirectory ? "directory" : "file") + "_keeps_code_path_and_checks_rest", () =>
            {
                var f = new Fixture(); var entries = f.Entries.ToArray();
                string relative = entries[3].Relative;
                if (missingDirectory)
                {
                    relative = "not-created/" + relative;
                    entries[3] = (relative, Path.Combine(f.Root, "not-created", entries[3].Relative), entries[3].Hash);
                }
                else File.Delete(entries[3].Path);
                var notices = new ConcurrentQueue<NavigationBundleProgress>();
                InvalidDataException? rejected = null;
                try { NavigationBundleContract.VerifyFiles(entries, progress: notices.Enqueue); }
                catch (InvalidDataException error) { rejected = error; }
                Need(rejected?.Message.Contains("NAV_BUNDLE_FILE_MISSING", StringComparison.Ordinal) == true
                    && rejected.Message.Contains(relative, StringComparison.Ordinal)
                    && notices.Last().VerifiedFiles == entries.Length - 1 && !notices.Any(n => n.Complete),
                    "缺檔被aggregate／原生FileOpen錯誤遮住，或漏驗其餘完整檔案／錯報全部成功。");
            });
        await Check("navigation_hash_virtual_opener_needs_no_physical_file_or_exists_probe", () =>
        {
            var f = new Fixture(); var virtualFiles = f.Entries.ToDictionary(
                entry => Path.Combine(f.Root, "virtual-not-created", entry.Relative), entry => File.ReadAllBytes(entry.Path));
            var entries = f.Entries.Select(entry => (entry.Relative, Path: Path.Combine(f.Root, "virtual-not-created", entry.Relative), entry.Hash)).ToArray();
            int opened = 0; var notices = new ConcurrentQueue<NavigationBundleProgress>();
            NavigationBundleContract.VerifyFiles(entries, openStream: path =>
            { Interlocked.Increment(ref opened); return new MemoryStream(virtualFiles[path], writable: false); }, progress: notices.Enqueue);
            Need(opened == entries.Length && notices.Last() is { Complete: true } final && final.VerifiedFiles == entries.Length,
                "fake opener仍要求實體檔案，或進度未以完整實際核對結果結束。");
        });
        await Check("navigation_hash_progress_is_ordered_and_callback_failure_cannot_skip_verification", () =>
        {
            var f = new Fixture(); var notices = new ConcurrentQueue<NavigationBundleProgress>();
            NavigationBundleContract.VerifyFiles(f.Entries, openStream: f.Open, progress: value =>
            { notices.Enqueue(value); throw new IOException("offline presentation failure"); });
            var values = notices.ToArray();
            Need(values.Length >= 3 && values[0].VerifiedFiles == 0 && !values[0].Complete
                && values[^1].Complete && values[^1].VerifiedFiles == f.Entries.Count
                && values.All(value => value.TotalFiles == f.Entries.Count)
                && values.Zip(values.Skip(1), (previous, next) => previous.VerifiedFiles <= next.VerifiedFiles).All(ordered => ordered)
                && f.Opened.Count == f.Entries.Count && f.Disposed == f.Entries.Count && f.Active == 0,
                "進度倒序／遺失實際完成量，或presentation例外中斷完整核對與stream釋放。");
        });
        await Check("navigation_hash_cancel_after_last_file_never_reports_ready", () =>
        {
            var f = new Fixture(); using var stop = new CancellationTokenSource();
            var notices = new ConcurrentQueue<NavigationBundleProgress>(); bool cancelled = false;
            try
            {
                NavigationBundleContract.VerifyFiles(f.Entries, stop.Token, f.Open, progress: value =>
                { notices.Enqueue(value); if (value.VerifiedFiles == f.Entries.Count && !value.Complete) stop.Cancel(); });
            }
            catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { cancelled = true; }
            Need(cancelled && !notices.Any(value => value.Complete) && f.Active == 0 && f.Disposed == f.Entries.Count,
                "完成前取消仍發出全部核對成功，或未釋放全部stream。");
        });
        await Check("navigation_hash_precancelled_never_reports_progress_or_ready", () =>
        {
            var f = new Fixture(); using var stop = new CancellationTokenSource(); stop.Cancel();
            var notices = new ConcurrentQueue<NavigationBundleProgress>(); bool cancelled = false;
            try { NavigationBundleContract.VerifyFiles(f.Entries, stop.Token, f.Open, notices.Enqueue); }
            catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { cancelled = true; }
            Need(cancelled && notices.IsEmpty && f.Opened.IsEmpty, "预先取消仍報初始化成功／進度或開檔。");
        });
        await Check("navigation_hash_precancelled_bundle_returns_cancel_before_manifest_access", () =>
        {
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { NavigationBundleContract.Verify(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"), stop.Token); }
            catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { cancelled = true; }
            Need(cancelled, "初始化取消仍先讀bundle或回傳manifest缺少。");
        });
        await Check("navigation_hash_manifest_pin_still_rejects_a_generated_manifest", () =>
        {
            var f = new Fixture(); string executable = Path.Combine(f.Root, "MarketNavigationBackend.exe");
            File.WriteAllBytes(executable, [0]); File.WriteAllText(Path.Combine(f.Root, "bundle-manifest.json"),
                "{\"programId\":\"LC.PlayerMarket.Navigation.V1\",\"protocol\":1,\"files\":[]}");
            InvalidDataException? error = null;
            try { NavigationBundleContract.Verify(executable); } catch (InvalidDataException failure) { error = failure; }
            Need(error?.Message.Contains("NAV_BUNDLE_MANIFEST_MISMATCH", StringComparison.Ordinal) == true,
                "並行改動取消了manifest固定SHA，或先信任生成的清單。");
        });
        return checks.ToArray();
    }

    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

    private sealed class Fixture
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "LC-V54-navigation-hash-" + Guid.NewGuid().ToString("N"));
        internal readonly List<(string Relative, string Path, string Hash)> Entries = [];
        internal readonly ConcurrentDictionary<string, int> Opened = new(StringComparer.Ordinal);
        internal int Active, Maximum, Disposed;
        internal bool SlowRead;
        internal Action<int>? OnRead;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            for (int index = 0; index < 32; index++)
            {
                string relative = "fake-file-" + index.ToString("D2") + ".dat", path = Path.Combine(Root, relative);
                byte[] bytes = Enumerable.Range(0, 2048 + index).Select(i => (byte)((i + index) % 251)).ToArray();
                File.WriteAllBytes(path, bytes); Entries.Add((relative, path, Convert.ToHexString(SHA256.HashData(bytes))));
            }
        }
        internal Stream Open(string path)
        {
            Opened.AddOrUpdate(path, 1, (_, count) => count + 1);
            int active = Interlocked.Increment(ref Active);
            while (true)
            {
                int previous = Volatile.Read(ref Maximum); if (previous >= active) break;
                if (Interlocked.CompareExchange(ref Maximum, active, previous) == previous) break;
            }
            return new ObservedStream(File.ReadAllBytes(path), amount =>
            { if (SlowRead) Thread.Sleep(2); OnRead?.Invoke(amount); }, () =>
            { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Disposed); });
        }
    }

    private sealed class ObservedStream(byte[] bytes, Action<int> afterRead, Action afterDispose,
        Action<int>? request = null) : MemoryStream(bytes, writable: false)
    {
        private int closed;
        public override int Read(byte[] buffer, int offset, int count)
        {
            request?.Invoke(count); int read = base.Read(buffer, offset, count);
            if (read > 0) afterRead(read); return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) afterDispose();
            base.Dispose(disposing);
        }
    }
}
