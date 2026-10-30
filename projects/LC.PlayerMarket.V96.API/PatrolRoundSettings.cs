using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LcPlayerMarket;

/// <summary>Round count is bound to an immutable legacy-compatible route revision.
/// The route file is the commit point; orphan sidecars are never discovered or adopted.</summary>
internal static class PatrolRoundSettings
{
    private sealed record Settings
    {
        [JsonRequired] public int Schema { get; init; }
        [JsonRequired] public string Revision { get; init; } = "";
        [JsonRequired] public string RouteSha256 { get; init; } = "";
        [JsonRequired] public int RoundCount { get; init; }
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
          NumberHandling = JsonNumberHandling.Strict };

    internal static string SidecarPath(string routePath, string revision) =>
        Path.Combine(Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(routePath))!)!.FullName,
            "patrol-rounds", revision + ".rounds.json");

    internal static PatrolRouteSnapshot Read(string routePath, PatrolRouteSnapshot route, byte[] routeBytes)
    {
        string path = SidecarPath(routePath, route.Revision);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException("巡迴輪數設定不是檔案。");
            return route with { RoundCount = 1 };
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 4096) throw new InvalidDataException("巡迴輪數設定大小無效。");
        var value = JsonSerializer.Deserialize<Settings>(file, Json)
            ?? throw new InvalidDataException("巡迴輪數設定為空。");
        if (value.Schema != 1 || value.Revision != route.Revision
            || value.RouteSha256 != Convert.ToHexString(SHA256.HashData(routeBytes)))
            throw new InvalidDataException("巡迴輪數設定與路線修訂或內容不符；沒有改用預設輪數。");
        PatrolRouteRules.ValidateRoundCount(value.RoundCount);
        return route with { RoundCount = value.RoundCount };
    }

    internal static void Commit(string routePath, PatrolRouteSnapshot route, byte[] routeBytes)
    {
        PatrolRouteRules.Validate(route);
        string sidecar = SidecarPath(routePath, route.Revision);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        var settings = new Settings { Schema = 1, Revision = route.Revision,
            RouteSha256 = Convert.ToHexString(SHA256.HashData(routeBytes)), RoundCount = route.RoundCount };
        byte[] settingsBytes = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
        if (File.Exists(sidecar))
        {
            // Retry an interrupted import only when its prepared immutable sidecar is byte-for-byte identical.
            // Callers hold writer.lock; never overwrite or adopt a different orphan.
            using var prepared = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (prepared.Length != settingsBytes.Length) throw new InvalidDataException("既有巡迴輪數設定與準備提交內容不符。");
            byte[] existing = new byte[settingsBytes.Length]; prepared.ReadExactly(existing);
            if (!existing.AsSpan().SequenceEqual(settingsBytes)) throw new InvalidDataException("既有巡迴輪數設定與準備提交內容不符。");
        }
        else WriteImmutable(sidecar, settingsBytes);
        // Publish the route last. Failed publication leaves an unreferenced sidecar, not a new current route.
        WriteImmutable(routePath, routeBytes);
    }

    private static void WriteImmutable(string final, byte[] bytes)
    {
        string temporary = final + ".writing-" + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, final, overwrite: false);
    }
}
