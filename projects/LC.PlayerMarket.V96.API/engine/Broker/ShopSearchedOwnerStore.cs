using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryHub.Service;

internal sealed record ShopSearchedOwnerPage(ShopSearchedOwnerRecord[] Owners, ulong? NextOwnerId, bool Truncated);
internal sealed record SearchedOwnerEvent
{
    public SearchedOwnerEvent[]? ImportedEntries { get; init; }
    [JsonRequired] public int Schema { get; init; } = 1;
    [JsonRequired] public int Sequence { get; init; }
    [JsonRequired] public string Kind { get; init; } = "";
    [JsonRequired] public string Epoch { get; init; } = "";
    [JsonRequired] public DateTimeOffset At { get; init; }
    [JsonRequired] public string Key { get; init; } = "";
    [JsonRequired] public string RequestId { get; init; } = "";
    [JsonRequired] public SourceIdentity? Source { get; init; }
    [JsonRequired] public MarketOwner? Owner { get; init; }
    [JsonRequired] public string Direction { get; init; } = "";
    [JsonRequired] public string State { get; init; } = "";
    [JsonRequired] public string SourceSessionKey { get; init; } = "";
    [JsonRequired] public DateTimeOffset SearchedAt { get; init; }
}
internal sealed record SearchedOwnerHead([property: JsonRequired] int Sequence, [property: JsonRequired] string Sha256);

/// <summary>
/// Append-only local numeric owner/page history. Epoch clearing resets only the
/// visible dedup collection. Unresolved intents retain their original process
/// fence and are never erased, including across epochs. No LC API is used here.
/// </summary>
internal sealed partial class ShopSearchedOwnerStore
{
    private const int MaximumEvents = 262144;
    private readonly string root;
    private readonly object sync = new();
    private readonly Dictionary<string, SearchedOwnerEvent> attempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> importKeys = new(StringComparer.Ordinal);
    private string epoch = "", error = "";
    private int sequence, archiveTailSequence;
    private string archiveTailHash = "";
    private DateTimeOffset updatedAt;
    private DateTimeOffset? clearedAt;
    private bool imported;
    internal string? Error { get { lock (sync) return error.Length == 0 ? null : error; } }
    internal string Revision { get { lock (sync) return epoch + ":" + sequence.ToString(CultureInfo.InvariantCulture); } }

    // Reusable offline initialization/audit entry point: writes only directory,
    // never legacyProgressDirectory or legacyHistoryDirectory.
    internal ShopSearchedOwnerStore(string directory, string legacyProgressDirectory, string? legacyHistoryDirectory = null,
        CancellationToken startupCancellation = default, Action<ShopStartupStageEvent>? startupStage = null)
    {
        root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        try
        {
            using var lease = Writer();
            startupCancellation.ThrowIfCancellationRequested();
            ShopStartupTrace.Run("owner.ledger-replay", startupStage, () => Reload(startupCancellation, startupStage)); RequireHealthy();
            if (sequence == 0) Append(new() { Kind = "Initialize", Epoch = Guid.NewGuid().ToString("N") });
            if (!imported)
            {
                ShopStartupTrace.Run("owner.first-import", startupStage, () =>
                    ImportBatch(ShopScanProgressStore.ReadHistoricalEvidence(legacyProgressDirectory)
                        .Concat(ReadVerifiedListings(legacyHistoryDirectory, startupCancellation)), startupCancellation));
                Append(new() { Kind = "ImportComplete", Epoch = epoch });
            }
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested) { throw; }
        catch (Exception failure) { error = "擺攤搜尋紀錄無法完整初始化；未開放重送：" + failure.Message; }
    }

    internal static ShopSearchedOwnerStore OpenReadOnly(string directory)
    {
        var value=new ShopSearchedOwnerStore(directory,true);
        try{value.Reload();value.RequireReady();}catch(Exception ex){value.error="唯讀紀錄查核失敗："+ex.Message;}
        return value;
    }
    private ShopSearchedOwnerStore(string directory,bool readOnly){root=Path.GetFullPath(directory);}
    internal bool ShouldSkip(ulong ownerId, string direction) => ShouldSkip(ownerId, direction, false);

    internal bool ShouldSkip(ulong ownerId, string direction, bool forceDispatch)
    {
        lock (sync)
        {
            RequireReady();
            return attempts.Values.Any(item => item.Epoch == epoch && item.Owner!.Id == ownerId && item.Direction == direction
                && (item.State is "Sent" or "Confirmed" || !forceDispatch && item.State == "Uncertain"));
        }
    }

    internal bool BlocksSource(SourceIdentity source)
    {
        lock (sync) return error.Length != 0 || !imported || attempts.Values.Any(item => item.State == "Uncertain"
            && item.Source is { } prior && prior.ProcessId == source.ProcessId && prior.CreationFileTime == source.CreationFileTime);
    }

    internal void Begin(SourceIdentity source, MarketOwner owner, string direction, string requestId, bool forceDispatch = false) => Mutate(() =>
    {
        if (!forceDispatch && BlocksSource(source) || ShouldSkip(owner.Id, direction, forceDispatch)) throw Failure("這個玩家 ID 的本頁已有搜尋或未明意圖；未重複送出。");
        string key = LiveKey(requestId);
        if (attempts.ContainsKey(key)) throw Failure("原生搜尋意圖已存在，禁止同請求重送。");
        Append(new() { Kind = "Intent", Epoch = epoch, Key = key, RequestId = requestId, Source = source,
            Owner = owner, Direction = direction, State = "Uncertain", SourceSessionKey = source.SessionKey, SearchedAt = DateTimeOffset.UtcNow });
    });

    // Caller must supply the already validated native return/positive-no-entry
    // result. Generic response false flags never call this method.
    internal void Settle(string requestId, bool returned) => Mutate(() =>
    {
        if (!attempts.TryGetValue(LiveKey(requestId), out var item) || item.State != "Uncertain")
            throw Failure("搜尋結束證據找不到唯一原意圖。");
        Append(item with { Kind = "Settled", State = returned ? "Sent" : "NotSent" });
    });

    internal void ValidateUnpublishedRecovery(string requestId,SourceIdentity source,ulong ownerId,string ownerIdentity,string direction)
    {
        lock(sync)
        {
            using var lease=Writer(); Synchronize(); RequireHealthy();
            if(!attempts.TryGetValue(LiveKey(requestId),out var item)||item.Source!=source||item.Owner?.Id!=ownerId
                ||item.Owner.LocalIdentity!=ownerIdentity||item.Direction!=direction||item.State is not("Uncertain" or "NotSent"))
                throw Failure("Recovery original owner intent identity or state differs.");
        }
    }
    internal void SettleUnpublishedRecovery(string requestId,SourceIdentity source,ulong ownerId,string direction) => Mutate(() =>
    {
        if(!attempts.TryGetValue(LiveKey(requestId),out var item)||item.Source!=source||item.Owner?.Id!=ownerId||item.Direction!=direction)
            throw Failure("Recovery original owner intent mismatch.");
        if(item.State=="NotSent")return;
        if(item.State!="Uncertain")throw Failure("Recovery cannot change an already sent or confirmed page.");
        Append(item with{Kind="Settled",State="NotSent"});
    });

    // Commit existing V95-readable transitions with one durable head update.
    // Every event file is individually durable before publishing that head.
    // Until this commits, the original Intent remains Uncertain and cannot replay.
    internal void CompletePage(string requestId, bool confirmed) => Mutate(() =>
    {
        if (!attempts.TryGetValue(LiveKey(requestId), out var item) || item.State != "Uncertain")
            throw Failure("單頁完成缺少唯一尚未結清的原意圖。");
        var settled = item with { Kind = "Settled", State = "Sent" };
        if (!confirmed) Append(settled);
        else AppendCommittedCompletionBatch([settled, item with { Kind = "Confirmed", State = "Confirmed" }]);
    });

    internal void Confirm(string requestId) => Mutate(() =>
    {
        if (!attempts.TryGetValue(LiveKey(requestId), out var item) || item.State != "Sent")
            throw Failure("清單確認缺少已返回的原生意圖。");
        Append(item with { Kind = "Confirmed", State = "Confirmed" });
    });

    internal void Clear(string expectedRevision) => Mutate(() =>
    {
        if (expectedRevision != Revision) throw Failure("搜尋紀錄已更新；未清除其他版本。");
        Append(new() { Kind = "Clear", Epoch = Guid.NewGuid().ToString("N") });
    });

    // A persisted local reset can finish after a crash. The same reset epoch
    // must never clear records that were added after that reset completed.
    internal void ClearForReset(string expectedRevision, string resetEpoch) => Mutate(() =>
    {
        if (!Guid.TryParseExact(resetEpoch, "N", out _)) throw Failure("重設搜尋期別無效。");
        if (epoch == resetEpoch) return;
        if (expectedRevision != Revision) throw Failure("重設前搜尋紀錄已不同；保留新紀錄，未套用過期清除。");
        Append(new() { Kind = "Clear", Epoch = resetEpoch });
    });

    internal ShopSearchHistorySummary Summary(bool canClear, string blockReason)
    {
        lock (sync)
        {
            var pages = CurrentPages();
            string? readError = error.Length == 0 && imported ? null : error.Length == 0 ? "歷史搜尋匯入尚未完成。" : error;
            return new() { Revision = Revision, Epoch = epoch, OwnerCount = pages.Select(item => item.Owner!.Id).Distinct().Count(),
                SearchedPages = pages.Count(item => item.State is "Sent" or "Confirmed"), ConfirmedPages = pages.Count(item => item.State == "Confirmed"),
                UncertainPages = pages.Count(item => item.State == "Uncertain"), ReadError = readError, ClearedAt = clearedAt,
                CanClear = canClear && readError is null, ClearBlockReason = readError ?? (canClear ? "" : blockReason) };
        }
    }

    internal ShopSearchedOwnerPage ReadOwners(ulong? afterOwnerId, int limit)
    {
        lock (sync)
        {
            RequireReady();
            if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
            var owners = CurrentPages().GroupBy(item => item.Owner!.Id).Where(group => afterOwnerId is null || group.Key > afterOwnerId.Value)
                .OrderBy(group => group.Key).Take(limit + 1).Select(group =>
                {
                    var allOwnerEvidence = attempts.Values.Where(item => item.Epoch == epoch && item.Owner!.Id == group.Key && item.State != "NotSent").ToArray();
                    var latest = allOwnerEvidence.OrderByDescending(item => item.Owner!.ObservedAt).ThenByDescending(item => item.SearchedAt).First();
                    return new ShopSearchedOwnerRecord { OwnerId = group.Key, OwnerName = latest.Owner!.Name,
                        Position = latest.Owner.Position, MapName = latest.Owner.MapName, ObservedAt = latest.Owner.ObservedAt,
                        LastSearchedAt = allOwnerEvidence.Max(item => item.SearchedAt), SourceSessionKey = latest.SourceSessionKey,
                        BuyState = group.FirstOrDefault(item => item.Direction == ShopPageModes.ViewerBuy)?.State ?? "NotSent",
                        SellState = group.FirstOrDefault(item => item.Direction == ShopPageModes.ViewerSell)?.State ?? "NotSent" };
                }).ToArray();
            bool more = owners.Length > limit;
            if (more) owners = owners[..limit];
            return new(owners, more ? owners[^1].OwnerId : null, more);
        }
    }

    private SearchedOwnerEvent[] CurrentPages() => attempts.Values
        .Where(item => item.Epoch == epoch && item.State != "NotSent")
        .GroupBy(item => (item.Owner!.Id, item.Direction))
        .Select(group => group.OrderByDescending(item => item.State == "Uncertain" ? 3 : item.State == "Confirmed" ? 2 : 1)
            .ThenByDescending(item => item.At).First()).ToArray();

    private void ImportBatch(IEnumerable<ShopHistoricalPageEvidence> evidence, CancellationToken cancellation)
    {
        var batch = new List<SearchedOwnerEvent>();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        void Flush()
        {
            if (batch.Count == 0) return;
            Append(new() { Kind = "ImportBatch", Epoch = epoch, ImportedEntries = batch.ToArray() });
            batch.Clear(); pending.Clear();
        }
        foreach (var item in evidence)
        {
            cancellation.ThrowIfCancellationRequested();
            string key = Hash(item.EvidenceId);
            if (importKeys.Contains(key) || pending.Contains(key)) continue;
            var entry = new SearchedOwnerEvent { Kind = "Import", Epoch = epoch, Key = key, RequestId = item.RequestId, Source = item.Source,
                Owner = item.Owner, Direction = item.Direction, State = item.State, SourceSessionKey = item.Source?.SessionKey ?? item.SourceSessionKey, SearchedAt = item.At };
            if (batch.Count > 0 && (batch.Count >= 64 || JsonSerializer.SerializeToUtf8Bytes(batch.Append(entry), HubProtocol.Json).Length > 100 * 1024)) Flush();
            batch.Add(entry); pending.Add(key);
        }
        Flush();
    }

    private static IEnumerable<ShopHistoricalPageEvidence> ReadVerifiedListings(string? directory, CancellationToken cancellation)
    {
        if (directory is null || !Directory.Exists(directory)) yield break;
        var files = Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).Take(65537).ToArray();
        if (files.Length > 65536) throw new InvalidDataException("商品歷史超出首次匯入界線。");
        foreach (string path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var value = Read<MarketListing>(path, 1024 * 1024);
            if (!value.OwnerAndResponseMatched || !value.Complete) continue;
            if (value.State != "OwnerAndResponseMatched" || value.RequestedAt is not { } requested || requested == default
                || value.CapturedAt < requested || string.IsNullOrWhiteSpace(value.SourceSessionKey) || value.SourceSessionKey.Length > 2048
                || string.IsNullOrWhiteSpace(value.Revision) || value.Revision.Length > 128)
                throw new InvalidDataException("已確認商品歷史缺少完整來源／請求時間證據。");
            // MarketListing has no full SourceIdentity. It can supplement known
            // confirmed-page dedup only, never settle a native/source pending.
            yield return new("listing:" + value.SourceSessionKey + ":" + value.Revision, value.RequestId, null, value.Owner,
                value.ViewerDirection, "Confirmed", value.CapturedAt) { SourceSessionKey = value.SourceSessionKey };
        }
    }

    private void Mutate(Action action)
    {
        lock (sync)
        {
            RequireReady();
            try { using var lease = Writer(); Synchronize(); RequireReady(); action(); }
            catch (ShopRequestFailure) { throw; }
            catch (Exception failure) { error = "擺攤搜尋紀錄保存失敗；停止送出並保留原證據：" + failure.Message; throw; }
        }
    }

    private FileStream Writer() => new(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private void Reload(CancellationToken cancellation = default, Action<ShopStartupStageEvent>? startupStage = null)
    {
        attempts.Clear(); importKeys.Clear(); epoch = ""; error = ""; sequence = 0; imported = false; updatedAt = default; clearedAt = null;
        archiveTailSequence = 0; archiveTailHash = "";
        var archive = ShopStartupTrace.Run("owner.archive-read", startupStage,
            () => SearchedOwnerArchive.Read(root, cancellation), value => value?.Events.Count ?? 0);
        if (archive is not null)
        {
            ShopStartupTrace.Run("owner.archive-apply", startupStage, () =>
            {
                foreach (var item in archive.Events) { cancellation.ThrowIfCancellationRequested(); Apply(item); }
                return archive.Events.Count;
            }, count => count);
            archiveTailSequence = archive.TailSequence; archiveTailHash = archive.TailOriginalFileSHA;
        }
        var files = Directory.EnumerateFiles(root, "*.json").Order(StringComparer.Ordinal).Take(MaximumEvents + 1).ToArray();
        if (files.Length + archiveTailSequence > MaximumEvents) throw new InvalidDataException("持久搜尋紀錄超出事件界線。");
        ShopStartupTrace.Run("owner.json-tail-replay", startupStage, () =>
        {
            // Bound in-flight memory and file handles. Applying remains ordinal
            // and single-threaded so sequence, time, epoch and intent validation
            // are identical to the original ledger replay.
            const int batchSize = 128;
            for (int offset = 0; offset < files.Length; offset += batchSize)
            {
                cancellation.ThrowIfCancellationRequested();
                int length = Math.Min(batchSize, files.Length - offset);
                var batch = new SearchedOwnerEvent[length];
                Parallel.For(0, length, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellation }, index =>
                    batch[index] = Read<SearchedOwnerEvent>(files[offset + index], 128 * 1024));
                for (int index = 0; index < length; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var item = batch[index];
                    if (Path.GetFileName(files[offset + index]) != Name(item.Sequence)) throw new InvalidDataException("搜尋紀錄檔名不符。");
                    Apply(item);
                }
            }
            return files.Length;
        }, count => count);
        ShopStartupTrace.Run("owner.validate-head", startupStage, ValidateHead);
        foreach(var release in ManualLockReleaseStore.Read(Path.GetDirectoryName(root)!))
        {
            if(!attempts.TryGetValue(LiveKey(release.RequestId),out var item)||item.Source!=release.Source||item.Owner?.Id!=release.OwnerId||item.Direction!=release.Direction)
                throw new InvalidDataException("人工解除缺少相符店主意圖。");
            if(item.State=="Uncertain")attempts[LiveKey(release.RequestId)]=item with{State="ManualReleased"};
        }
    }

    // Common page operations validate the head and read only a changed tail;
    // they do not reread thousands of immutable legacy/history files per page.
    private void Synchronize()
    {
        if (sequence == 0 || File.Exists(Path.Combine(root, Name(sequence + 1)))) { Reload(); return; }
        var head = Read<SearchedOwnerHead>(Path.Combine(root, "head.checkpoint"), 4096);
        if (head.Sequence != sequence) { Reload(); return; }
        ValidateHead();
    }

    private void ValidateHead()
    {
        string path = Path.Combine(root, "head.checkpoint");
        if (!File.Exists(path))
        {
            if (sequence > 1) throw new InvalidDataException("搜尋紀錄缺少持久尾端校驗；未放寬去重。");
            return; // first Initialize event may precede its first checkpoint
        }
        var head = Read<SearchedOwnerHead>(path, 4096);
        if (head.Sequence is < 1 || head.Sequence < archiveTailSequence || head.Sequence > sequence || head.Sha256 is not { Length: 64 }
            || !string.Equals((head.Sequence == archiveTailSequence ? archiveTailHash : FileHash(Path.Combine(root, Name(head.Sequence)))), head.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("搜尋紀錄尾端缺失或內容不符；未放寬去重。");
    }

    private void Append(SearchedOwnerEvent value)
    {
        if (sequence >= MaximumEvents) throw new InvalidDataException("持久搜尋紀錄已達事件上限。");
        var now = DateTimeOffset.UtcNow;
        var item = value with { Sequence = sequence + 1, At = now > updatedAt ? now : updatedAt.AddTicks(1) };
        Validate(item);
        string path = Path.Combine(root, Name(item.Sequence)), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (JsonSerializer.SerializeToUtf8Bytes(item, HubProtocol.Json).Length > 128 * 1024) throw new InvalidDataException("搜尋匯入批次超過大小限制。");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(file, item, HubProtocol.Json); file.Flush(true); }
        File.Move(temporary, path, false);
        string checkpoint = Path.Combine(root, "head.checkpoint"), checkpointTemp = checkpoint + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(checkpointTemp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(file, new SearchedOwnerHead(item.Sequence, FileHash(path)), HubProtocol.Json); file.Flush(true); }
        File.Move(checkpointTemp, checkpoint, true);
        Apply(item);
    }

    private void AppendCommittedCompletionBatch(SearchedOwnerEvent[] values)
    {
        if (values is not [{ Kind: "Settled", State: "Sent" }, { Kind: "Confirmed", State: "Confirmed" }]
            || values[0].Key != values[1].Key || sequence > MaximumEvents - values.Length)
            throw new InvalidDataException("單頁批次完成範圍無效。");
        string? lastPath = null;
        foreach (var value in values)
        {
            var now = DateTimeOffset.UtcNow;
            var item = value with { Sequence = sequence + 1, At = now > updatedAt ? now : updatedAt.AddTicks(1) };
            Validate(item);
            string path = Path.Combine(root, Name(item.Sequence)), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(item, HubProtocol.Json);
            if (bytes.Length > 128 * 1024) throw new InvalidDataException("搜尋事件超過大小限制。");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, false);
            // Mutate holds sync + writer lease. Failure before the final head
            // leaves this writer in Error; reload validates each durable tail event
            // under the unchanged V95 replay/head rules. It never replays native.
            Apply(item); lastPath = path;
        }
        string checkpoint = Path.Combine(root, "head.checkpoint"), checkpointTemp = checkpoint + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(checkpointTemp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(file, new SearchedOwnerHead(sequence, FileHash(lastPath!)), HubProtocol.Json); file.Flush(true); }
        File.Move(checkpointTemp, checkpoint, true);
    }

    private void Apply(SearchedOwnerEvent item)
    {
        Validate(item);
        switch (item.Kind)
        {
            case "Initialize": epoch = item.Epoch; break;
            case "Clear": epoch = item.Epoch; clearedAt = item.At; break;
            case "ImportComplete": imported = true; break;
            case "ImportBatch":
                foreach (var entry in item.ImportedEntries!)
                {
                    var persisted = entry with { Sequence = item.Sequence, At = item.At };
                    importKeys.Add(persisted.Key); attempts.Add(persisted.Key, persisted);
                }
                break;
            case "Import": importKeys.Add(item.Key); attempts.Add(item.Key, item); break;
            case "Intent": attempts.Add(item.Key, item); break;
            case "Settled": case "Confirmed": attempts[item.Key] = item; break;
        }
        sequence = item.Sequence; updatedAt = item.At;
    }

    private void Validate(SearchedOwnerEvent item)
    {
        if (item.Schema != 1 || item.Sequence != sequence + 1 || item.At == default || item.At < updatedAt
            || !Guid.TryParseExact(item.Epoch, "N", out _)) throw new InvalidDataException("搜尋紀錄版本／順序／時間無效。");
        if (item.Kind == "ImportBatch")
        {
            if (sequence == 0 || imported || item.Epoch != epoch || item.ImportedEntries is not { Length: > 0 and <= 64 } entries
                || item.Key != "" || item.RequestId != "" || item.Source is not null || item.SourceSessionKey != "" || item.SearchedAt != default
                || item.Owner is not null || item.Direction != "" || item.State != "") throw new InvalidDataException("搜尋匯入批次格式無效。");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry.Kind != "Import" || entry.Sequence != 0 || entry.At != default || !keys.Add(entry.Key)) throw new InvalidDataException("搜尋匯入批次成員無效或重複。");
                Validate(entry with { Sequence = item.Sequence, At = item.At });
            }
            return;
        }
        if (item.ImportedEntries is not null) throw new InvalidDataException("非批次事件包含匯入成員。");
        bool control = item.Kind is "Initialize" or "Clear" or "ImportComplete";
        if (control)
        {
            if (item.Key != "" || item.RequestId != "" || item.Source is not null || item.SourceSessionKey != "" || item.SearchedAt != default || item.Owner is not null || item.Direction != "" || item.State != ""
                || item.Kind == "Initialize" && sequence != 0 || item.Kind == "ImportComplete" && (sequence == 0 || imported || item.Epoch != epoch)
                || item.Kind == "Clear" && (!imported || item.Epoch == epoch)) throw new InvalidDataException("搜尋紀錄控制事件無效。");
            return;
        }
        if (sequence == 0 || item.Kind is not ("Intent" or "Import" or "Settled" or "Confirmed") || item.Key.Length != 64 || !item.Key.All(Uri.IsHexDigit)
            || item.RequestId is not { Length: > 0 and <= 128 } || item.RequestId.Any(char.IsControl)
            || item.SourceSessionKey is not { Length: > 0 and <= 2048 }
            || item.SearchedAt == default
            || item.Direction is not (ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell)
            || item.Owner is not { Id: > 0 and <= int.MaxValue, Position: { X: > 0 and <= 1_000_000, Y: > 0 and <= 1_000_000 } } owner
            || owner.ObservedAt == default || owner.Name is null || owner.Name.Length > 1024 || owner.LocalIdentity is not { Length: > 0 and <= 256 }
            || owner.MapName?.Length > 1024 || owner.VisibilityEvidence is null || owner.VisibilityEvidence.Length > 2048)
            throw new InvalidDataException("搜尋紀錄玩家／頁面／請求欄位無效。");
        if (item.Source is { } source && (source.ProcessId <= 0 || source.CreationFileTime <= 0 || source.SessionKey is not { Length: > 0 and <= 2048 }
            || source.SessionKey != item.SourceSessionKey || source.ImagePath is not { Length: > 0 and <= 32768 }
            || source.DiskSha256 is not { Length: 64 } || !source.DiskSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("搜尋紀錄來源無效。");
        if (item.Kind is "Intent" or "Import")
        {
            if (item.Epoch != epoch || attempts.ContainsKey(item.Key) || item.Kind == "Intent" && (!imported || item.State != "Uncertain" || item.Source is null)
                || item.Kind == "Import" && (imported || item.State is not ("Sent" or "Confirmed" or "Uncertain") || item.State == "Uncertain" && item.Source is null))
                throw new InvalidDataException("搜尋紀錄意圖／匯入順序無效。");
        }
        else
        {
            if (!imported || !attempts.TryGetValue(item.Key, out var prior) || prior.Epoch != item.Epoch || prior.RequestId != item.RequestId
                || prior.Source != item.Source || prior.SourceSessionKey != item.SourceSessionKey || prior.SearchedAt != item.SearchedAt || prior.Owner != item.Owner || prior.Direction != item.Direction
                || item.Kind == "Settled" && (prior.State != "Uncertain" || item.State is not ("Sent" or "NotSent"))
                || item.Kind == "Confirmed" && (prior.State != "Sent" || item.State != "Confirmed"))
                throw new InvalidDataException("搜尋紀錄結束／確認證據無法對回原意圖。");
        }
    }

    private void RequireHealthy() { if (error.Length != 0) throw Failure(error); }
    private void RequireReady() { RequireHealthy(); if (!imported) throw Failure("首次歷史匯入尚未完成；未開放重送。"); }
    private static ShopRequestFailure Failure(string detail) => new(ShopFailure.Global("SearchHistoryUnavailable", detail));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FileHash(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string LiveKey(string requestId) => Hash("request:" + requestId);
    private static string Name(int value) => value.ToString("D10", CultureInfo.InvariantCulture) + ".json";
    private static T Read<T>(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 || file.Length > maximum) throw new InvalidDataException("持久搜尋紀錄大小無效。");
        return JsonSerializer.Deserialize<T>(file, HubProtocol.Json) ?? throw new InvalidDataException("持久搜尋紀錄為空。");
    }
}
