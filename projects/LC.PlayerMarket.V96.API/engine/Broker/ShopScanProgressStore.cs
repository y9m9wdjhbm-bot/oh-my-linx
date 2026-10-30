using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryHub.Service;

internal sealed record SavedScanPlan([property: JsonRequired] string PlanId, [property: JsonRequired] SourceIdentity Source,
    [property: JsonRequired] string OriginalSnapshotId, [property: JsonRequired] ulong SelfId,
    [property: JsonRequired] ShopPoint SelfPosition, [property: JsonRequired] DateTimeOffset CreatedAt,
    [property: JsonRequired] MarketOwner[] Owners);
internal sealed record SavedScanPage(ulong OwnerId, string OwnerIdentity, string Direction);
internal sealed record SavedScanResetMarker(
    [property: JsonRequired] int Schema,
    [property: JsonRequired] string ResetId,
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string? PreviousPlanId,
    [property: JsonRequired] DateTimeOffset CutoffPlanCreatedAt,
    [property: JsonRequired] DateTimeOffset At);
internal sealed record ShopHistoricalPageEvidence(string EvidenceId, string RequestId, SourceIdentity? Source,
    MarketOwner Owner, string Direction, string State, DateTimeOffset At)
{
    internal string SourceSessionKey { get; init; } = "";
}
internal sealed record ScanProgressEvent
{
    [JsonRequired] public int Schema { get; init; } = 1;
    [JsonRequired] public int Sequence { get; init; }
    [JsonRequired] public string PlanId { get; init; } = "";
    [JsonRequired] public string Kind { get; init; } = "";
    [JsonRequired] public string RequestId { get; init; } = "";
    [JsonRequired] public ulong OwnerId { get; init; }
    [JsonRequired] public string OwnerIdentity { get; init; } = "";
    [JsonRequired] public string Direction { get; init; } = "";
    [JsonRequired] public DateTimeOffset At { get; init; }
    [JsonRequired] public bool NativeEntered { get; init; }
    [JsonRequired] public bool NativeReturned { get; init; }
    [JsonRequired] public bool ResponseVerified { get; init; }
    [JsonRequired] public bool SessionBlocked { get; init; }
    [JsonRequired] public string Detail { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativePostCodeRecoveryEvidence? PostCodeRecovery { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeGuardRejectedRecoveryEvidence? GuardRejectedRecovery { get; init; }
}
internal sealed record ScanIntentEvidence([property: JsonRequired] SourceIdentity Source,
    [property: JsonRequired] string PlanId, [property: JsonRequired] string RequestId,
    [property: JsonRequired] DateTimeOffset At, [property: JsonRequired] bool Settled,
    [property: JsonRequired] bool Entered, [property: JsonRequired] bool Returned);

/// <summary>
/// Append-only plan and lifecycle evidence. A response's default false flags can
/// never erase a durable native intent. A process-wide file lease serializes runs
/// using the same state root, including separate application instances.
/// </summary>
internal sealed partial class ShopScanProgressStore
{
    private const int MaximumEvents = 8192;
    private readonly string root;
    private readonly object sync = new();
    private SavedScanPlan? plan;
    private readonly Dictionary<SavedScanPage, PageState> pages = [];
    private readonly Dictionary<string, SavedScanPage> requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScanIntentEvidence> unresolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScanIntentEvidence> knownIntents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScanIntentEvidence> knownSettlements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> settlementEvents = new(StringComparer.Ordinal);
    private string? error;
    private int sequence;
    private DateTimeOffset updatedAt;
    private DateTimeOffset lastPlanCreatedAt, lastResetAt;
    private string? lastPlanId;
    internal SourceIdentity? Source { get { lock (sync) return plan?.Source; } }
    internal string? Error { get { lock (sync) return error; } }
    internal string? PlanId { get { lock (sync) return plan?.PlanId; } }
    internal bool BlocksSource(SourceIdentity source)
    { lock (sync) return error is not null || unresolved.Values.Any(item => SameProcess(item.Source, source))
        || plan is not null && SameProcess(plan.Source, source) && pages.Values.Any(page => page.State == "Uncertain"); }

    internal ShopScanProgressStore(string directory, Action<ShopStartupStageEvent>? startupStage = null)
    {
        root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        ReloadCore(settleIndices:true, readConcurrency:8, startupStage:startupStage);
    }

    // Historical replay is deliberately separate from Reload: it never creates
    // directories or completes any old source-intent index on disk.
    private ShopScanProgressStore(string directory, bool historicalReplay) => root = Path.GetFullPath(directory);

    internal static ShopHistoricalPageEvidence[] ReadHistoricalEvidence(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var replay = new ShopScanProgressStore(directory, historicalReplay: true);
        replay.LoadIntents();
        string indexDirectory = Path.Combine(directory, "source-intents");
        var resolvedFiles = Directory.Exists(indexDirectory)
            ? Directory.EnumerateFiles(indexDirectory, "*.resolved.json").Take(32769).ToArray() : [];
        if (resolvedFiles.Length > 32768) throw new InvalidDataException("歷史結束索引超出匯入界線。");
        foreach (string file in resolvedFiles)
        {
            var resolved = Read<ScanIntentEvidence>(file, 16 * 1024);
            ValidateIntent(resolved, settled: true);
            if (Path.GetFullPath(file) != replay.IntentPath(resolved.RequestId, true)
                || !replay.knownSettlements.TryGetValue(resolved.RequestId, out var matched) || matched != resolved)
                throw new InvalidDataException("歷史結束索引缺少相符原生意圖；未忽略可能已送出證據。");
        }
        var output = new List<ShopHistoricalPageEvidence>();
        var seenPlans = new HashSet<string>(StringComparer.Ordinal);
        var plans = Directory.EnumerateFiles(directory, "*.plan.json").Order(StringComparer.Ordinal).Take(32769).ToArray();
        if (plans.Length > 32768) throw new InvalidDataException("歷史搜尋計畫超出匯入界線。");
        foreach (string path in plans)
        {
            var stored = Read<SavedScanPlan>(path, 512 * 1024); ValidatePlan(stored);
            if (Path.GetFileName(path) != PlanFileName(stored) || !seenPlans.Add(stored.PlanId))
                throw new InvalidDataException("歷史計畫檔名或識別不唯一。");
            replay.Install(stored);
            var verified = new HashSet<string>(StringComparer.Ordinal);
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            string events = replay.EventsDirectory(stored.PlanId);
            var files = Directory.Exists(events) ? Directory.EnumerateFiles(events, "*.json").Order(StringComparer.Ordinal).Take(MaximumEvents + 1).ToArray() : [];
            if (files.Length > MaximumEvents) throw new InvalidDataException("歷史計畫事件超出界線。");
            foreach (string file in files)
            {
                var item = Read<ScanProgressEvent>(file, 64 * 1024);
                if (Path.GetFileName(file) != EventFileName(item.Sequence)) throw new InvalidDataException("歷史事件檔名不符。");
                replay.Apply(item);
                if (item.Kind == "Result" && item.ResponseVerified) verified.Add(item.RequestId);
                if (item.Kind == "Result" && item.SessionBlocked) blocked.Add(item.RequestId);
                if (item.Kind is "NativePostCodeRecovered" or "NativeGuardRejectedRecovered") blocked.Remove(item.RequestId);
            }
            replay.ApplyManualReleases();
            foreach (var resolved in replay.knownSettlements.Values.Where(item => item.PlanId == stored.PlanId))
                if (resolved.Source != stored.Source || !replay.requests.ContainsKey(resolved.RequestId)
                    || !replay.settlementEvents.TryGetValue(resolved.RequestId, out bool returned) || returned != resolved.Returned)
                    throw new InvalidDataException("歷史已結束索引缺少完整頁面證據；不推定可重送。");
            foreach (var intent in replay.knownIntents.Values.Where(item => item.PlanId == stored.PlanId))
            {
                if (intent.Source != stored.Source || !replay.requests.TryGetValue(intent.RequestId, out var page))
                    throw new InvalidDataException("歷史原生意圖無法對回來源及玩家頁面。");
                if(replay.pages[page].State=="ManualReleased")continue; // audited override; never import as unsent/success
                bool settled = replay.settlementEvents.TryGetValue(intent.RequestId, out bool returned);
                if (settled && !returned && !blocked.Contains(intent.RequestId)) continue; // positive no-entry is not searched
                string state = !settled || blocked.Contains(intent.RequestId) ? "Uncertain" : verified.Contains(intent.RequestId) ? "Confirmed" : "Sent";
                var owner = stored.Owners.Single(item => item.Id == page.OwnerId && item.LocalIdentity == page.OwnerIdentity);
                output.Add(new("plan:" + stored.PlanId + ":" + intent.RequestId, intent.RequestId, intent.Source, owner, page.Direction, state, intent.At));
            }
        }
        if (replay.knownIntents.Values.Any(item => !seenPlans.Contains(item.PlanId)))
            throw new InvalidDataException("歷史原生意圖缺少原始計畫；未忽略安全證據。");
        return output.ToArray();
    }

    internal IDisposable AcquireRunLease() => new FileStream(Path.Combine(root, "run.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal static ShopScanProgressStore OpenReadOnly(string directory)
    {var value=new ShopScanProgressStore(directory,historicalReplay:true);value.Reload(settleIndices:false);return value;}
    internal void Reload(bool settleIndices=true) => ReloadCore(settleIndices, readConcurrency:4);
    private void ReloadCore(bool settleIndices, int readConcurrency, Action<ShopStartupStageEvent>? startupStage = null)
    {
        lock (sync)
        {
            plan = null; pages.Clear(); requests.Clear(); unresolved.Clear(); knownIntents.Clear(); knownSettlements.Clear();
            settlementEvents.Clear(); sequence = 0; error = null;
            lastPlanCreatedAt = lastResetAt = default; lastPlanId = null;
            try
            {
                // Names begin with UTC ticks. A malformed newest plan cannot make
                // us silently fall back to an older, more permissive checkpoint.
                string? latest = Directory.EnumerateFiles(root, "*.plan.json")
                    .OrderDescending(StringComparer.Ordinal).FirstOrDefault();
                ShopStartupTrace.Run("scan.source-intents", startupStage, () => LoadIntents(readConcurrency, startupStage));
                var reset = ReadLatestResetMarker();
                if (latest is null) { if (reset is not null) ApplyResetMarker(reset); return; }
                var loaded = Read<SavedScanPlan>(latest, 512 * 1024);
                ValidatePlan(loaded);
                string expectedName = PlanFileName(loaded);
                if (Path.GetFileName(latest) != expectedName) throw new InvalidDataException("搜尋計畫檔名與內容不符。");
                Install(loaded);
                string events = EventsDirectory(loaded.PlanId);
                var files = Directory.Exists(events)
                    ? Directory.EnumerateFiles(events, "*.json").Order(StringComparer.Ordinal).Take(MaximumEvents + 1).ToArray() : [];
                if (files.Length > MaximumEvents) throw new InvalidDataException("搜尋進度事件數超出上限。");
                ShopStartupTrace.Run("scan.current-plan-events", startupStage, () =>
                {
                    foreach (string file in files)
                    {
                        var item = Read<ScanProgressEvent>(file, 64 * 1024);
                        if (Path.GetFileName(file) != EventFileName(item.Sequence)) throw new InvalidDataException("搜尋進度事件檔名不符。");
                        Apply(item);
                    }
                    return files.Length;
                }, count => count);
                foreach (var settlement in knownSettlements.Values.Where(item => item.PlanId == loaded.PlanId))
                    if (settlement.Source != loaded.Source || !requests.ContainsKey(settlement.RequestId)
                        || !settlementEvents.TryGetValue(settlement.RequestId, out bool returned) || returned != settlement.Returned)
                        throw new InvalidDataException("已知原生結束索引缺少相符頁面事件；進度尾端可能遺失，禁止重送。");
                // A committed settlement event is positive evidence even if a
                // crash interrupted its separate source-index update. Resolve
                // only our own index; never touch native/session pending files.
                foreach (var page in pages.Values.Where(page => page.Settled && page.RequestId is not null))
                    if (settleIndices && unresolved.TryGetValue(page.RequestId!, out var intent) && intent.PlanId == loaded.PlanId && intent.Source == loaded.Source)
                        SettleIndex(intent, page.SettledReturned);
                ApplyManualReleases();
                foreach (var intent in unresolved.Values.Where(item => item.PlanId == loaded.PlanId && item.Source == loaded.Source))
                    if (requests.TryGetValue(intent.RequestId, out var key) && pages[key].RequestId == intent.RequestId)
                        pages[key] = pages[key] with { Intent = true, State = "Uncertain",
                            Detail = "原生意圖已保存，結果尚未完整核對；不會將中斷頁面重新當成未送出。" };
                if (reset is not null) ApplyResetMarker(reset);
            }
            catch (Exception failure) when (failure is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException || failure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Count > 0 && aggregate.Flatten().InnerExceptions.All(inner => inner is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException))
            { error = "保存的搜尋進度無法完整核對；未自動重建或重送：" + failure.Message; }
        }
    }

    internal SavedScanPlan StartNew(SourceIdentity source, ShopTargetSnapshot targets, bool forceDispatch = false, ShopCommandResponse? responseEnvelope = null)
    {
        lock (sync)
        {
            RequireHealthy();
            if (BlocksSource(source))
                throw Failure("前次搜尋仍有原生結果未確認的頁面；重新搜尋不能解除此來源的未完成鎖。");
            ShopTargetPolicy.ValidateStored(targets, source);
            if (!forceDispatch && !targets.CanDispatch) throw Failure("請先完成本次手動刷新；歷史名單不能開始新搜尋。");
            var next = new SavedScanPlan(Guid.NewGuid().ToString("N"), source, targets.SnapshotId,
                targets.SelfId, targets.SelfPosition!, NextTimestamp(lastPlanCreatedAt > lastResetAt ? lastPlanCreatedAt : lastResetAt), targets.Owners.Where(ShopEligibilityPolicy.CanScan).ToArray());
            ValidatePlan(next);
            if (JsonSerializer.SerializeToUtf8Bytes(next, HubProtocol.Json).Length > 512 * 1024)
                throw Failure("完整搜尋計畫超過既有 512 KiB 保存界線；未寫入或啟動搜尋，舊計畫保留。");
            ShopTargetSnapshotBudget.RequirePlanFits(targets, next, responseEnvelope ?? new());
            WriteNew(Path.Combine(root, PlanFileName(next)), next);
            Install(next);
            return next;
        }
    }

    internal SavedScanResetMarker CreateResetMarker(string resetId, string requestId)
    {
        lock (sync)
        {
            RequireHealthy();
            var previous = updatedAt > lastResetAt ? updatedAt : lastResetAt;
            var marker = new SavedScanResetMarker(1, resetId, requestId, lastPlanId, lastPlanCreatedAt, NextTimestamp(previous));
            ValidateResetMarker(marker);
            return marker;
        }
    }

    // Deactivate the visible checkpoint only. Global source intents and every
    // old plan/event remain available for evidence and source blocking.
    internal void ResetActivePlan(SavedScanResetMarker marker)
    {
        lock (sync)
        {
            RequireHealthy(); ValidateResetMarker(marker);
            string path = Path.Combine(root, ResetFileName(marker));
            if (File.Exists(path))
            {
                if (Read<SavedScanResetMarker>(path, 16 * 1024) != marker)
                    throw new InvalidDataException("重設標記內容不同；未覆蓋現有證據。");
                if (marker.At < lastResetAt) return;
            }
            else
            {
                if (marker.At < lastResetAt) throw new InvalidDataException("不能追加過期搜尋重設。");
                ValidateResetPlanBoundary(marker);
                WriteNew(path, marker);
            }
            ApplyResetMarker(marker);
        }
    }

    private SavedScanResetMarker? ReadLatestResetMarker()
    {
        string? path = Directory.EnumerateFiles(root, "*.reset.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        if (path is null) return null;
        var marker = Read<SavedScanResetMarker>(path, 16 * 1024); ValidateResetMarker(marker);
        if (Path.GetFileName(path) != ResetFileName(marker)) throw new InvalidDataException("搜尋重設檔名與內容不符。");
        return marker;
    }

    private void ValidateResetPlanBoundary(SavedScanResetMarker marker)
    {
        if (lastPlanCreatedAt < marker.CutoffPlanCreatedAt
            || lastPlanCreatedAt == marker.CutoffPlanCreatedAt && lastPlanId != marker.PreviousPlanId)
            throw new InvalidDataException("搜尋重設缺少相符原計畫；未略過損失的證據。");
    }

    private void ApplyResetMarker(SavedScanResetMarker marker)
    {
        ValidateResetPlanBoundary(marker);
        lastResetAt = marker.At;
        if (plan is not null && plan.CreatedAt > marker.CutoffPlanCreatedAt) return;
        plan = null; pages.Clear(); requests.Clear(); settlementEvents.Clear(); sequence = 0; updatedAt = marker.At;
    }

    internal static void ValidateResetMarker(SavedScanResetMarker marker)
    {
        if (marker.Schema != 1 || !Guid.TryParseExact(marker.ResetId, "N", out _)
            || marker.RequestId is not { Length: > 0 and <= 128 } || marker.RequestId.Any(char.IsControl)
            || marker.At == default || marker.At <= marker.CutoffPlanCreatedAt
            || marker.PreviousPlanId is null && marker.CutoffPlanCreatedAt != default
            || marker.PreviousPlanId is not null && (!Guid.TryParseExact(marker.PreviousPlanId, "N", out _) || marker.CutoffPlanCreatedAt == default))
            throw new InvalidDataException("搜尋重設標記無效。");
    }

    private static string ResetFileName(SavedScanResetMarker marker) => marker.At.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)
        + "-" + marker.ResetId + ".reset.json";

    internal SavedScanPage[] Continue(string? expectedPlanId, SourceIdentity source, ShopTargetSnapshot targets, bool allowEmpty = false, bool forceDispatch = false)
    {
        lock (sync)
        {
            RequireHealthy();
            if (plan is null || string.IsNullOrWhiteSpace(expectedPlanId) || plan.PlanId != expectedPlanId)
                throw Failure("保存的搜尋計畫已不同；未接續其他計畫。");
            if (plan.Source != source) throw Failure("LC 完整來源已不同；舊進度保留供查看，不能沿用舊玩家 ID 接續。");
            if (!forceDispatch && !targets.CanDispatch || targets.SourceSessionKey != source.SessionKey)
                throw Failure("請先完成本次手動刷新；載入歷史進度不會恢復送出資格。");
            if (BlocksSource(source))
                throw Failure("保存的搜尋含未確認原生結果；接續不能解除未完成鎖。");
            var pending = PendingPages(targets, forceDispatch);
            if (pending.Length == 0 && !allowEmpty) throw Failure("目前名單沒有可接續的未送出頁；已搜尋頁不會重送，原店主不在場或身分不同時仍保留未送出。");
            return pending;
        }
    }

    internal SavedScanPage[] PendingPages(ShopTargetSnapshot targets, bool forceDispatch = false)
    {
        lock (sync)
        {
            RequireHealthy();
            if (plan is null || targets.SourceSessionKey != plan.Source.SessionKey) return [];
            return plan.Owners.SelectMany(owner => Directions.Select(direction => new SavedScanPage(owner.Id, owner.LocalIdentity, direction)))
                .Where(key => pages[key].State == "NotSent" && targets.Owners.Count(owner => owner.Id == key.OwnerId
                    && owner.LocalIdentity == key.OwnerIdentity && ShopEligibilityPolicy.CanScan(owner)) == 1).ToArray();
        }
    }

    internal int SkipKnownHistory(Func<ulong, string, bool> shouldSkip)
    {
        lock (sync)
        {
            RequireHealthy();
            var selected = pages.Where(item => item.Value.State == "NotSent" && shouldSkip(item.Key.OwnerId, item.Key.Direction)).Select(item => item.Key).ToArray();
            foreach (var page in selected) Append(page, "HistorySkipped", "history-" + Guid.NewGuid().ToString("N"),
                "本機持久搜尋紀錄已包含此玩家 ID 的本頁；略過，不送出，也不把本計畫頁面標為新搜尋成功。");
            return selected.Length;
        }
    }

    internal void BeginAttempt(SavedScanPage page, string requestId) => Append(page, "Attempt", requestId, "本頁正在準備；尚未持久化原生送出意圖。");
    internal void NativeIntent(string requestId)
    {
        lock (sync)
        {
            if (!requests.ContainsKey(requestId)) return;
            RequireHealthy();
            var intent = new ScanIntentEvidence(plan!.Source, plan.PlanId, requestId, NextTimestamp(updatedAt), false, false, false);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "source-intents"));
                WriteNew(IntentPath(requestId, false), intent);
                knownIntents.Add(requestId, intent);
                unresolved.Add(requestId, intent);
                AppendKnown(requestId, "NativeIntent", "已持久化原生送出意圖；返回與清理尚待確認。");
            }
            catch (Exception failure) { error = "原生意圖保存失敗；未再啟動原生請求：" + failure.Message; throw; }
        }
    }
    internal void NativeSettled(string requestId, bool entered, bool returned, string detail)
    {
        lock (sync)
        {
            if (!requests.ContainsKey(requestId)) return;
            AppendKnown(requestId, "NativeSettled", detail, entered, returned);
            try { if (unresolved.TryGetValue(requestId, out var intent)) SettleIndex(intent, returned); }
            catch (Exception failure) { error = "已知原生結果的索引保存失敗；保留事件及鎖：" + failure.Message; throw; }
        }
    }
    internal void Result(string requestId, ShopCommandResponse response) => AppendKnown(requestId, "Result", response.Detail,
        response.NativeEntered, response.NativeReturned, response.ResponseVerified, response.SessionBlocked);

    internal ShopSavedScanProgress? View(SourceIdentity? current, ShopTargetSnapshot? targets, bool running, bool sessionBlocked, bool forceDispatch = false)
    {
        lock (sync)
        {
            if (plan is null) return error is null ? null : new() { ContinueBlockReason = error };
            var owners = plan.Owners.Select(owner =>
            {
                var buy = pages[new(owner.Id, owner.LocalIdentity, ShopPageModes.ViewerBuy)];
                var sell = pages[new(owner.Id, owner.LocalIdentity, ShopPageModes.ViewerSell)];
                return new ShopOwnerSearchProgress { OwnerId = owner.Id, OwnerName = owner.Name,
                    BuyState = buy.State, SellState = sell.State, BuyDetail = PublicDetail(buy.Detail), SellDetail = PublicDetail(sell.Detail),
                    UpdatedAt = buy.At > sell.At ? buy.At : sell.At };
            }).ToArray();
            int remaining = pages.Values.Count(page => page.State == "NotSent"), uncertain = pages.Values.Count(page => page.State == "Uncertain");
            bool matching = current is not null && current == plan.Source && targets is not null && (forceDispatch || targets.CanDispatch)
                && targets.SourceSessionKey == plan.Source.SessionKey && targets.SelfId == plan.SelfId && targets.RefreshedAt != default;
            int available = error is null && matching ? PendingPages(targets!, forceDispatch).Length : 0;
            string code = error is not null ? "ProgressUnavailable" : running ? "Busy" : sessionBlocked ? "SessionBlocked"
                : uncertain != 0 ? "UncertainPages" : current is null ? "SourceUnavailable" : current != plan.Source ? "SourceMismatch"
                : !matching ? "RefreshRequired" : remaining == 0 ? "Complete" : available == 0 ? "NoAvailableOwners" : "Ready";
            string reason = code switch
            {
                "ProgressUnavailable" => error!, "Busy" => "搜尋工作仍在執行或暫停；請使用原工作的繼續／停止控制。",
                "SessionBlocked" or "UncertainPages" => "尚有原生結果或清理未確認；接續與重新搜尋均不能解除未完成鎖。",
                "SourceUnavailable" or "SourceMismatch" => "LC 完整來源尚未符合保存計畫；舊進度僅供查看。",
                "RefreshRequired" => "請先成功手動刷新周遭，再按接續搜尋。",
                "Complete" => "本計畫已無未送出頁；既有搜尋紀錄略過頁另計，不重送。",
                "NoAvailableOwners" => "未送出頁的原店主目前不在可搜尋名單、未確認擺攤或本機身分不同；保留原進度。",
                _ => ""
            };
            return new() { PlanId = plan.PlanId, SourceSessionKey = plan.Source.SessionKey, CreatedAt = plan.CreatedAt,
                UpdatedAt = updatedAt, TotalPages = pages.Count, SearchedPages = pages.Values.Count(page => page.State is "Sent" or "Confirmed"),
                ConfirmedPages = pages.Values.Count(page => page.State == "Confirmed"), RemainingPages = remaining,
                AvailablePages = available, DeferredPages = remaining - available,
                HistorySkippedPages = pages.Values.Count(page => page.State == "HistorySkipped"),
                UncertainPages = uncertain, CanContinue = code == "Ready", ContinueBlockCode = code, ContinueBlockReason = reason, Owners = owners };
        }
    }

    private void AppendKnown(string requestId, string kind, string detail, bool entered = false, bool returned = false,
        bool verified = false, bool blocked = false)
    {
        lock (sync)
        {
            // Direct single-shop commands are outside this saved scan plan.
            if (!requests.TryGetValue(requestId, out var page)) return;
            Append(page, kind, requestId, detail, entered, returned, verified, blocked);
        }
    }

    private void Append(SavedScanPage page, string kind, string requestId, string detail, bool entered = false,
        bool returned = false, bool verified = false, bool blocked = false)
    {
        lock (sync)
        {
            RequireHealthy();
            if (plan is null || sequence >= MaximumEvents) throw Failure("搜尋進度尚未建立或事件已達上限；未再送出。");
            var item = new ScanProgressEvent { Sequence = sequence + 1, PlanId = plan.PlanId, Kind = kind,
                RequestId = requestId, OwnerId = page.OwnerId, OwnerIdentity = page.OwnerIdentity, Direction = page.Direction,
                At = NextTimestamp(updatedAt), NativeEntered = entered, NativeReturned = returned, ResponseVerified = verified,
                SessionBlocked = blocked, Detail = detail.Length > 16000 ? detail[..16000] : detail };
            ValidateEvent(item);
            try
            {
                string directory = EventsDirectory(plan.PlanId); Directory.CreateDirectory(directory);
                WriteNew(Path.Combine(directory, EventFileName(item.Sequence)), item);
                Apply(item);
            }
            catch (Exception failure)
            {
                error = "搜尋進度保存失敗；停止後續送出，保留現有證據：" + failure.Message;
                throw;
            }
        }
    }

    private void Install(SavedScanPlan value)
    {
        plan = value; pages.Clear(); requests.Clear(); settlementEvents.Clear(); sequence = 0; updatedAt = value.CreatedAt;
        lastPlanId = value.PlanId; lastPlanCreatedAt = value.CreatedAt;
        foreach (var owner in value.Owners)
            foreach (string direction in Directions) pages.Add(new(owner.Id, owner.LocalIdentity, direction), new("NotSent", "尚未送出", value.CreatedAt));
    }

    private void ValidateEvent(ScanProgressEvent item)
    {
        if (plan is null || item.Schema != 1 || item.PlanId != plan.PlanId || item.Sequence != sequence + 1
            || item.At == default || item.At < plan.CreatedAt || item.RequestId is not { Length: > 0 and <= 128 }
            || item.RequestId.Any(char.IsControl) || item.Detail is null || item.Detail.Length > 16000
            || !pages.TryGetValue(new(item.OwnerId, item.OwnerIdentity, item.Direction), out var page))
            throw new InvalidDataException("搜尋進度事件的計畫／頁面／順序無效。");
        if (item.Kind != "NativeGuardRejectedRecovered" && item.GuardRejectedRecovery is not null)
            throw new InvalidDataException("Guard recovery proof belongs only to its exact event.");
        if (item.Kind != "NativePostCodeRecovered" && item.PostCodeRecovery is not null)
            throw new InvalidDataException("原生恢復收據不可附加到其他事件種類。");
        if (item.Kind == "HistorySkipped")
        {
            if (requests.ContainsKey(item.RequestId) || page.State != "NotSent" || page.Intent && !page.Settled
                || item.NativeEntered || item.NativeReturned || item.ResponseVerified || item.SessionBlocked)
                throw new InvalidDataException("歷史略過不能取代已送出或未明的頁面。");
        }
        else if (item.Kind == "Attempt")
        {
            if (requests.ContainsKey(item.RequestId) || page.State != "NotSent" || item.NativeEntered || item.NativeReturned || item.ResponseVerified || item.SessionBlocked)
                throw new InvalidDataException("已送出／未確認頁面不可建立重複嘗試。");
        }
        else if (!requests.TryGetValue(item.RequestId, out var existing) || existing != new SavedScanPage(item.OwnerId, item.OwnerIdentity, item.Direction)
            || page.RequestId != item.RequestId)
            throw new InvalidDataException("搜尋進度事件不屬於目前頁面嘗試。");
        else if (item.Kind == "NativeIntent")
        {
            if (page.Intent || page.Result || page.State != "NotSent" || item.NativeEntered || item.NativeReturned || item.ResponseVerified || item.SessionBlocked)
                throw new InvalidDataException("原生意圖事件重複或順序無效。");
            if (!knownIntents.TryGetValue(item.RequestId, out var intent) || intent.PlanId != plan.PlanId || intent.Source != plan.Source)
                throw new InvalidDataException("原生意圖頁面事件缺少相符來源索引；禁止重送。");
        }
        else if (item.Kind == "NativeSettled")
        {
            if (!page.Intent || page.Settled || page.Result || item.NativeEntered != item.NativeReturned || item.SessionBlocked || item.ResponseVerified)
                throw new InvalidDataException("原生返回／清理證據不完整或順序無效。");
        }
        else if (item.Kind == "NativeGuardRejected")
        {
            if (!page.Intent || page.Settled || page.Result || item.NativeEntered || item.NativeReturned || item.SessionBlocked || item.ResponseVerified)
                throw new InvalidDataException("Guard refusal settlement order or flags differ.");
        }
        else if (item.Kind == "NativeGuardRejectedRecovered")
        {
            if (!page.Intent || page.Settled || !page.Result || page.State != "Uncertain" || item.NativeEntered || item.NativeReturned
                || item.SessionBlocked || item.ResponseVerified || item.GuardRejectedRecovery is null
                || item.GuardRejectedRecovery.OriginalSequence >= item.Sequence)
                throw new InvalidDataException("Guard reconciliation requires the original blocked result.");
            ValidateGuardRecovery(item.GuardRejectedRecovery, new(item.OwnerId,item.OwnerIdentity,item.Direction));
        }
        else if (item.Kind == "NativePostCodeRecovered")
        {
            if (!page.Intent || page.Settled || !page.Result || page.State != "Uncertain"
                || !item.NativeEntered || !item.NativeReturned || item.SessionBlocked || item.ResponseVerified
                || item.PostCodeRecovery is null)
                throw new InvalidDataException("延後原生檢核恢復必須接在相符未結清原結果之後。");
            ShopPostCodeRecoveryProof.ValidateEvidence(item.PostCodeRecovery, plan.Source, item.RequestId,
                item.OwnerId, item.OwnerIdentity, item.Direction);
        }
        else if (item.Kind == "Result")
        {
            if (page.Result || item.ResponseVerified && (!page.Settled || !item.NativeEntered || !item.NativeReturned || item.SessionBlocked)
                || !page.Intent && (item.NativeEntered || item.NativeReturned || item.ResponseVerified)
                || page.Settled && !page.SettledReturned && (item.NativeEntered || item.NativeReturned || item.ResponseVerified))
                throw new InvalidDataException("搜尋結果與原生意圖／返回證據矛盾。");
        }
        else throw new InvalidDataException("未知搜尋進度事件種類。");
    }

    private void Apply(ScanProgressEvent item)
    {
        ValidateEvent(item);
        var key = new SavedScanPage(item.OwnerId, item.OwnerIdentity, item.Direction);
        var prior = pages[key];
        var next = prior with { Detail = item.Detail, At = item.At };
        if (item.Kind == "HistorySkipped")
        {
            requests.Add(item.RequestId, key);
            next = new("HistorySkipped", item.Detail, item.At) { RequestId = item.RequestId };
        }
        else if (item.Kind == "Attempt")
        {
            requests.Add(item.RequestId, key);
            next = new("NotSent", item.Detail, item.At) { RequestId = item.RequestId };
        }
        else if (item.Kind == "NativeIntent") next = next with { Intent = true, State = "Uncertain" };
        else if (item.Kind is "NativeSettled" or "NativePostCodeRecovered" or "NativeGuardRejected" or "NativeGuardRejectedRecovered")
        {
            settlementEvents.Add(item.RequestId, item.NativeReturned);
            next = next with { Settled = true, SettledReturned = item.NativeReturned, State = item.NativeReturned ? "Sent" : "NotSent" };
            if (item.Kind is "NativeGuardRejected" or "NativeGuardRejectedRecovered") next = next with { State = "NativeGuardRejected" };
            if (item.Kind is "NativePostCodeRecovered" or "NativeGuardRejectedRecovered") next = next with { Detail = prior.Detail + " " + item.Detail };
        }
        else if (item.Kind == "Result") next = next with { Result = true,
            State = item.SessionBlocked || prior.Intent && !prior.Settled ? "Uncertain"
                : item.ResponseVerified ? "Confirmed" : prior.State };
        pages[key] = next; sequence = item.Sequence; updatedAt = item.At;
    }

    private void RequireHealthy() { if (error is not null) throw Failure(error); }
    private void ApplyManualReleases()
    {
        foreach(var release in ManualLockReleaseStore.Read(Path.GetDirectoryName(root)!))
        {
            if(!knownIntents.TryGetValue(release.RequestId,out var intent)||intent.Source!=release.Source||intent.PlanId!=release.PlanId)
                throw new InvalidDataException("人工解除缺少相符的原始 source-intent。");
            unresolved.Remove(release.RequestId);
            if(plan?.PlanId==release.PlanId&&requests.TryGetValue(release.RequestId,out var key)&&pages.TryGetValue(key,out var page))
            {
                if(key.OwnerId!=release.OwnerId||key.Direction!=release.Direction)throw new InvalidDataException("人工解除頁面不符。");
                if(page.State=="Uncertain")pages[key]=page with{State="ManualReleased",Detail=ManualLockReleaseStore.Warning};
            }
        }
    }
    private void LoadIntents(int readConcurrency=4, Action<ShopStartupStageEvent>? startupStage = null)
    {
        string directory = Path.Combine(root, "source-intents");
        if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory, "*.intent.json").Take(32769).ToArray();
        if (files.Length > 32768) throw new InvalidDataException("原生意圖索引超出上限。");
        var loadedIntents = new (ScanIntentEvidence Intent,ScanIntentEvidence? Settlement)[files.Length];
        ShopStartupTrace.Run("scan.source-intents-read", startupStage, () =>
        {
            Parallel.For(0,files.Length,new ParallelOptions{MaxDegreeOfParallelism=readConcurrency},i=>
            {
                var intent=Read<ScanIntentEvidence>(files[i],16*1024);
                var resolved=IntentPath(intent.RequestId,true);
                loadedIntents[i]=(intent,File.Exists(resolved)?Read<ScanIntentEvidence>(resolved,16*1024):null);
            });
            return files.Length;
        }, count => count);
        for(int i=0;i<files.Length;i++)
        {
            string file=files[i];var intent=loadedIntents[i].Intent;
            ValidateIntent(intent, settled: false);
            if (Path.GetFullPath(file) != IntentPath(intent.RequestId, false)) throw new InvalidDataException("原生意圖索引檔名不符。");
            knownIntents.Add(intent.RequestId, intent);
            if (loadedIntents[i].Settlement is{} settlement)
            {
                ValidateIntent(settlement, settled: true);
                if (settlement.Source != intent.Source || settlement.PlanId != intent.PlanId || settlement.RequestId != intent.RequestId
                    || settlement.At < intent.At) throw new InvalidDataException("原生意圖與已知結束索引不符。");
                knownSettlements.Add(intent.RequestId, settlement);
            }
            else unresolved.Add(intent.RequestId, intent);
        }
    }
    private static void ValidateIntent(ScanIntentEvidence item, bool settled)
    {
        if (item.Source is null || item.Source.ProcessId <= 0 || item.Source.CreationFileTime <= 0
            || item.Source.DiskSha256 is not { Length: 64 } || string.IsNullOrWhiteSpace(item.Source.SessionKey)
            || !Guid.TryParseExact(item.PlanId, "N", out _) || item.RequestId is not { Length: > 0 and <= 128 }
            || item.RequestId.Any(char.IsControl) || item.At == default || item.Settled != settled
            || item.Entered != item.Returned || !settled && item.Entered)
            throw new InvalidDataException("原生意圖索引不完整或狀態矛盾。");
    }
    private void SettleIndex(ScanIntentEvidence intent, bool returned)
    {
        var settled = intent with { Settled = true, Entered = returned, Returned = returned, At = NextTimestamp(intent.At) };
        WriteNew(IntentPath(intent.RequestId, true), settled);
        knownSettlements.Add(intent.RequestId, settled);
        unresolved.Remove(intent.RequestId);
    }
    private string IntentPath(string requestId, bool settled) => Path.Combine(root, "source-intents",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId))) + (settled ? ".resolved.json" : ".intent.json"));
    private static DateTimeOffset NextTimestamp(DateTimeOffset previous)
    { var now = DateTimeOffset.UtcNow; return now > previous ? now : previous.AddTicks(1); }
    private static string PublicDetail(string detail) => detail.Length <= 750 ? detail : detail[..750] + "…（完整原因保留於進度記錄）";
    private static ShopRequestFailure Failure(string detail) => new(ShopFailure.Global("SavedScanProgressUnavailable", detail));
    private static bool SameProcess(SourceIdentity left, SourceIdentity right) => left.ProcessId == right.ProcessId && left.CreationFileTime == right.CreationFileTime;
    private static readonly string[] Directions = [ShopPageModes.ViewerBuy, ShopPageModes.ViewerSell];
    private string EventsDirectory(string planId) => Path.Combine(root, planId + ".events");
    private static string PlanFileName(SavedScanPlan value) => value.CreatedAt.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + value.PlanId + ".plan.json";
    private static string EventFileName(int value) => value.ToString("D8", CultureInfo.InvariantCulture) + ".json";
    private static void ValidatePlan(SavedScanPlan value)
    {
        if (!Guid.TryParseExact(value.PlanId, "N", out _) || value.Source is null || value.CreatedAt == default || value.Owners is null)
            throw new InvalidDataException("搜尋計畫識別或建立時間無效。");
        ShopTargetPolicy.ValidateStored(new ShopTargetSnapshot { SnapshotId = value.OriginalSnapshotId,
            SourceSessionKey = value.Source.SessionKey, RefreshedAt = value.CreatedAt, SelfId = value.SelfId,
            SelfPosition = value.SelfPosition, Owners = value.Owners, Detail = "保存搜尋計畫" }, value.Source);
    }
    private static T Read<T>(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 || file.Length > maximum) throw new InvalidDataException("搜尋進度檔案大小無效。");
        return JsonSerializer.Deserialize<T>(file, HubProtocol.Json) ?? throw new InvalidDataException("搜尋進度檔案為空。");
    }
    private static void WriteNew<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(file, value, HubProtocol.Json); file.Flush(true); }
        File.Move(temporary, path, overwrite: false);
    }
    private sealed record PageState(string State, string Detail, DateTimeOffset At)
    {
        internal string? RequestId { get; init; }
        internal bool Intent { get; init; }
        internal bool Settled { get; init; }
        internal bool SettledReturned { get; init; }
        internal bool Result { get; init; }
    }
}
