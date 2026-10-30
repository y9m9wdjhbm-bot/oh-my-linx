using System.Text.Json.Serialization;

namespace LcPlayerMarket;

internal sealed record PatrolWaypoint(
    [property: JsonRequired] string PointId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string MapId,
    [property: JsonRequired] string MapName,
    [property: JsonRequired] int X,
    [property: JsonRequired] int Y);

/// <summary>The array is the route order. A saved definition never carries execution permission.</summary>
internal sealed record PatrolRouteSnapshot
{
    [JsonRequired] public int Schema { get; init; } = 1;
    [JsonRequired] public string Revision { get; init; } = "";
    [JsonRequired] public DateTimeOffset SavedAt { get; init; }
    [JsonRequired] public int RetryLimit { get; init; } = 3;
    // Missing in legacy schema 1 revisions: default without rewriting the saved file.
    // Explicit null or a non-integer remains a JSON error.
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int ArrivalRadius { get; init; } = PatrolRouteRules.DefaultArrivalRadius;
    // Kept outside legacy route JSON so V48/V49 can still read the same route revisions.
    [JsonIgnore] public int RoundCount { get; init; } = 1;
    [JsonRequired] public PatrolWaypoint[] Points { get; init; } = [];
}

internal sealed record PatrolRouteLoadResult(PatrolRouteSnapshot? Snapshot, string? Error)
{
    public bool Success => Snapshot is not null && Error is null;
}

internal static class PatrolRouteRules
{
    internal const int DefaultArrivalRadius = 2;
    internal const int MinimumArrivalRadius = 1;
    internal const int MaximumArrivalRadius = 10;
    internal const int MaximumPoints = 128;

    internal static void Validate(PatrolRouteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Schema != 1 || !Guid.TryParseExact(snapshot.Revision, "N", out var revision) || revision == Guid.Empty
            || snapshot.SavedAt == default || snapshot.SavedAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("巡迴清單版本、修訂識別或保存時間無效。");
        ValidatePoints(snapshot.Points, snapshot.RetryLimit);
        ValidateArrivalRadius(snapshot.ArrivalRadius);
        ValidateRoundCount(snapshot.RoundCount);
    }

    internal static void ValidateRoundCount(int count)
    {
        if (count is < 1 or > 100) throw new InvalidDataException("巡迴輪數必須介於 1 與 100。");
    }

    internal static void ValidateArrivalRadius(int radius)
    {
        if (radius is < MinimumArrivalRadius or > MaximumArrivalRadius)
            throw new InvalidDataException("到點容差必須介於 1 至 10 格。");
    }

    internal static void ValidatePoints(IReadOnlyList<PatrolWaypoint> points, int retryLimit)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (retryLimit is < 0 or > 10) throw new InvalidDataException("錯誤後接續次數必須介於 0 至 10。");
        if (points.Count > MaximumPoints) throw new InvalidDataException($"定位點最多保存 {MaximumPoints} 個。");
        var identities = new HashSet<Guid>();
        foreach (var point in points)
        {
            ValidateWaypoint(point);
            if (!identities.Add(Guid.Parse(point.PointId))) throw new InvalidDataException("定位點識別重複，沒有套用變更。");
        }
    }

    internal static void ValidateWaypoint(PatrolWaypoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (point.PointId is null || point.PointId.Length > 36 || !Guid.TryParse(point.PointId, out var id) || id == Guid.Empty)
            throw new InvalidDataException("定位點識別必須是有效 GUID。");
        RequireText(point.Name, 80, "定位點名稱", required: true);
        RequireText(point.MapId, 128, "地圖 ID", required: true);
        RequireText(point.MapName, 128, "地圖名稱", required: false);
        if (point.X is < 0 or > 65535 || point.Y is < 0 or > 65535)
            throw new InvalidDataException("定位點 X、Y 座標必須介於 0 至 65535。");
    }

    private static void RequireText(string? value, int maximum, string label, bool required)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl)
            || required && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{label}無效或超出 {maximum} 字元。");
    }

    internal static PatrolRouteSnapshot Copy(PatrolRouteSnapshot value) => value with
        { Points = value.Points.Select(point => point with { }).ToArray() };
}
