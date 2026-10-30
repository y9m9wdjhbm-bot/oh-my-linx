using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record LivePlayer(ulong Id, string Identity, string Name, ShopPoint? Position, DateTimeOffset At);
internal sealed record NavigationView(RectangleF Target, PointF Direction, long Distance, string Name, bool Research, string Evidence)
{
    public bool DistanceToSavedPosition { get; init; }
    public bool CoordinateOnly { get; init; }
    public ShopPoint? SavedPosition { get; init; }
    public string? CoordinateNote { get; init; }
}

internal sealed class LiveContext
{
    private readonly HubSnapshot? snapshot;
    private readonly DateTimeOffset now;
    public SourceIdentity? Source { get; }
    public ulong? SelfId { get; }
    public ShopPoint? SelfPosition { get; }
    public string? MapName { get; }
    public string? MapId { get; }
    public bool Valid { get; }
    public bool NearbyAvailable { get; }
    public string Detail { get; }
    public Dictionary<ulong, LivePlayer> Players { get; } = [];
    public LiveContext(HubSnapshot? current, DateTimeOffset at)
    {
        now = at; snapshot = current?.AtTime(at); Source = snapshot?.Source;
        if (snapshot is null || Source is null || snapshot.State == "Disconnected" || snapshot.PublishedAt > at
            || at - snapshot.PublishedAt > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
        { Detail = "目前來源未連線或已過期"; return; }
        if (Field("selfId") is { } id && id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out ulong sid) && sid != 0) SelfId = sid;
        if (Field("position") is { } position) SelfPosition = Point(position);
        if (Field("mapName") is { } map && map.ValueKind == JsonValueKind.String) MapName = map.GetString();
        if (Field("mapId") is { } mapId && mapId.ValueKind is JsonValueKind.String or JsonValueKind.Number) MapId = mapId.ToString();
        if (SelfId is null) { Detail = MissingFieldDetail("selfId", "自身 ID"); return; }
        if (SelfPosition is null) { Detail = MissingFieldDetail("position", "自身座標"); return; }
        Valid = true; Detail = "自身 ID 與座標已連線；周遭名單另以手動快照保存";
        if (Field("nearbyPlayers") is not { } players || !Bool(players, "complete") || !Array(players, "items", out var items))
            return;
        var raw = items.EnumerateArray().Take(8193).ToArray();
        if (raw.Length > 8192) { Detail = "自身資料有效；周遭即時清單超出資料上限，未採用"; return; }
        var duplicates = raw.Where(p => Number(p, "id").HasValue).GroupBy(p => Number(p, "id")!.Value)
            .Where(g => g.Count() != 1).Select(g => g.Key).ToHashSet();
        foreach (var item in raw)
        {
            if (Number(item, "id") is not { } value || value == 0 || duplicates.Contains(value) || Text(item, "kind") != "Player"
                || Text(item, "rawType") != ".?AVPlayer@lineage@@" || Text(item, "localIdentity") is not { Length: > 0 } identity
                || !TryTime(item, "sampledAt", out var sampled) || !Fresh(sampled)) continue;
            var world = Text(item, "positionState") == "Observed" && item.TryGetProperty("position", out var p) ? Point(p) : null;
            Players[value] = new(value, identity, Text(item, "name") ?? "", world, sampled);
        }
        NearbyAvailable = true; Detail = $"自身資料已連線，另有 {Players.Count} 個目前玩家位置來源";
    }

    private string MissingFieldDetail(string key, string label)
    {
        var field = snapshot?.Fields.GetValueOrDefault(key);
        string message = field?.State switch
        {
            "Disabled" => $"本程式的{label}讀取已停用；請檢查本機讀取設定",
            "Stale" => $"{label}更新逾時，等待本程式重新採樣",
            "Unsupported" => $"本程式目前不支援此 LC 版本的{label}讀取",
            "Observed" => $"{label}採樣值尚未有效，等待本程式重新採樣",
            _ => $"等待本程式讀取{label}"
        };
        return string.IsNullOrWhiteSpace(field?.Detail) ? message : message + "；" + field.Detail;
    }

    public LivePlayer? Match(MarketListing listing) => Valid && NearbyAvailable && listing.SourceSessionKey == Source?.SessionKey
        && listing.Owner is not null && Players.TryGetValue(listing.Owner.Id, out var player)
        && !string.IsNullOrEmpty(listing.Owner.LocalIdentity) && player.Identity == listing.Owner.LocalIdentity ? player : null;

    public bool TrySavedPositionNavigation(MarketListing listing, out NavigationView? view, out string reason)
    {
        view = null;
        reason = "等待最新自身座標；保存的商店位置仍保留";
        if (!Valid || SelfPosition is not { } self || Source is null) { reason += "；" + Detail; return false; }
        return SavedPositionNavigation.TryCreate(listing, self, MapName, out view, out reason);
    }

    public bool TryNavigation(MarketListing listing, Size frame, bool allowCandidate, bool bottomOrigin, out NavigationView? view, out string reason)
    {
        view = null; reason = "尚未選擇目前來源中的店主";
        var player = Match(listing);
        if (player?.Position is not { } owner || SelfPosition is not { } self || SelfId is not { } selfId) return false;
        if (Field("actorViewport") is not { } viewport || !Bool(viewport, "complete") || !Array(viewport, "items", out var items))
        { reason = "物件矩形欄位未更新；標記已隱藏"; return false; }
        return TryProjection(viewport, player, self, selfId, frame, allowCandidate, bottomOrigin, true, false, out view, out reason);
    }

    public bool TrySelectedNavigation(MarketListing listing, ManualNearbyContext manual, ShopNavigationSnapshot? navigation,
        Size frame, bool allowCandidate, bool bottomOrigin, out NavigationView? view, out string reason)
    {
        view = null; reason = "等待本程式讀取選中的店主投影";
        var player = manual.Match(listing);
        if (!Valid || Source is null || Source != manual.Source || player is null || player.Position is null || SelfPosition is null || SelfId is null)
        { reason = "自身來源或保存店主尚未有效對應"; return false; }
        if (navigation is null) return false;
        if (navigation.SourceSessionKey != Source.SessionKey || navigation.TargetSnapshotId != manual.SnapshotId
            || navigation.OwnerId != player.Id || navigation.OwnerIdentity != player.Identity || navigation.SelfId != SelfId
            || navigation.SavedTargetAt != player.At || navigation.SavedOwnerPosition != player.Position || !Fresh(navigation.SampledAt))
        { reason = "選中目標的投影來源／名單版本不符，或投影已過期"; return false; }
        if (navigation.State is not ("ResearchCandidate" or "Observed") || navigation.RawViewport is not { } viewport)
        { reason = "尚無選中目標投影；" + navigation.Detail; return false; }
        return TryProjection(viewport, player, SelfPosition, SelfId.Value, frame, allowCandidate, bottomOrigin,
            navigation.PhysicalPixelMappingVerified && navigation.CurrentActorIdentityVerified, true, out view, out reason);
    }

    private static bool TryProjection(JsonElement viewport, LivePlayer player, ShopPoint self, ulong selfId,
        Size frame, bool allowCandidate, bool bottomOrigin, bool permitVerified, bool selectedLookup,
        out NavigationView? view, out string reason)
    {
        view = null; reason = "物件矩形欄位未更新；標記已隱藏";
        if (frame.Width is < 100 or > 32000 || frame.Height is < 100 or > 32000
            || !Bool(viewport, "complete") || !Array(viewport, "items", out var items) || player.Position is not { } owner) return false;
        bool verified = permitVerified && Bool(viewport, "physicalPixelMappingVerified") && Bool(viewport, "currentCodeSemanticsVerified")
            && Text(viewport, "coordinateSpace") == "LCClientPhysicalPixels";
        if (!verified && !allowCandidate) { reason = "目前投影為研究候選；可開啟「研究投影」人工比對"; return false; }
        var rows = items.EnumerateArray().Take(8193).ToArray();
        if (rows.Length > 8192) { reason = "物件矩形超出資料上限"; return false; }
        if (selectedLookup && (rows.Length != 2 || !viewport.TryGetProperty("collectionEnumerated", out var enumerated)
            || enumerated.ValueKind != JsonValueKind.False))
        { reason = "指定目標回覆不是兩個 ID 的獨立查詢"; return false; }
        var targets = rows.Where(p => Number(p, "localId") == player.Id && Text(p, "kind") == "Player").ToArray();
        var selves = rows.Where(p => Number(p, "localId") == selfId && Text(p, "kind") == "MyPlayer").ToArray();
        bool Present(JsonElement row) => selectedLookup ? Bool(row, "presentInSelectedLookup") : Bool(row, "presentInSampledCollection");
        if (targets.Length != 1 || selves.Length != 1 || !Present(targets[0]) || !Present(selves[0]))
        { reason = "本輪目標或自己矩形不唯一；標記已隱藏"; return false; }
        RectangleF target, own;
        if (verified)
        {
            if (!viewport.TryGetProperty("clientSize", out var size) || Double(size, "width") != frame.Width || Double(size, "height") != frame.Height
                || Text(targets[0], "localIdentity") != player.Identity || !Rect(targets[0], "clientPixelRectangle", out target)
                || !Rect(selves[0], "clientPixelRectangle", out own))
            { reason = "本程式讀取的已驗證投影與目前視窗尺寸不符"; return false; }
        }
        else
        {
            if (!viewport.TryGetProperty("rawFrame", out var rawFrame) || !rawFrame.TryGetProperty("rawRectangle", out var raw)
                || Double(raw, "x") != 0 || Double(raw, "y") != 0 || Double(rawFrame, "candidateWidth") is not { } width
                || Double(rawFrame, "candidateHeight") is not { } height || width <= 0 || height <= 0 || width > 32000 || height > 32000
                || Double(rawFrame, "scaleX") is not { } scaleX || Double(rawFrame, "scaleY") is not { } scaleY || scaleX <= 0 || scaleY <= 0
                || Double(raw, "width") is not { } rawWidth || Double(raw, "height") is not { } rawHeight
                || Math.Abs(rawWidth / scaleX - width) > .02 || Math.Abs(rawHeight / scaleY - height) > .02
                || Text(targets[0], "rectangleState") != "RawRectangleObserved" || Text(selves[0], "rectangleState") != "RawRectangleObserved"
                || !Rect(targets[0], "correctedRectangleCandidate", out target) || !Rect(selves[0], "correctedRectangleCandidate", out own))
            { reason = "候選投影的來源或縮放不完整"; return false; }
            double sx = frame.Width / width, sy = frame.Height / height;
            if (sx < .05 || sy < .05 || sx > 20 || sy > 20) { reason = "候選投影縮放超出範圍"; return false; }
            target = Map(target, sx, sy, height, bottomOrigin); own = Map(own, sx, sy, height, bottomOrigin);
        }
        var targetCenter = new PointF(target.X + target.Width / 2, target.Y + target.Height / 2);
        var ownCenter = new PointF(own.X + own.Width / 2, own.Y + own.Height / 2);
        var direction = new PointF(targetCenter.X - ownCenter.X, targetCenter.Y - ownCenter.Y);
        view = new(target, direction, self.GridDistance(owner), player.Name, !verified,
            verified ? "本程式已驗證實體像素" : $"研究候選／{(bottomOrigin ? "下方" : "上方")}原點／矩形中心"
                + (selectedLookup ? "／距離至店主保存位置" : "")) { DistanceToSavedPosition = selectedLookup };
        reason = view.Evidence; return true;
    }

    private JsonElement? Field(string key) => snapshot?.Fields.TryGetValue(key, out var f) == true && f.Fresh(now) ? f.Value : null;
    private bool Fresh(DateTimeOffset time) => time <= now && now - time <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds);
    private static RectangleF Map(RectangleF r, double x, double y, double height, bool bottom) =>
        new((float)(r.X * x), (float)((bottom ? height - r.Y - r.Height : r.Y) * y), (float)(r.Width * x), (float)(r.Height * y));
    internal static ShopPoint? Point(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int ix)
        && value.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out int iy) ? new(ix, iy) : null;
    internal static bool Bool(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    internal static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static ulong? Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var n) ? n : null;
    private static double? Double(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static bool TryTime(JsonElement value, string key, out DateTimeOffset time)
    { time = default; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out time); }
    private static bool Array(JsonElement value, string key, out JsonElement array)
    { array = default; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out array) && array.ValueKind == JsonValueKind.Array; }
    private static bool Rect(JsonElement value, string key, out RectangleF rect)
    {
        rect = default;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var p) || Double(p, "x") is not { } x
            || Double(p, "y") is not { } y || Double(p, "width") is not { } w || Double(p, "height") is not { } h
            || Math.Abs(x) > 100000 || Math.Abs(y) > 100000 || w <= 0 || h <= 0 || w > 32000 || h > 32000) return false;
        rect = new((float)x, (float)y, (float)w, (float)h); return true;
    }
}
