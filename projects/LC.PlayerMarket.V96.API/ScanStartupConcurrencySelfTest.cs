using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class ScanStartupConcurrencySelfTest
{
    internal static int RunReport(string output)
    {
        var checks = Run();
        int failed = checks.Count(check => !check.Passed);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = checks.Length - failed, Failed = failed, Checks = checks, GameInputs = 0 }, HubProtocol.Json));
        return failed == 0 ? 0 : 1;
    }

    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        var source = new SourceIdentity(123, 456, "offline.exe", "0x10000", 1, 65536, new string('A', 64), "offline");
        string plan = Guid.NewGuid().ToString("N");
        foreach (string scenario in new[] { "settled", "uncertain", "corrupt", "pair_identity", "wrong_filename", "duplicate_request" })
        {
            string root = Path.Combine(Path.GetTempPath(), "LC-ScanStartup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "source-intents"));
            try
            {
                var at = DateTimeOffset.UtcNow;
                for (int i = 0; i < 32; i++)
                {
                    string request = "offline-" + i;
                    var intent = new ScanIntentEvidence(source, plan, request, at, false, false, false);
                    Write(request, false, intent);
                    if (scenario != "uncertain" || i != 31)
                        Write(request, true, intent with { Settled = true, At = at.AddTicks(1) });
                }
                string last = FileName("offline-31", true);
                if (scenario == "corrupt") File.WriteAllText(last, "{invalid");
                if (scenario == "pair_identity")
                    Write("offline-31", true, new ScanIntentEvidence(source with { CreationFileTime = 999 }, plan, "offline-31", at.AddTicks(1), true, false, false));
                if (scenario == "wrong_filename")
                    File.Move(FileName("offline-31", false), Path.Combine(root, "source-intents", "WRONG.intent.json"));
                if (scenario == "duplicate_request")
                    File.Copy(FileName("offline-31", false), Path.Combine(root, "source-intents", "DUPLICATE.intent.json"));

                bool expectedError = scenario is not ("settled" or "uncertain");
                bool expectedBlocked = scenario != "settled";
                var store = new ShopScanProgressStore(root); // Startup uses eight readers.
                Need((store.Error is not null) == expectedError && store.BlocksSource(source) == expectedBlocked);
                store.Reload(); // Subsequent validation keeps four readers.
                Need((store.Error is not null) == expectedError && store.BlocksSource(source) == expectedBlocked);
                checks.Add(new("scan_startup8_reload4_" + scenario, true, "Both paths preserve full validation and source blocking"));

                string FileName(string request, bool settled) => Path.Combine(root, "source-intents",
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request))) + (settled ? ".resolved.json" : ".intent.json"));
                void Write(string request, bool settled, ScanIntentEvidence value) =>
                    File.WriteAllBytes(FileName(request, settled), JsonSerializer.SerializeToUtf8Bytes(value, HubProtocol.Json));
            }
            catch (Exception failure) { checks.Add(new("scan_startup8_reload4_" + scenario, false, failure.ToString())); }
            finally
            {
                string resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("LC-ScanStartup-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected fixture cleanup path");
                Directory.Delete(resolved, true);
            }
        }
        return checks.ToArray();
    }

    private static void Need(bool value) { if (!value) throw new InvalidDataException("Startup/reload validation or uncertainty fence changed"); }
}
