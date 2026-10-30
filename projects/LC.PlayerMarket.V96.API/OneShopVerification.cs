using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Explicit diagnostic: one existing shop listing request, no retries, trade, input or navigation.</summary>
internal static class OneShopVerification
{
    internal static async Task<int> RunAsync(ulong ownerId, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) return 2;
        Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ready = new TaskCompletionSource<HubSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CurrentLcSource? source = null;
        HubRuntime? runtime = null;
        ShopCommandResponse? refresh = null, response = null, brokerSettledState = null;
        SourceIdentity? bound = null;
        PendingEvidence[] pending = [];
        PersonalShopListing? before = null;
        var errors = new List<string>();
        bool stopped = false, brokerSettled = false;
        int attempts = 0;
        var backend = new StandaloneMarketBackend(Path.Combine(output, "state"),
            log => source = (CurrentLcSource)StandaloneMarketBackend.CreateDefaultSource(log),
            (rt, src, shops) => { runtime = rt; return new StandaloneShopCommands(new ShopBroker(rt, (CurrentLcSource)src, shops)); });
        backend.ConfigurePageWaitMilliseconds(new LocalStore().LoadPreferences().EffectivePageOpenIntervalMilliseconds);
        Task watcher = WatchAsync();
        try
        {
            var snapshot = await ready.Task.WaitAsync(deadline.Token);
            bound = snapshot.Source ?? throw new InvalidOperationException("SOURCE_REQUIRED");
            if (source is null || runtime is null || runtime.TransportEnabled) throw new InvalidOperationException("LOCAL_READER_REQUIRED");
            var read = source.ReadSlice(new ReadSelection(["personalShopListing"]), deadline.Token);
            var field = read.Fields.GetValueOrDefault("personalShopListing");
            if (read.Source != bound || field?.Fresh(DateTimeOffset.UtcNow) != true || field.Value is null)
                throw new InvalidOperationException("FRESH_BASELINE_REQUIRED");
            before = field.Value.Value.Deserialize<PersonalShopListing>(HubProtocol.Json);
            if (before is not { Complete: true, PageModeState: "MatchedExclusiveBankAndRows" }
                || before.PageMode is not (ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell)
                || before.OwnerIdRaw != ownerId) throw new InvalidOperationException("OPEN_VIEWER_BUY_PAGE_FOR_EXACT_OWNER_REQUIRED");
            string direction=before.PageMode!;
            refresh = await backend.SendAsync(new ShopCommandRequest { Command = ShopCommands.RefreshTargets, ExpectedSourceSessionKey = bound.SessionKey }, deadline.Token);
            var targets = refresh.TargetSnapshot;
            var owner = targets?.Owners.SingleOrDefault(x => x.Id == ownerId);
            if (targets is not { CanDispatch: true } || owner is null || runtime.Snapshot.Source != bound)
                throw new InvalidOperationException("CURRENT_OWNER_TARGET_REQUIRED");
            attempts++;
            response = await backend.SendAsync(new ShopCommandRequest {
                Command = ShopCommands.OpenShop, OwnerId = ownerId, ViewerDirection = direction,
                ExpectedSourceSessionKey = bound.SessionKey, TargetSnapshotId = targets.SnapshotId, ExpectedOwnerIdentity = owner.LocalIdentity
            }, deadline.Token);
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        finally
        {
            // OpenShop's response was made while its native gate was held. Preserve
            // that response, then observe the broker after OpenAsync's finally.
            if (bound is not null && runtime is not null)
            {
                using var settleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    var stateRequest = new ShopCommandRequest { Command = ShopCommands.GetState, ExpectedSourceSessionKey = bound.SessionKey };
                    brokerSettledState = await backend.SendAsync(stateRequest, settleDeadline.Token);
                    string stem = bound.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
                        + bound.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string shared = Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal", stem);
                    pending = [
                        new(Path.Combine(output, "state", "shops", "sessions", stem + ".pending.json"), false),
                        new(shared + ".pending.json", false),
                        new(shared + ".v15-singletrial.pending.json", false)
                    ];
                    pending = pending.Select(item => item with { Exists = File.Exists(item.Path) }).ToArray();
                    brokerSettled = brokerSettledState.RequestId == stateRequest.RequestId && brokerSettledState.State == "State"
                        && brokerSettledState.CurrentSourceSessionKey == bound.SessionKey && runtime.Snapshot.Source == bound
                        && brokerSettledState.WorkSettled && !brokerSettledState.SessionBlocked && !brokerSettledState.Scan.Running
                        && pending.All(item => !item.Exists);
                    if (!brokerSettled) errors.Add("FINAL_BROKER_SETTLEMENT_NOT_CONFIRMED");
                }
                catch (Exception error) { errors.Add("Broker 收尾確認：" + error); }
            }
            deadline.Cancel();
            try { await watcher; } catch (OperationCanceledException) { } catch (Exception error) { errors.Add(error.ToString()); }
            try { await backend.DisposeAsync(); stopped = runtime is null || !runtime.IsRunning; } catch (Exception error) { errors.Add(error.ToString()); }
        }
        bool success = attempts == 1 && brokerSettled && stopped && errors.Count == 0 && response is { NativeEntered: true, NativeReturned: true, ResponseVerified: true, SessionBlocked: false };
        var report = new { At = DateTimeOffset.UtcNow, Version = "83.0.0", Success = success, OwnerId = ownerId,
            RequestAttempts = attempts, MaximumRequests = 1, AutomaticRetries = 0, InputSent = 0, TradeCommands = 0,
            Before = before, RefreshState = refresh?.State, Response = response,
            BrokerSettledState = brokerSettledState, BrokerSettled = brokerSettled, PendingEvidence = pending, Stopped = stopped, Errors = errors };
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(HubProtocol.Json) { WriteIndented = true }));
        return success ? 0 : 1;

        async Task WatchAsync()
        {
            try { await foreach (var value in backend.WatchAsync(deadline.Token)) if (value.Source is not null) ready.TrySetResult(value); }
            catch (Exception error) { ready.TrySetException(error); throw; }
        }
    }

    private sealed record PendingEvidence(string Path, bool Exists);
}
