using System.Text.Json;

namespace MemoryHub.Reader;

/// <summary>Service-only closed observation. Contains no process handles or addresses.</summary>
public sealed record ShopNavigationReadResult(SourceIdentity Source, DateTimeOffset SampledAt, ulong SelfId,
    JsonElement? RawViewport, bool ProjectionAvailable, string Detail);

public sealed partial class CurrentLcSource
{
    public ShopNavigationReadResult ReadShopNavigation(SourceIdentity expectedSource, ulong expectedSelfId, ulong targetId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            var configured = Volatile.Read(ref selection);
            string[] required = ["selfId", "position", "actorViewport"];
            if (required.Any(key => !configured.Enabled(key)))
                throw new InvalidOperationException("NAVIGATION_FIELD_DISABLED：導航需要自身 ID、自身座標及矩形欄位；未暗中開啟。");
            var profile = activeProfile;
            if (profileError is not null || profile is null || required.Any(key => !profile.Allows(key)))
                throw new InvalidOperationException("NAVIGATION_PROFILE_UNCONFIRMED：導航所需欄位尚未保存已確認的版本設定。");
            if (expectedSelfId == 0 || targetId == 0 || expectedSelfId == targetId)
                throw new ArgumentException("NAVIGATION_TARGET_INVALID：需要不同的自身及保存店主 ID。");

            SelectedViewportObservation? viewport = null;
            // actorViewport is allowed by the configured/profile gate, but is
            // intentionally absent from the slice so CaptureActors cannot run.
            var result = ReadProfile(token, profile, new ReadSelection(["selfId"]), configured,
                reuseMetadata: metadataReuse,
                readNavigation: (memory, journal, budget) => viewport = ViewportFieldReader.CaptureSelected(memory, journal, budget, expectedSelfId, targetId));
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(configured, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile))
                throw new InvalidOperationException("NAVIGATION_CONFIGURATION_CHANGED：採樣中讀取設定已變更。");
            var now = DateTimeOffset.UtcNow;
            var own = result.Fields.GetValueOrDefault("selfId");
            if (result.Source != expectedSource || viewport is null || own is null ||
                !own.Fresh(now) || viewport.SampledAt > now || (now - viewport.SampledAt).TotalMilliseconds > HubProtocol.FreshMilliseconds)
                throw new InvalidOperationException("NAVIGATION_SOURCE_UNAVAILABLE：未取得新鮮且來源完整閉合的自身／導航資料；" + result.Detail);
            if (own.Value is not { ValueKind: JsonValueKind.Number } ownValue || !ownValue.TryGetUInt64(out ulong id) || id != expectedSelfId)
                throw new InvalidOperationException("NAVIGATION_SOURCE_CHANGED：自身 ID 已不同於保存的商店名單。");
            return new(result.Source!, viewport.SampledAt, id, viewport.Value, viewport.ProjectionAvailable, viewport.Detail);
        }
    }
}
