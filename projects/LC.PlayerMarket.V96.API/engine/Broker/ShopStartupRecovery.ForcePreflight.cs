using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemoryHub.Service;

internal static partial class ShopStartupRecovery
{
    // This recovery admits only the dedicated, complete initial owner-change
    // receipt. Generic ContextUnavailable and cleanup-only outcomes stay locked.
    private sealed record ForceRecoveryInput(string Path, long Bytes, string Sha256);
    private sealed record ForceRecoveryBinding(string RequestId, SourceIdentity Source, string PlanId,
        ulong OwnerId, string OwnerIdentity, string Direction, ForceRecoveryInput[] Inputs);
    private sealed record ForceOwnerBackup(string Relative, long Bytes, string Sha256);
    private sealed record ForceRecoveryBackup(int Schema, ForceRecoveryBinding Binding, ForceOwnerBackup[] OwnerLedger);

    private static bool IsOwnerChangeProof(ShopNativeOutcome proof) => proof.SafeToSettle
        && proof.NoEntryConfirmed && proof.SourceMatched && !proof.Entered && !proof.Returned
        && proof.LogPurpose == ShopNativeLogPurpose.NativeRequest
        && proof.Failure is { Code: "NativePreflightOwnerChanged", Scope: "Global", Phase: "Preflight", Rva: null };

    private static ForceRecoveryBinding BindForceOwnerChange(string root, string file, Pending pending,
        ShopScanProgressStore scans, ShopNativeOutcome proof)
    {
        ForceNeed(IsOwnerChangeProof(proof), "Force owner-change recovery lacks the dedicated complete no-dispatch proof.");
        ForceNeed(scans.Error is null && scans.Source == pending.Source && scans.PlanId is not null,
            "Force recovery source or current scan plan differs.");
        string stem = $"{pending.Source.ProcessId}-{pending.Source.CreationFileTime}";
        ForceNeed(string.Equals(Path.GetFullPath(file), Path.GetFullPath(ShopForceDispatchPolicy.PendingPath(root, stem, pending.RequestId)),
            StringComparison.OrdinalIgnoreCase), "Force pending request-hash filename differs.");
        CheckForceSharedPending(pending.Source);
        using var pendingDoc = ForceDocument(file, 32768);
        var p = pendingDoc.RootElement;
        ForceNeed(p.GetProperty("state").GetString() == "ForceNativeLaunchIntent_NoAutomaticReplay"
            && p.GetProperty("forceDispatch").GetBoolean()
            && p.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json) == pending.Source
            && p.GetProperty("requestId").GetString() == pending.RequestId
            && p.GetProperty("targetOwner").GetUInt64() == pending.Owner.Id
            && p.GetProperty("viewerDirection").GetString() == pending.ViewerDirection,
            "Force pending content identity differs.");

        string key = ManualLockReleaseStore.Key(pending.RequestId);
        string intentPath = Path.Combine(root, "scan-progress", "source-intents", key + ".intent.json");
        var intent = ForceRead<ScanIntentEvidence>(intentPath, 32768);
        ForceNeed(intent.Source == pending.Source && intent.RequestId == pending.RequestId && intent.PlanId == scans.PlanId
            && !intent.Settled && !intent.Entered && !intent.Returned, "Force source-intent binding differs.");
        string requestPath = Path.Combine(root, "requests", key + ".request.json");
        string responsePath = Path.Combine(root, "requests", key + ".response.json");
        using var requestDoc = ForceDocument(requestPath, 128 * 1024);
        var accepted = requestDoc.RootElement;
        var request = accepted.GetProperty("request").Deserialize<ShopCommandRequest>(HubProtocol.Json);
        ForceNeed(request is not null && request.RequestId == pending.RequestId && request.Command == ShopCommands.OpenShop
            && request.ForceDispatch && request.ExpectedSourceSessionKey == pending.Source.SessionKey
            && request.OwnerId == pending.Owner.Id && request.ExpectedOwnerIdentity == pending.Owner.LocalIdentity
            && request.ViewerDirection == pending.ViewerDirection && accepted.GetProperty("state").GetString() == "ScanChild_NoAutomaticReplay",
            "Force original scan-child request differs.");
        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HubProtocol.Json))));
        ForceNeed(accepted.GetProperty("signature").GetString() == signature, "Force accepted request signature differs.");
        using var responseDoc = ForceDocument(responsePath, 16 * 1024 * 1024);
        var response = responseDoc.RootElement;
        ForceNeed(response.GetProperty("requestId").GetString() == pending.RequestId
            && response.GetProperty("currentSourceSessionKey").GetString() == pending.Source.SessionKey
            && response.GetProperty("state").GetString() == "Unverified"
            && !response.GetProperty("nativeEntered").GetBoolean() && !response.GetProperty("nativeReturned").GetBoolean()
            && !response.GetProperty("responseVerified").GetBoolean() && response.GetProperty("sessionBlocked").GetBoolean()
            && !response.GetProperty("workSettled").GetBoolean()
            && response.GetProperty("failureCode").GetString() is ("ContextUnavailable" or "NativePreflightOwnerChanged")
            && response.GetProperty("failureScope").GetString() == "Global"
            && response.GetProperty("failurePhase").GetString() == "Preflight"
            && response.GetProperty("failureRva").ValueKind == JsonValueKind.Null,
            "Force archived response is not the matching blocked preflight result.");
        string[] plans = Directory.EnumerateFiles(Path.Combine(root, "scan-progress"), "*-" + intent.PlanId + ".plan.json").Take(2).ToArray();
        ForceNeed(plans.Length == 1, "Force original plan is absent or ambiguous.");
        var plan = ForceRead<SavedScanPlan>(plans[0], 4 * 1024 * 1024);
        ForceNeed(plan.Source == pending.Source && plan.PlanId == intent.PlanId
            && plan.OriginalSnapshotId == request!.TargetSnapshotId
            && plan.Owners.Count(owner => owner.Id == pending.Owner.Id && owner.LocalIdentity == pending.Owner.LocalIdentity) == 1,
            "Force plan source, snapshot or owner identity differs.");
        string[] inputs = [file, pending.NativeLog, intentPath, requestPath, responsePath, plans[0]];
        var binding = new ForceRecoveryBinding(pending.RequestId, pending.Source, intent.PlanId, pending.Owner.Id,
            pending.Owner.LocalIdentity, pending.ViewerDirection, inputs.Select(ForceInput).ToArray());
        string backup = Path.Combine(root, "recovery", key);
        if (File.Exists(Path.Combine(backup, "proof.json"))) ValidateForceBackup(backup, binding);
        return binding;
    }

    private static ForceRecoveryInput[] BindOwnerChangeMode(string file, Pending pending, bool force, string parsedNativeHash)
    {
        // ParseFile binds source and target owner, but its API has no direction.
        // Bind both normal and force pending to that last request-specific gap.
        using var pendingDoc = ForceDocument(file, 32768);
        var p = pendingDoc.RootElement;
        ForceNeed(new FileInfo(pending.NativeLog).Length is > 0 and <= 4 * 1024 * 1024, "Owner-change native receipt exceeds bounds.");
        RejectForceLinks(pending.NativeLog);
        var nativeRows = File.ReadLines(pending.NativeLog).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
        { using var doc = JsonDocument.Parse(line); ForceNeed(ForceUnique(doc.RootElement), "Duplicate native evidence property."); return doc.RootElement.Clone(); }).ToArray();
        var start = nativeRows.Single(row => row.GetProperty("Kind").GetString() == "START").GetProperty("Data");
        var nativeSource = nativeRows.Single(row => row.GetProperty("Kind").GetString() == "SOURCE").GetProperty("Data");
        string expectedMode = pending.ViewerDirection == "ViewerBuy" ? "buy" : "sell";
        ForceNeed(start.GetProperty("Mode").GetString() == expectedMode
            && nativeSource.GetProperty("Owner").GetUInt64() == p.GetProperty("currentOwnerIdRaw").GetUInt64()
            && nativeRows.Any(row => row.GetProperty("Kind").GetString() == "FORCE_DISPATCH") == force,
            "Native mode, force policy or sampled current-owner differs from the original pending.");
        var inputs = new[] { ForceInput(file), ForceInput(pending.NativeLog) };
        ForceNeed(inputs[1].Sha256 == parsedNativeHash, "Owner-change evidence changed after parsing.");
        CheckOwnerChangeUnchanged(inputs, pending.Source);
        return inputs;
    }

    private static void CheckOwnerChangeUnchanged(ForceRecoveryInput[]? inputs, SourceIdentity source)
    {
        if (inputs is null) return;
        CheckForceSharedPending(source);
        foreach (var input in inputs) ForceNeed(ForceInput(input.Path) == input, "Owner-change pending or receipt changed.");
    }

    private static ForceRecoveryInput ForceInput(string path)
    {
        RejectForceLinks(path);
        var before = new FileInfo(path); long size = before.Length; DateTime stamp = before.LastWriteTimeUtc;
        string digest = Hash(path);
        var after = new FileInfo(path);
        ForceNeed(after.Length == size && after.LastWriteTimeUtc == stamp, "Force evidence changed while hashing.");
        return new(Path.GetFullPath(path), size, digest);
    }

    private static void CheckForceUnchanged(ForceRecoveryBinding binding)
    {
        CheckForceSharedPending(binding.Source);
        foreach (var input in binding.Inputs)
            ForceNeed(ForceInput(input.Path) == input, "Force recovery input changed: " + input.Path);
    }

    private static void BackupForceEvidence(string root, string backup, ForceRecoveryBinding binding)
    {
        CheckForceUnchanged(binding);
        foreach (var input in binding.Inputs) BackupExact(input.Path, Path.Combine(backup, "force-" + Path.GetFileName(input.Path)));
        string manifest = Path.Combine(backup, "force-binding.json");
        if (!File.Exists(manifest))
        {
            string ownerRoot = Path.Combine(root, "searched-owners");
            RejectForceLinks(ownerRoot);
            var ownerFiles = Directory.EnumerateFiles(ownerRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Take(20001).ToArray();
            ForceNeed(ownerFiles.Length <= 20000 && ownerFiles.Sum(path => new FileInfo(path).Length) <= 128L * 1024 * 1024,
                "Force owner ledger backup exceeds bounds.");
            var ownerBackup = new List<ForceOwnerBackup>();
            foreach (string path in ownerFiles)
            {
                var input = ForceInput(path);
                string relative = Path.GetRelativePath(ownerRoot, path), target = Path.Combine(backup, "original-owner-ledger", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); BackupExact(path, target);
                ownerBackup.Add(new(relative, input.Bytes, input.Sha256));
            }
            WriteNew(manifest, new ForceRecoveryBackup(1, binding, ownerBackup.ToArray()));
        }
        ValidateForceBackup(backup, binding);
    }

    private static void ValidateForceBackup(string backup, ForceRecoveryBinding binding)
    {
        var saved = ForceRead<ForceRecoveryBackup>(Path.Combine(backup, "force-binding.json"), 8 * 1024 * 1024);
        ForceNeed(saved.Schema == 1 && saved.Binding.RequestId == binding.RequestId && saved.Binding.Source == binding.Source
            && saved.Binding.PlanId == binding.PlanId && saved.Binding.OwnerId == binding.OwnerId
            && saved.Binding.OwnerIdentity == binding.OwnerIdentity && saved.Binding.Direction == binding.Direction
            && saved.Binding.Inputs.SequenceEqual(binding.Inputs), "Force recovery backup binding changed.");
        foreach (var input in binding.Inputs)
        {
            string copy = Path.Combine(backup, "force-" + Path.GetFileName(input.Path));
            RejectForceLinks(copy);
            ForceNeed(new FileInfo(copy).Length == input.Bytes && Hash(copy) == input.Sha256, "Force original input backup changed.");
        }
        ForceNeed(saved.OwnerLedger.Length <= 20000, "Force saved owner ledger exceeds bounds.");
        string ownerRoot = Path.GetFullPath(Path.Combine(backup, "original-owner-ledger")) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in saved.OwnerLedger)
        {
            string copy = Path.GetFullPath(Path.Combine(ownerRoot, item.Relative));
            ForceNeed(!Path.IsPathRooted(item.Relative) && copy.StartsWith(ownerRoot, StringComparison.OrdinalIgnoreCase)
                && names.Add(copy), "Force owner backup path is invalid or duplicated.");
            RejectForceLinks(copy);
            ForceNeed(new FileInfo(copy).Length == item.Bytes && Hash(copy) == item.Sha256, "Force owner ledger backup changed.");
        }
    }

    private static void CheckForceSharedPending(SourceIdentity source)
    {
        string shared = @"D:\lineage\LC_PlayerShopListAction_v1\journal";
        try { _ = File.GetAttributes(shared); }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        RejectForceLinks(shared);
        string stem = $"{source.ProcessId}-{source.CreationFileTime}";
        ForceNeed(!Directory.EnumerateFiles(shared, stem + "*.pending.json").Any(), "Shared native pending remains; force recovery refused.");
        string transaction = Path.Combine(shared, stem + ".delayed-postcode-recovery-v1.transaction.json");
        if (File.Exists(transaction))
            ForceNeed(ShopPostCodeRecoveryProof.CommittedTransaction(transaction, source, shared), "Native recovery transaction remains incomplete.");
    }

    private static T ForceRead<T>(string path, int maximum)
    { using var doc = ForceDocument(path, maximum); return doc.RootElement.Deserialize<T>(HubProtocol.Json) ?? throw new InvalidDataException("Force recovery evidence missing."); }
    private static JsonDocument ForceDocument(string path, int maximum)
    {
        RejectForceLinks(path);
        var info = new FileInfo(path);
        ForceNeed(info.Exists && info.Length > 0 && info.Length <= maximum, "Force evidence missing or exceeds bounds: " + path);
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!ForceUnique(doc.RootElement)) { doc.Dispose(); throw new InvalidDataException("Duplicate force evidence property."); }
        return doc;
    }
    private static bool ForceUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var item in value.EnumerateObject()) if (!names.Add(item.Name) || !ForceUnique(item.Value)) return false; }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (!ForceUnique(item)) return false;
        return true;
    }
    private static void RejectForceLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { ForceNeed((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked force recovery path: " + current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void ForceNeed(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
