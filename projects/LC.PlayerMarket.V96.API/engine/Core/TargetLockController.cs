using System.Text.Json;

namespace MemoryHub;

/// <summary>Local research selection. Never sends input or writes game memory.</summary>
public sealed record TargetLockSelection(SourceIdentity Source, ulong Id, string LocalIdentity, string? Name);
public sealed record TargetLockClientFrame(int ProcessId, long CreationFileTime, int Width, int Height,
    bool IsForeground, bool IsVisible);
public sealed record TargetLockPoint(double X, double Y, string Evidence = "VerifiedClientPixels");
public sealed record TargetLockPresentation(TargetLockPoint? Marker, string Status);
public enum TargetLockCandidateOrigin { BottomLeft, TopLeft }

/// <summary>
/// Consumes immutable Hub snapshots only. Raw/candidate rectangles are deliberately
/// not a verified physical-pixel mapping contract. Optional research projection is
/// explicitly labeled; future verified producers use the client-pixel contract.
/// </summary>
public sealed class TargetLockController(bool allowResearchProjection = false)
{
    private HubSnapshot snapshot = new();
    public TargetLockCandidateOrigin CandidateOrigin { get; private set; } = TargetLockCandidateOrigin.BottomLeft;
    public TargetLockSelection? Selection { get; private set; }
    public string LastReason { get; private set; } = "研究鎖定：尚未選擇怪物；不會攻擊或施法。";

    public void UpdateSnapshot(HubSnapshot value, DateTimeOffset now)
    {
        snapshot = value;
        if (Selection is not { } target) return;
        if (value.Source != target.Source)
        {
            Clear("遊戲來源已改變或中斷，已解除研究鎖定。");
            return;
        }
        if (TryMonsters(now, out var monsters, out _) &&
            !monsters.Any(m => m.Id == target.Id && m.LocalIdentity == target.LocalIdentity))
            Clear("原怪物已離開最新物件集合或識別已變更，已解除研究鎖定。");
    }

    public void Clear(string reason = "已解除研究鎖定。") { Selection = null; LastReason = reason; }

    public void SetCandidateOrigin(TargetLockCandidateOrigin origin)
    {
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        CandidateOrigin = origin;
    }

    public bool TryLockMonster(ulong id, DateTimeOffset now, out string reason)
    {
        if (!TryMonsters(now, out var monsters, out reason)) { LastReason = reason; return false; }
        var matches = monsters.Where(m => m.Id == id).ToArray();
        if (id is 0 or ulong.MaxValue || matches.Length != 1)
        {
            reason = LastReason = "此 ID 不在唯一且新鮮的怪物資料中，未改變鎖定。";
            return false;
        }
        var monster = matches[0];
        Selection = new(snapshot.Source!, monster.Id, monster.LocalIdentity, monster.Name);
        reason = LastReason = $"研究選定：{monster.Name ?? "未命名怪物"}，ID {id}；不會攻擊或施法。";
        return true;
    }

    public TargetLockPresentation Present(TargetLockClientFrame? window, DateTimeOffset now)
    {
        if (Selection is not { } target) return new(null, LastReason);
        string selected = $"研究選定 ID {target.Id}；";
        if (snapshot.Source != target.Source) return new(null, selected + "來源不符，黃點隱藏。");
        if (!TryMonsters(now, out var monsters, out string why)) return new(null, selected + why);
        if (!monsters.Any(m => m.Id == target.Id && m.LocalIdentity == target.LocalIdentity))
            return new(null, selected + "最新樣本找不到原物件，黃點隱藏。");
        if (!TryVerifiedRectangles(window, now, out var rectangles, out why)) return new(null, selected + why);
        var found = rectangles.Where(r => r.Id == target.Id && r.LocalIdentity == target.LocalIdentity && r.Kind == "Monster").ToArray();
        if (found.Length != 1 || !found[0].Visible) return new(null, selected + "尚無唯一且可見的已驗證矩形，黃點隱藏。");
        var rect = found[0];
        var point = new TargetLockPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, rect.Research ? "ResearchCandidate" : "VerifiedClientPixels");
        if (point.X < 0 || point.Y < 0 || point.X >= window!.Width || point.Y >= window.Height)
            return new(null, selected + "標記中心不在遊戲用戶區，黃點隱藏。");
        return new(point, selected + (rect.Research ? $"黃點為候選投影（{(CandidateOrigin == TargetLockCandidateOrigin.BottomLeft ? "Y 下方原點" : "Y 上方原點")}），物理映射未驗證；供人工比對。" : "黃點為已驗證座標之外部提示。") + "未設定遊戲原生目標。");
    }

    public bool TrySelectAtClientPoint(double x, double y, TargetLockClientFrame? window,
        DateTimeOffset now, out string reason)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || window is null || x < 0 || y < 0 || x >= window.Width || y >= window.Height)
        { reason = LastReason = "點擊不在有效的遊戲用戶區。"; return false; }
        if (!TryMonsters(now, out _, out reason) || !TryVerifiedRectangles(window, now, out var rectangles, out reason))
        { LastReason = reason; return false; }
        // Any actor overlap is ambiguous, including NPC/player overlap. Never pick
        // a nearest row, frontmost guess, repeated ID, or rectangle candidate.
        var hits = rectangles.Where(r => r.Visible && x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height).ToArray();
        if (hits.Length != 1 || hits[0].Kind != "Monster")
        { reason = LastReason = hits.Length > 1 ? "點擊區域有多個物件重疊，未改變鎖定。" : "點擊位置沒有唯一已驗證怪物，未改變鎖定。"; return false; }
        var hit = hits[0];
        if (!TryMonsters(now, out var monsters, out reason) || !monsters.Any(m => m.Id == hit.Id && m.LocalIdentity == hit.LocalIdentity))
        { reason = LastReason = "怪物清單與畫面物件識別不一致，未改變鎖定。"; return false; }
        bool selected = TryLockMonster(hit.Id, now, out reason);
        if (selected && hit.Research) reason = LastReason = reason + "此次畫面點選使用候選投影，待人工比對。";
        return selected;
    }

    private sealed record Monster(ulong Id, string LocalIdentity, string? Name);
    private sealed record ActorRectangle(ulong Id, string LocalIdentity, string Kind, bool Visible,
        double X, double Y, double Width, double Height, bool Research = false);

    private bool TryMonsters(DateTimeOffset now, out List<Monster> monsters, out string reason)
    {
        monsters = [];
        if (!TryField("nearbyMonsters", now, out var value, out reason)) return false;
        if (!Array(value, "items", out var items, ActorCollectionContract.MaximumActors)) { reason = "怪物清單格式無效，黃點隱藏。"; return false; }
        var ids = new HashSet<ulong>();
        foreach (var item in items.EnumerateArray())
        {
            if (!Id(item, "id", out ulong id) || Text(item, "kind") != "Monster" || Text(item, "localIdentity") is not { Length: > 0 } identity || !ids.Add(id))
            { reason = "怪物清單識別無效或重複，黃點隱藏。"; return false; }
            monsters.Add(new(id, identity, Text(item, "name")));
        }
        reason = ""; return true;
    }

    private bool TryVerifiedRectangles(TargetLockClientFrame? window, DateTimeOffset now,
        out List<ActorRectangle> rectangles, out string reason)
    {
        rectangles = [];
        if (window is null || snapshot.Source is not { } source || window.ProcessId != source.ProcessId || window.CreationFileTime != source.CreationFileTime)
        { reason = "遊戲視窗來源尚未確認，黃點隱藏。"; return false; }
        if (!window.IsForeground || !window.IsVisible || window.Width <= 0 || window.Height <= 0)
        { reason = "LC 不在前景或視窗不可見，黃點隱藏。"; return false; }
        if (!TryField("actorViewport", now, out var value, out reason)) return false;
        if (!True(value, "currentCodeSemanticsVerified") || !True(value, "physicalPixelMappingVerified") ||
            Text(value, "coordinateSpace") != "LCClientPhysicalPixels")
        {
            if (allowResearchProjection) return TryCandidateRectangles(value, window, now, out rectangles, out reason);
            reason = "螢幕投影尚未驗證：黃點與畫面點選暫不可用；可先從怪物清單選定 ID。"; return false;
        }
        if (!True(value, "complete") || !Object(value, "clientSize", out var size) ||
            !Number(size, "width", out double width) || !Number(size, "height", out double height) ||
            width != window.Width || height != window.Height || !Array(value, "items", out var items))
        { reason = "已驗證視野樣本不完整或視窗尺寸已變，黃點隱藏。"; return false; }
        var ids = new HashSet<ulong>();
        foreach (var item in items.EnumerateArray())
        {
            if (!Id(item, "localId", out ulong id) || !ids.Add(id) || Text(item, "localIdentity") is not { Length: > 0 } identity ||
                Text(item, "kind") is not { Length: > 0 } kind || !True(item, "presentInSampledCollection") ||
                !item.TryGetProperty("inViewport", out var visible) || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { reason = "視野物件缺少唯一識別或可見性證據，黃點隱藏。"; return false; }
            if (visible.ValueKind == JsonValueKind.False) continue;
            if (!Object(item, "clientPixelRectangle", out var rect) || !Number(rect, "x", out double x) || !Number(rect, "y", out double y) ||
                !Number(rect, "width", out double w) || !Number(rect, "height", out double h) ||
                Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000 || w is <= 0 or > 65536 || h is <= 0 or > 65536)
            { reason = "可見物件缺少已驗證的實體像素矩形，黃點隱藏。"; return false; }
            rectangles.Add(new(id, identity, kind, true, x, y, w, h));
        }
        reason = ""; return true;
    }

    private bool TryCandidateRectangles(JsonElement value, TargetLockClientFrame window, DateTimeOffset now,
        out List<ActorRectangle> rectangles, out string reason)
    {
        rectangles = [];
        if (!True(value, "complete") || !Object(value, "rawFrame", out var frame) ||
            !Number(frame, "candidateWidth", out double cw) || !Number(frame, "candidateHeight", out double ch) ||
            !Number(frame, "scaleX", out double frameScaleX) || !Number(frame, "scaleY", out double frameScaleY) ||
            frameScaleX is <= 0 or > 64 || frameScaleY is <= 0 or > 64 ||
            !Object(frame, "rawRectangle", out var frameRect) || !Number(frameRect, "x", out double fx) || !Number(frameRect, "y", out double fy) ||
            fx != 0 || fy != 0 || !Number(frameRect, "width", out double fw) || !Number(frameRect, "height", out double fh) ||
            fw is <= 0 or > 65536 || fh is <= 0 or > 65536 ||
            cw is <= 0 or > 65536 || ch is <= 0 or > 65536 ||
            Math.Abs(fw / frameScaleX - cw) > 0.02 || Math.Abs(fh / frameScaleY - ch) > 0.02 ||
            !Array(value, "items", out var items))
        { reason = "研究投影缺少完整矩形樣本或有界畫面大小，黃點隱藏。"; return false; }
        double sx = window.Width / cw, sy = window.Height / ch;
        if (sx is < 0.05 or > 20 || sy is < 0.05 or > 20 || !TryMonsters(now, out var monsters, out reason))
        { reason = "研究投影縮放超出範圍或怪物來源無效，黃點隱藏。"; return false; }
        var monsterById = monsters.ToDictionary(m => m.Id);
        var ids = new HashSet<ulong>();
        foreach (var item in items.EnumerateArray())
        {
            if (!Id(item, "localId", out ulong id) || !ids.Add(id) || Text(item, "kind") is not { Length: > 0 } kind ||
                !True(item, "presentInSampledCollection"))
            { reason = "研究投影物件識別無效或重複，黃點隱藏。"; return false; }
            if (!Object(item, "correctedRectangleCandidate", out var rect)) continue;
            if (!Number(rect, "x", out double x) || !Number(rect, "y", out double y) ||
                !Number(rect, "width", out double w) || !Number(rect, "height", out double h) ||
                Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000 || w is <= 0 or > 16384 || h is <= 0 or > 16384)
            { reason = "研究投影包含無效矩形，黃點隱藏。"; return false; }
            if (x >= cw || y >= ch || x + w <= 0 || y + h <= 0) continue;
            // Same-source ID joins only. A viewport Monster without the fresh
            // matching classification makes the full candidate sample ambiguous.
            string identity = "viewport:" + id;
            if (kind == "Monster")
            {
                if (!monsterById.TryGetValue(id, out var monster))
                { reason = "研究投影與怪物清單不同步，黃點隱藏。"; return false; }
                identity = monster.LocalIdentity;
            }
            double clientY = CandidateOrigin == TargetLockCandidateOrigin.BottomLeft ? (ch - y - h) * sy : y * sy;
            rectangles.Add(new(id, identity, kind, true, x * sx, clientY, w * sx, h * sy, true));
        }
        reason = "候選投影：物理像素映射與遮蔽未驗證。"; return true;
    }

    private bool TryField(string key, DateTimeOffset now, out JsonElement value, out string reason)
    {
        value = default;
        if (snapshot.Source is null || snapshot.State is not "Reading") { reason = "遊戲資料來源尚未有效，黃點隱藏。"; return false; }
        if (!snapshot.Fields.TryGetValue(key, out var field) || !field.Fresh(now) || field.Value is not { ValueKind: JsonValueKind.Object } observed)
        { reason = $"{key} 尚未啟用、未知或已過期，黃點隱藏。"; return false; }
        value = observed; reason = ""; return true;
    }
    private static string? Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool True(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Id(JsonElement item, string key, out ulong id) { id = 0; return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out id) && id is > 0 and < ulong.MaxValue; }
    private static bool Number(JsonElement item, string key, out double number) { number = 0; return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number); }
    private static bool Object(JsonElement item, string key, out JsonElement value) { value = default; return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object; }
    private static bool Array(JsonElement item, string key, out JsonElement value, int maximum = 128) { value = default; return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum; }
}
