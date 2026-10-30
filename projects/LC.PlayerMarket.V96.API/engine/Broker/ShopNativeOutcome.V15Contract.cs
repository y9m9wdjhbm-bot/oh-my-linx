using System.Text.Json;
namespace MemoryHub.Service;

internal sealed partial record ShopNativeOutcome
{
    private static readonly (string Rva, int Length, bool Metadata, string Sha256)[] V15PostGuards =
    [
        ("0x3EC5C1C", 12, true, "540571BB493FC0409992149A976798A0F8BA52F8F46F5467EC002F3B2ABC8DEE"),
        ("0x15371DC", 12, true, "39455C244CD3D4DE53471A0ADEC96067C74300DDC0BA6DB9733FE8F8E3A9FFC4"),
        ("0x72D300", 91, false, "BA49FB37E824FC2A33864D390BCEBB5EB52872025FBACCA457CF9C8FF7AB6EFD"),
        ("0x76BEB0", 67, false, "7E16D7DD5472FC8C48134A98B3ABC910F48B91B50F998758EBF38D51A153C80F")
    ];

    private static bool V15JournalContractMatches(JsonElement end, SourceIdentity source)
    {
        string stem = Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal", $"{source.ProcessId}-{source.CreationFileTime}");
        string? pending = Text(end, "PendingJournal");
        if (Text(end, "SingleTrialPendingSchema") != "V15SingleTrialPendingV1"
            || !string.Equals(Text(end, "SingleTrialPendingJournal"), stem + ".v15-singletrial.pending.json", StringComparison.OrdinalIgnoreCase)
            || pending is null) return false;
        if (string.Equals(pending, stem + ".pending.json", StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = stem + ".force-", suffix = ".pending.json";
        return pending.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && pending.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && pending.Length == prefix.Length + 32 + suffix.Length
            && Guid.TryParseExact(pending.AsSpan(prefix.Length, 32), "N", out _);
    }

    private static bool ConfirmedV15ResultReceipt(string text, SourceIdentity source, ulong image, ulong owner, JsonElement native, JsonElement end, bool postConfirmed)
    {
        if (!V15JournalContractMatches(end, source)
            || postConfirmed && !end.GetProperty("EventResultDurable").GetBoolean()) return false;
        var rows = new List<JsonElement>(); JsonElement? eventResult = null; int results = 0, disk = 0, buffered = 0;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            using var doc = JsonDocument.Parse(line); var row = doc.RootElement;
            if (row.GetProperty("Sequence").GetInt32() != rows.Count + 1) return false;
            bool durable = row.GetProperty("DiskFlush").GetBoolean();
            if (Text(row, "Kind") == "EVENT_RESULT")
            { if (!durable) return false; eventResult = row.GetProperty("Data").Clone(); results++; }
            if (Text(row, "Kind") == "END") { if (!durable) return false; }
            else if (durable) disk++; else buffered++;
            rows.Add(row.Clone());
        }
        if (results != 1 || eventResult is not { } result || rows.Count < 4
            || end.GetProperty("LogRecordsBeforeEnd").GetInt32() != rows.Count - 1
            || end.GetProperty("LogDiskFlushesBeforeEnd").GetInt32() != disk
            || end.GetProperty("LogStreamFlushesBeforeEnd").GetInt32() != 0
            || end.GetProperty("LogBufferedRecordsBeforeEnd").GetInt32() != buffered) return false;
        double io = end.GetProperty("LogIoWriteDurationMsBeforeEnd").GetDouble();
        double serialization = end.GetProperty("LogSerializationDurationMsBeforeEnd").GetDouble();
        double total = end.GetProperty("LogWriteDurationMsBeforeEnd").GetDouble();
        if (!double.IsFinite(io) || io < 0 || !double.IsFinite(serialization) || serialization < 0
            || !double.IsFinite(total) || total != io + serialization
            || result.GetProperty("NativeReturned").GetBoolean() != postConfirmed
            || result.GetProperty("PostCodeConfirmed").GetBoolean() != postConfirmed
            || end.GetProperty("PostCodeConfirmed").GetBoolean() != postConfirmed
            || end.GetProperty("BorrowedSourceContractVerified").GetBoolean() != postConfirmed
            || end.GetProperty("NativeAlreadyReturned").GetBoolean() != postConfirmed
            || end.GetProperty("AckObserved").GetBoolean() || end.GetProperty("ListReceivedVerified").GetBoolean()
            || end.GetProperty("RemoteModuleUnloadProven").GetBoolean() || end.GetProperty("EntireHookTailExitProven").GetBoolean()
            || !result.GetProperty("PostReadBeforeDurableResult").GetBoolean()
            || result.GetProperty("PostCodeErrorRva").ValueKind != JsonValueKind.Null
            || result.GetProperty("ListReceivedVerified").GetBoolean() || result.GetProperty("OwnerUpdatedByAckVerified").GetBoolean()
            || result.GetProperty("TargetOwner").GetUInt64() != owner
            || !JsonElement.DeepEquals(result.GetProperty("State"), native)) return false;
        if (!DateTimeOffset.TryParse(Text(result, "PostStartedAt"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var started)
            || !DateTimeOffset.TryParse(Text(result, "PostFinishedAt"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var finished)
            || started == default || finished < started) return false;
        var proofs = result.GetProperty("PostProof");
        if (!postConfirmed)
            return Text(result, "PostCodeErrorCode") == "NativeResultUnconfirmed"
                && proofs.ValueKind == JsonValueKind.Array && proofs.GetArrayLength() == 0;
        if (result.GetProperty("PostCodeErrorCode").ValueKind != JsonValueKind.Null) return false;
        if (proofs.ValueKind != JsonValueKind.Array || proofs.GetArrayLength() != V15PostGuards.Length) return false;
        for (int i = 0; i < V15PostGuards.Length; i++)
        {
            var guard = V15PostGuards[i]; var proof = proofs[i];
            var before = proof.GetProperty("Before"); var after = proof.GetProperty("After");
            if (Rva(Text(proof, "Rva")) != guard.Rva || proof.GetProperty("Length").GetInt32() != guard.Length
                || proof.GetProperty("Metadata").GetBoolean() != guard.Metadata
                || Text(proof, "CurrentSha256") != guard.Sha256
                || !proof.GetProperty("Matched").GetBoolean() || !proof.GetProperty("BodyAbClosed").GetBoolean()
                || !proof.GetProperty("PageAbClosed").GetBoolean() || !JsonElement.DeepEquals(before, after)
                || !V15PostPageMatches(before, image, Convert.ToUInt32(guard.Rva[2..], 16), guard.Length, !guard.Metadata)) return false;
        }
        return true;
    }

    private static bool V15PostPageMatches(JsonElement page, ulong image, uint rva, int length, bool body)
    {
        ulong start = page.GetProperty("Base").GetUInt64(), size = page.GetProperty("Size").GetUInt64(), address = checked(image + rva);
        uint protect = page.GetProperty("Protect").GetUInt32();
        _ = page.GetProperty("AllocationProtect").GetUInt32();
        if (page.GetProperty("PartitionId").GetUInt32() > ushort.MaxValue) return false;
        return page.GetProperty("AllocationBase").GetUInt64() == image
            && page.GetProperty("State").GetUInt32() == 0x1000 && page.GetProperty("Type").GetUInt32() == 0x1000000
            && (!body || protect is 0x20 or 0x40 or 0x80)
            && start <= address && size >= (uint)length && address - start <= size - (uint)length;
    }

    private static bool V15NoEntryContractMatches(JsonElement native) =>
        // V15 sets Entered before executing the getter or acquiring its lease.
        // A completed rejection before entry cannot contain request or lease work.
        native.GetProperty("Stage").GetInt32() == 0
        && native.GetProperty("Status").GetInt32() is -3 or -4
        && native.GetProperty("PostCodeConfirmed").GetInt32() == 0
        && native.GetProperty("LeaseAcquired").GetInt32() == 0
        && native.GetProperty("LeaseReleaseAttempted").GetInt32() == 0
        && native.GetProperty("LeaseReleaseCompleted").GetInt32() == 0
        && native.GetProperty("ReleaseExceptionCode").GetUInt32() == 0
        && native.GetProperty("RequestUnchanged").GetInt32() == 0
        && native.GetProperty("RequestMetadataBefore").GetUInt64() == 0
        && native.GetProperty("RequestMetadataAfter").GetUInt64() == 0
        && native.GetProperty("DestructorCalls").GetInt32() == 0;
}
