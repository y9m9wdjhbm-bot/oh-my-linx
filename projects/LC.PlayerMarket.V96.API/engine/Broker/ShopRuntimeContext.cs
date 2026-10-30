using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryHub.Reader;

namespace MemoryHub.Service;

/// <summary>Uses service snapshots or one reader-owned refresh; never reads process memory itself or publishes synthetic snapshots.</summary>
internal sealed partial record ShopRuntimeContext(SourceIdentity Source, ulong SelfId, ShopPoint SelfPosition,
    MarketOwner[] Players, bool ViewportAvailable, string CoverageDetail)
{
    private HubSnapshot? Observations { get; init; }
    private ReadSelection? ObservedSelection { get; init; }
    private ulong? SelectedDispatchOwnerId { get; init; }

    internal static ShopRuntimeContext Capture(HubRuntime runtime, string expectedSession)
    {
        Need(runtime.IsRunning, "本機讀取器尚未啟動。");
        return Capture(runtime.Snapshot, runtime.ReadSelection, expectedSession, DateTimeOffset.UtcNow);
    }

    internal static ShopRuntimeContext CaptureFresh(HubRuntime runtime, CurrentLcSource reader, string expectedSession,
        CancellationToken token, bool requireViewport = false, ShopRuntimeContext? prior = null, bool requireShopStates = false)
    {
        token.ThrowIfCancellationRequested(); Need(runtime.IsRunning, "本機讀取器尚未啟動。");
        var selected = runtime.ReadSelection;
        foreach (string key in new[] { "selfId", "position", "nearbyPlayers", "playerShopStates" })
            Need(selected.Enabled(key), $"{key} 已停用；未暗中開啟讀取。");
        Need(!requireViewport || selected.Enabled("actorViewport"), "畫面候選搜尋需要啟用 actorViewport。");
        var before = runtime.Snapshot;
        Need(before.Source is not null && before.Source.SessionKey == expectedSession && !string.IsNullOrWhiteSpace(expectedSession),
            "LC 來源已改變或無法確認。");
        var now = DateTimeOffset.UtcNow;
        ShopRuntimeContext context;
        if (!RequiresRefresh(before, selected, now, requireViewport, requireShopStates)) context = Capture(before, selected, expectedSession, now);
        else if (prior?.Observations is {} previous && prior.Source == before.Source && ReferenceEquals(prior.ObservedSelection, selected)
            && !RequiresRefresh(previous, selected, now, requireViewport, requireShopStates))
        {
            // The preceding fresh action sample may still be valid while the
            // background snapshot lags. Preserve every original SampledAt.
            context = Capture(previous, selected, expectedSession, now);
        }
        else
        {
            var timer = Stopwatch.StartNew();
            var result = reader.ReadSlice(new ReadSelection(RequiredKeys(selected, requireViewport, requireShopStates).Append("playerShopStates")), token);
            token.ThrowIfCancellationRequested();
            var after = runtime.Snapshot;
            Need(runtime.IsRunning && ReferenceEquals(selected, runtime.ReadSelection) && after.Source == before.Source,
                "即時角色採樣期間來源或讀取選項已改變。");
            context = FromFreshResult(result, before.Source!, selected, expectedSession, DateTimeOffset.UtcNow,
                after.Fields.GetValueOrDefault("mapName"), requireViewport, requireShopStates);
            runtime.RecordLog(new(DateTimeOffset.UtcNow, "SHOP_CONTEXT_REFRESH", "必要快照已過期；僅透過 Reader 即時重讀一次，未更改背景快照時間。",
                new { ElapsedMs = timer.Elapsed.TotalMilliseconds, result.Reads, result.ReadBytes,
                    Keys = RequiredKeys(selected, requireViewport, requireShopStates), context.Source.SessionKey }));
        }
        if (requireShopStates) ValidateShopStates(context.Observations!, DateTimeOffset.UtcNow);
        Need(!requireViewport || context.ViewportAvailable, "本次沒有唯一且新鮮的矩形候選。");
        token.ThrowIfCancellationRequested();
        Need(runtime.IsRunning && ReferenceEquals(selected, runtime.ReadSelection) && runtime.Snapshot.Source == context.Source,
            "交付角色操作來源前，LC 或讀取選項已改變。");
        return context;
    }

    internal static bool RequiresRefresh(HubSnapshot snapshot, ReadSelection selected, DateTimeOffset now,
        bool requireViewport = false, bool requireShopStates = false) =>
        RequiredKeys(selected, requireViewport, requireShopStates).Any(key => !snapshot.Fields.TryGetValue(key, out var field) || !field.Fresh(now) || field.Value is null)
        || requireShopStates && snapshot.Fields.GetValueOrDefault("nearbyPlayers")?.SampledAt != snapshot.Fields.GetValueOrDefault("playerShopStates")?.SampledAt;

    private static string[] RequiredKeys(ReadSelection selected, bool requireViewport = false, bool requireShopStates = false) =>
        new[] { "selfId", "position", "nearbyPlayers" }
            .Concat(requireShopStates ? ["playerShopStates"] : System.Array.Empty<string>())
            .Concat(requireViewport ? ["actorViewport"] : System.Array.Empty<string>()).ToArray();

    internal static ShopRuntimeContext FromFreshResult(ReadResult result, SourceIdentity expected, ReadSelection selected,
        string expectedSession, DateTimeOffset now, FieldReading? map = null,
        bool requireViewport = false, bool requireShopStates = false)
        => FromResultCore(result, expected, selected, expectedSession, now, map, requireViewport, requireShopStates, null);

    internal static ShopRuntimeContext FromSelectedDispatchResult(ReadResult result, SourceIdentity expected, ReadSelection selected,
        string expectedSession, DateTimeOffset now, ulong ownerId, FieldReading? map = null)
        => FromResultCore(result, expected, selected, expectedSession, now, map, false, true, ownerId);

    private static ShopRuntimeContext FromResultCore(ReadResult result, SourceIdentity expected, ReadSelection selected,
        string expectedSession, DateTimeOffset now, FieldReading? map, bool requireViewport, bool requireShopStates, ulong? selectedOwnerId)
    {
        Need(!result.NoSourceRead && result.Source == expected && expected.SessionKey == expectedSession,
            "即時角色採樣不是相同的完整 LC 來源。");
        var fields = new Dictionary<string, FieldReading>(result.Fields);
        if (selected.Enabled("mapName") && map is not null && map.Fresh(now)) fields["mapName"] = map;
        var local = new HubSnapshot { Source = result.Source, Fields = fields };
        var missing = RequiredKeys(selected, requireViewport, requireShopStates)
            .Where(key => !fields.TryGetValue(key, out var field) || !field.Fresh(now) || field.Value is null)
            .Select(key => $"{key}={fields.GetValueOrDefault(key)?.State ?? "Missing"}（{fields.GetValueOrDefault(key)?.Detail ?? "缺少欄位"}）").ToArray();
        Need(missing.Length == 0, "即時重讀未取得全部必要欄位的新鮮值；不使用舊值代填。" + string.Join("；", missing));
        if (requireShopStates) ValidateShopStates(local, now, selectedOwnerId);
        var captured = CaptureScoped(local, selected, expectedSession, now, selectedOwnerId);
        Need(!requireViewport || captured.ViewportAvailable, "本次沒有唯一且新鮮的矩形候選。");
        return captured;
    }

    internal void RequireFreshForDispatch(DateTimeOffset now)
    {
        if (FrozenTargetDispatch) { RequireFreshFrozenDispatch(now); return; }
        if (Observations is null || ObservedSelection is null || Observations.Source != Source
            || RequiresRefresh(Observations, ObservedSelection, now, requireShopStates: true))
            throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable",
                "送出前自身／周遭玩家／擺攤狀態的必要採樣已過期或不完整；本頁未送出。" +
                string.Join("；", new[] { "selfId", "position", "nearbyPlayers", "playerShopStates" }.Select(key =>
                {
                    var field = Observations?.Fields.GetValueOrDefault(key);
                    return $"{key}={field?.State ?? "Missing"}, sampledAt={field?.SampledAt:O}, ageMs={(now-field?.SampledAt)?.TotalMilliseconds:F1}";
                }))));
        ValidateShopStates(Observations, now, SelectedDispatchOwnerId);
    }

    private static void ValidateShopStates(HubSnapshot snapshot, DateTimeOffset now, ulong? selectedOwnerId = null)
    {
        var states = Field(snapshot, "playerShopStates", now);
        Need(CollectionScopeMatches(states, selectedOwnerId) && Array(states, "items", out var rows) && rows.GetArrayLength() <= ActorCollectionContract.MaximumActors,
            "擺攤狀態集合尚未完整閉合；未將部分資料當成完整名單。");
        Need(snapshot.Fields["playerShopStates"].SampledAt == snapshot.Fields.GetValueOrDefault("nearbyPlayers")?.SampledAt,
            "周遭玩家與擺攤狀態不是同次採樣；未組合不同時間的目標資料。");
    }

    internal static ShopRuntimeContext Capture(HubSnapshot snapshot, ReadSelection selected, string expectedSession, DateTimeOffset now)
        => CaptureScoped(snapshot, selected, expectedSession, now, null);

    private static ShopRuntimeContext CaptureScoped(HubSnapshot snapshot, ReadSelection selected, string expectedSession, DateTimeOffset now,
        ulong? selectedOwnerId)
    {
        SourceIdentity source = snapshot.Source ?? throw new InvalidOperationException("目前沒有有效 LC 來源。");
        Need(!string.IsNullOrWhiteSpace(expectedSession) && source.SessionKey == expectedSession,
            "LC 來源已改變；請更新資料後重新提出明確請求。");
        JsonElement own = Field(snapshot, "selfId", now);
        Need(own.ValueKind == JsonValueKind.Number && own.TryGetUInt64(out ulong self) && self is > 0 and < ulong.MaxValue,
            "自身 ID 無效。");
        ulong selfId = own.GetUInt64();
        Need(selectedOwnerId is null || selectedOwnerId is > 0 and <= int.MaxValue && selectedOwnerId != selfId,
            "指定店主不能是自身、零或越界 ID。");
        ShopPoint position = Point(Field(snapshot, "position", now)) ?? throw new InvalidOperationException("自身座標無效。");
        JsonElement nearby = Field(snapshot, "nearbyPlayers", now);
        Need(CollectionScopeMatches(nearby, selectedOwnerId) && Array(nearby, "items", out var items) && items.GetArrayLength() <= ActorCollectionContract.MaximumActors,
            "周遭玩家清單尚未完整閉合。");
        items = nearby.GetProperty("items");
        string? map = snapshot.Fields.TryGetValue("mapName", out var mapField) && mapField.Fresh(now)
            && mapField.Value is { ValueKind: JsonValueKind.String } mapValue ? mapValue.GetString() : null;

        var viewportIds = new HashSet<ulong>();
        bool viewportAvailable = false;
        if (selected.Enabled("actorViewport") && snapshot.Fields.TryGetValue("actorViewport", out var viewField)
            && viewField.Fresh(now) && viewField.Value is {} view && Array(view, "items", out var rectangles)
            && rectangles.GetArrayLength() <= 128)
        {
            var seen = new HashSet<ulong>();
            bool unique = true;
            foreach (var row in rectangles.EnumerateArray())
            {
                if (!Id(row, "localId", out ulong id) || !seen.Add(id)) { unique = false; break; }
                if (Text(row, "kind") == "Player" && True(row, "presentInSampledCollection")
                    && True(row, "geometricCandidate")) viewportIds.Add(id);
            }
            viewportAvailable = unique;
            if (!unique) viewportIds.Clear();
        }
        var (shopStates, shopStateDetail) = ReadShopStates(snapshot, selected, now, selectedOwnerId);
        var ids = new HashSet<ulong>();
        var players = new List<MarketOwner>();
        foreach (var item in items.EnumerateArray())
        {
            Need(Id(item, "id", out ulong id) && ids.Add(id) && Text(item, "kind") == "Player"
                && Text(item, "localIdentity") is { Length: > 0 and <= 256 }, "周遭玩家 ID／型別／本機識別不唯一。");
            if (id == selfId || id > int.MaxValue) continue;
            if (Text(item, "positionState") != "Observed" || !Object(item, "position", out var p)
                || Point(p) is not {} xy || !item.TryGetProperty("sampledAt", out var time)
                || !time.TryGetDateTimeOffset(out var observedAt) || !Fresh(observedAt, now)) continue;
            var owner = new MarketOwner
            {
                Id = id, LocalIdentity = Text(item, "localIdentity")!,
                Name = Text(item, "nameState") == "Observed" ? Text(item, "name") ?? "" : "",
                Position = xy, MapName = map, ObservedAt = observedAt, InViewport = null,
                VisibilityEvidence = viewportAvailable && viewportIds.Contains(id)
                    ? "RawRectangleGeometricCandidate_UnverifiedCoverage" : "LocalActorCollection_VisibilityUnknown"
            };
            players.Add(ApplyShopState(owner, shopStates.GetValueOrDefault((id, owner.LocalIdentity)),
                shopStateDetail, snapshot.Fields["nearbyPlayers"].SampledAt!.Value, now));
        }
        return new(source, selfId, position, players.ToArray(), viewportAvailable,
            "只以本機玩家集合與原始矩形幾何候選選取；畫面可見性與完整覆蓋尚未驗證。")
            { Observations = snapshot, ObservedSelection = selected, SelectedDispatchOwnerId = selectedOwnerId };
    }

    // Only the same ActorFieldsReader capture may join these independent fields.
    // A missing/malformed state affects this optional leaf, never player coordinates.
    private static (Dictionary<(ulong, string), JsonElement> Items, string Detail) ReadShopStates(
        HubSnapshot snapshot, ReadSelection selected, DateTimeOffset now, ulong? selectedOwnerId = null)
    {
        var result = new Dictionary<(ulong, string), JsonElement>();
        if (!selected.Enabled("playerShopStates")) return (result, "玩家擺攤狀態讀取已停用。");
        if (!snapshot.Fields.TryGetValue("playerShopStates", out var field) || !field.Fresh(now)
            || field.Value is not {} value || !snapshot.Fields.TryGetValue("nearbyPlayers", out var players)
            || field.SampledAt != players.SampledAt)
            return (result, "尚未取得與本次玩家清單同次採樣的擺攤狀態。");
        if (!CollectionScopeMatches(value, selectedOwnerId) || !Array(value, "items", out var items) || items.GetArrayLength() > ActorCollectionContract.MaximumActors)
            return (result, "擺攤狀態集合尚未完整閉合。");
        var duplicate = new HashSet<(ulong, string)>();
        foreach (var row in items.EnumerateArray())
        {
            if (!Id(row, "id", out ulong id) || Text(row, "localIdentity") is not { Length: > 0 and <= 256 } identity) continue;
            var key = (id, identity);
            if (!result.TryAdd(key, row)) duplicate.Add(key);
        }
        foreach (var key in duplicate) result.Remove(key);
        return (result, "此玩家缺少唯一且同本機識別的擺攤狀態採樣。");
    }

    private static MarketOwner ApplyShopState(MarketOwner owner, JsonElement row, string missingDetail,
        DateTimeOffset captureAt, DateTimeOffset now)
    {
        if (row.ValueKind != JsonValueKind.Object) return owner with { ShopStateDetail = missingDetail };
        string? action = BoundedText(row, "actionType", 256);
        string? evidence = BoundedText(row, "shopStateEvidence", 1024);
        string detail = BoundedText(row, "shopStateDetail", 2048) ?? "擺攤狀態採樣缺少有效說明。";
        DateTimeOffset? at = row.TryGetProperty("shopStateSampledAt", out var time)
            && time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out var sampled)
            && sampled >= captureAt && sampled <= now ? sampled : null;
        var observed = owner with { ActionType = action, ShopState = Text(row, "shopState") ?? ShopEligibilityPolicy.Unknown,
            ShopStateSampledAt = at, ShopStateEvidence = evidence ?? "", ShopStateDetail = detail };
        string state = ShopEligibilityPolicy.State(observed);
        return observed with { ShopState = state, ShopStateDetail = state == ShopEligibilityPolicy.Unknown
            && observed.ShopState != ShopEligibilityPolicy.Unknown ? "擺攤狀態證據／時間／型別不完整，保留未知。" : detail };
    }

    private static string? BoundedText(JsonElement row, string key, int maximum) =>
        Text(row, key) is { Length: > 0 } text && text.Length <= maximum ? text : null;

    internal MarketOwner Owner(ulong id, string? expectedIdentity)
    {
        var matches = Players.Where(p => p.Id == id).ToArray();
        Need(id is > 0 and <= int.MaxValue && id != SelfId && matches.Length == 1,
            "店主不在目前唯一且新鮮的周遭玩家與座標資料中。");
        var owner = matches[0];
        Need(string.IsNullOrEmpty(expectedIdentity) || owner.LocalIdentity == expectedIdentity,
            "店主本機物件識別已改變；未沿用舊 ID。");
        return owner;
    }

    internal static ulong ImageBase(SourceIdentity source)
    {
        string value = source.ImageBase;
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    internal static ulong Window(SourceIdentity source)
    {
        using var process = Process.GetProcessById(source.ProcessId);
        Need(!process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == source.CreationFileTime,
            "LC 程序已退出或重新啟動。");
        nint hwnd = process.MainWindowHandle;
        Need(hwnd != 0 && IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == source.ProcessId,
            "無法唯一確認此 LC 的主視窗執行緒。");
        return checked((ulong)hwnd);
    }

    internal static JsonElement Field(HubSnapshot snapshot, string key, DateTimeOffset now)
    {
        Need(snapshot.Fields.TryGetValue(key, out var field) && field.Fresh(now) && field.Value is not null,
            $"{key} 尚未取得新鮮資料，可能已停用或已過期；不會暗中開啟欄位。");
        return field!.Value!.Value;
    }
    internal static bool Fresh(DateTimeOffset at, DateTimeOffset now) => at <= now && now - at <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds);
    internal static ShopPoint? Point(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("x", out var x) && x.TryGetInt32(out int px) && px is > 0 and <= 1_000_000
        && value.TryGetProperty("y", out var y) && y.TryGetInt32(out int py) && py is > 0 and <= 1_000_000 ? new(px, py) : null;
    internal static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    internal static bool True(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    private static bool Id(JsonElement value, string key, out ulong id)
    { id = 0; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out id) && id is > 0 and < ulong.MaxValue; }
    private static bool Object(JsonElement value, string key, out JsonElement result)
    { result = default; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out result) && result.ValueKind == JsonValueKind.Object; }
    private static bool Array(JsonElement value, string key, out JsonElement result)
    { result = default; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out result) && result.ValueKind == JsonValueKind.Array; }
    internal static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}

