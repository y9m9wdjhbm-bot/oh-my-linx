using System.Diagnostics;
using System.Text.Json;
using MemoryHub.Reader;

namespace MemoryHub.Service;

public sealed partial class ShopBroker
{
    private Task<ShopCommandResponse>? navigationTask;

    // Called with gate held. Read-only polling is not a durable native action:
    // no per-poll request file, auto replay, collection refresh or native helper.
    private Task<ShopCommandResponse> StartNavigationRead(ShopCommandRequest req, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (navigationTask is { IsCompleted: false })
            return Task.FromResult(NavigationReply(req, "Busy", "上一筆導航讀取尚未完成；未增加佇列。"));
        navigationTask = Task.Run(() => ReadNavigationAsync(req, token), CancellationToken.None);
        return navigationTask;
    }

    private async Task<ShopCommandResponse> ReadNavigationAsync(ShopCommandRequest req, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        bool entered = false;
        try
        {
            // Yield to explicit shop work. Polling must not queue ahead of a
            // batch or slow every Buy/Sell page by repeatedly reading actors.
            entered = await nativeGate.WaitAsync(0, linked.Token).ConfigureAwait(false);
            if (!entered) return NavigationReply(req, "Busy", "商店清單讀取中；研究標記暫不更新。");
            lock (gate) if (scan.Running) return NavigationReply(req, "Busy", "批次搜尋中；研究標記暫不更新。");
            var (targets, expectedSource) = RequireTargets(req, requireDispatch: false);
            var owner = ShopTargetPolicy.Owner(targets, req.OwnerId!.Value, req.ExpectedOwnerIdentity);
            var selected = runtime.ReadSelection;
            var selfSnapshot = runtime.Snapshot;
            if (selfSnapshot.Source != expectedSource) throw new InvalidOperationException("NAVIGATION_SOURCE_CHANGED：自身快照来源已變更。");
            ShopNavigationPolicy.RequireSelf(selfSnapshot, targets.SelfId, DateTimeOffset.UtcNow);
            var reading = source.ReadShopNavigation(expectedSource, targets.SelfId, owner.Id, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                // Generation/source/selection closure also rejects a response
                // which raced a refresh, stop, character change or relocation.
                var (after, afterSource) = RequireTargets(req, requireDispatch: false);
                if (!ReferenceEquals(targets, after) || afterSource != expectedSource || !ReferenceEquals(selected, runtime.ReadSelection) || disposed)
                    throw new InvalidOperationException("NAVIGATION_CONTEXT_CHANGED：導航讀取途中保存名單、來源或設定已變更。");
                var current = runtime.Snapshot;
                var now = DateTimeOffset.UtcNow;
                string? map = current.Source == expectedSource && current.Fields.TryGetValue("mapName", out var field) && field.Fresh(now)
                    && field.Value is { ValueKind: JsonValueKind.String } mapValue ? mapValue.GetString() : null;
                var navigation = ShopNavigationPolicy.Compose(targets, owner, expectedSource, reading, selfSnapshot, now, map);
                var response = NavigationReply(req, navigation.State, navigation.Detail) with
                { Navigation = navigation, RequestDurationMs = timer.Elapsed.TotalMilliseconds };
                Audit("SHOP_NAVIGATION_READ", navigation.Detail, new { req.RequestId, req.TargetSnapshotId, req.OwnerId,
                    navigation.SampledAt, navigation.State, ElapsedMs = timer.Elapsed.TotalMilliseconds, CollectionRefreshed = false });
                return response;
            }
        }
        catch (OperationCanceledException)
        { return NavigationReply(req, "Cancelled", "導航讀取已取消；未更新保存名單。"); }
        catch (Exception error)
        {
            Audit("SHOP_NAVIGATION_UNAVAILABLE", error.Message, new { req.RequestId, req.TargetSnapshotId, req.OwnerId });
            return NavigationReply(req, "ProjectionUnavailable", error.Message) with { RequestDurationMs = timer.Elapsed.TotalMilliseconds };
        }
        finally { if (entered) nativeGate.Release(); }
    }

    private ShopCommandResponse NavigationReply(ShopCommandRequest req, string state, string detail) => new()
    {
        RequestId = req.RequestId, State = state, Detail = detail,
        CurrentSourceSessionKey = runtime.Snapshot.Source?.SessionKey
        // Intentionally no TargetSnapshot or Listings: a navigation result must
        // not act as a new manual refresh or mutate a client's market history.
    };
}

/// <summary>Pure join rules shared by the real broker and offline regressions.</summary>
internal static class ShopNavigationPolicy
{
    internal static ShopNavigationSnapshot Compose(ShopTargetSnapshot targets, MarketOwner owner, SourceIdentity expected,
        ShopNavigationReadResult reading, HubSnapshot selfSnapshot, DateTimeOffset now, string? mapName)
    {
        var selected = ShopTargetPolicy.Owner(targets, owner.Id, owner.LocalIdentity);
        if (selected != owner || targets.SourceSessionKey != expected.SessionKey || reading.Source != expected || reading.SelfId != targets.SelfId ||
            reading.SampledAt == default || reading.SampledAt > now || (now - reading.SampledAt).TotalMilliseconds > HubProtocol.FreshMilliseconds ||
            owner.Position is null || selfSnapshot.Source != expected)
            throw new InvalidOperationException("NAVIGATION_JOIN_REJECTED：導航來源、自身或保存店主不一致，未製造投影。");
        var (position, positionAt) = RequireSelf(selfSnapshot, targets.SelfId, now);
        return new()
        {
            SourceSessionKey = expected.SessionKey, TargetSnapshotId = targets.SnapshotId, OwnerId = owner.Id, OwnerIdentity = owner.LocalIdentity,
            SelfId = reading.SelfId, SampledAt = reading.SampledAt, SelfPositionSampledAt = positionAt, SavedTargetAt = owner.ObservedAt,
            SelfPosition = position, SavedOwnerPosition = owner.Position, MapName = mapName,
            State = reading.ProjectionAvailable && reading.RawViewport is not null ? "ResearchCandidate" : "ProjectionUnavailable",
            Detail = reading.Detail, RawViewport = reading.RawViewport,
            CurrentActorIdentityVerified = false, PhysicalPixelMappingVerified = false
        };
    }

    internal static (ShopPoint Position, DateTimeOffset SampledAt) RequireSelf(HubSnapshot snapshot, ulong expectedId, DateTimeOffset now)
    {
        var own = snapshot.Fields.GetValueOrDefault("selfId");
        var position = snapshot.Fields.GetValueOrDefault("position");
        if (own is null || position is null || !own.Fresh(now) || !position.Fresh(now) ||
            own.Value is not { ValueKind: JsonValueKind.Number } ownValue || !ownValue.TryGetUInt64(out ulong id) || id != expectedId ||
            position.Value is not { ValueKind: JsonValueKind.Object } point || !point.TryGetProperty("x", out var x) || !point.TryGetProperty("y", out var y) ||
            !x.TryGetInt32(out int px) || !y.TryGetInt32(out int py) || px is <= 0 or > 1_000_000 || py is <= 0 or > 1_000_000)
            throw new InvalidOperationException("NAVIGATION_SELF_UNAVAILABLE：Hub 自身 ID／座標過期或不符；導航不重新掃描角色集合。");
        return (new(px, py), position.SampledAt!.Value);
    }
}

