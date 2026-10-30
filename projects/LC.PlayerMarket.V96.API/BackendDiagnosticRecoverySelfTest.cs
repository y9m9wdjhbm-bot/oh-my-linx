using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

// Exercises the actual startup recovery path with deliberately incomplete
// native evidence. This proves path reachability and refusal, not native recovery.
internal static class BackendDiagnosticRecoverySelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        string fixture = Path.Combine(MarketLogPaths.DefaultRoot, "verification-fixtures", "backend-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        void Check(string name, Action body)
        {
            try { body(); checks.Add(new("backend_reopen_" + name, true, "Isolated D-drive pending/native files; no LC/native invocation. Fixture=" + fixture)); }
            catch (Exception error) { checks.Add(new("backend_reopen_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Check("canonical_same_state_reuses_diagnostics", () =>
        {
            string state = Path.Combine(fixture, "state");
            string original = MarketLogPaths.BackendDiagnostics(state, true, null)!;
            foreach (string reopened in new[] { state, state + Path.DirectorySeparatorChar, Path.Combine(state, "."), Path.Combine(state, "child", ".."), state.ToUpperInvariant() })
                Need(MarketLogPaths.BackendDiagnostics(reopened, true, null) == original, "Equivalent state root received a different native evidence directory.");
            Need(original.StartsWith(Path.Combine(MarketLogPaths.DefaultRoot, "market-runtime", "standalone-state-"), StringComparison.Ordinal), "Production root escaped the fixed diagnostics base.");
        });
        Check("different_state_isolated_and_reopen_stable", () =>
        {
            string a = Path.Combine(fixture, "a"), b = Path.Combine(fixture, "b");
            string first = MarketLogPaths.BackendDiagnostics(a, true, null)!;
            string second = MarketLogPaths.BackendDiagnostics(b, true, null)!;
            Need(first != second && first == MarketLogPaths.BackendDiagnostics(a, true, null)
                && second == MarketLogPaths.BackendDiagnostics(b, true, null), "State identities collided or changed after reopening.");
        });
        Check("explicit_and_fake_paths_preserve_contract", () =>
        {
            string explicitRoot = Path.Combine(fixture, "explicit");
            Need(MarketLogPaths.BackendDiagnostics("unused", true, explicitRoot) == explicitRoot
                && MarketLogPaths.BackendDiagnostics("unused", false, explicitRoot) == explicitRoot
                && MarketLogPaths.BackendDiagnostics("unused", false, null) is null, "Explicit/fake diagnostics behavior changed.");
        });
        foreach (bool sameState in new[] { true, false })
        {
            Check(sameState ? "same_state_pending_reopen_reaches_native_proof_guard" : "different_state_pending_reopen_retains_exact_path_guard", () =>
            {
                string state = Path.Combine(fixture, sameState ? "same" : "different");
                string shops = Path.Combine(state, "shops"), sessions = Path.Combine(shops, "sessions");
                string initialNative = Path.Combine(MarketLogPaths.BackendDiagnostics(state, true, null)!, "native");
                Directory.CreateDirectory(sessions); Directory.CreateDirectory(initialNative);
                string request = Guid.NewGuid().ToString("N");
                string native = Path.Combine(initialNative, request + ".jsonl");
                File.WriteAllText(native, "{}\n"); // No valid no-dispatch proof: must retain the lock.
                var source = new SourceIdentity(int.MaxValue, DateTimeOffset.UtcNow.UtcTicks, @"D:\offline-fixture\LC.exe", "0x140000000", 1, 4096, new string('A', 64), "offline-" + request);
                var owner = new MarketOwner { Id = 7, LocalIdentity = "offline-owner", Name = "fixture" };
                string pending = Path.Combine(sessions, $"{source.ProcessId}-{source.CreationFileTime}.pending.json");
                File.WriteAllText(pending, JsonSerializer.Serialize(new { State = "NativeLaunchIntent_NoAutomaticRetry", RequestId = request,
                    Source = source, Owner = owner, TargetOwner = owner.Id, ViewerDirection = "ViewerBuy", NativeLog = native }, HubProtocol.Json));
                string pendingHash = Hash(pending), nativeHash = Hash(native);
                string reopenedState = sameState ? Path.Combine(state, ".") : Path.Combine(fixture, "foreign-state");
                string reopenedNative = Path.Combine(MarketLogPaths.BackendDiagnostics(reopenedState, true, null)!, "native");
                // Two reopen attempts use fresh stores. Neither may settle incomplete evidence.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var scans = new ShopScanProgressStore(Path.Combine(shops, "scan-progress"));
                    var owners = new ShopSearchedOwnerStore(Path.Combine(shops, "searched-owners"), Path.Combine(shops, "scan-progress"));
                    Need(scans.Error is null && owners.Error is null, "Isolated stores failed to initialize.");
                    var result = ShopStartupRecovery.Run(shops, scans, owners, () => false, nativeLogDirectory: reopenedNative);
                    Need(result is [{ State: "Deferred" }], "Incomplete native evidence was settled or silently skipped.");
                    if (sameState)
                        Need(result[0].Detail.StartsWith("缺少完整未派送證據；原鎖保留。", StringComparison.Ordinal),
                            "Reopened state could not reach the existing native-proof guard: " + result[0].Detail);
                    else Need(result[0].Detail == "Native log outside exact request directory.", "Different state bypassed the exact native path guard: " + result[0].Detail);
                    Need(File.Exists(pending) && Hash(pending) == pendingHash && Hash(native) == nativeHash,
                        "Recovery changed an unverified pending or its original native receipt.");
                    Need(!Directory.Exists(Path.Combine(shops, "recovery")), "Incomplete evidence generated a recovery receipt.");
                }
            });
        }
        return checks.ToArray();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
