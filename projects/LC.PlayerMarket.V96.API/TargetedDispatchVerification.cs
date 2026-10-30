using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Fake reader delegates and actual context/policy validation; reflection keeps the unchanged baseline buildable for RED.</summary>
internal static class TargetedDispatchVerification
{
    private sealed record FakeActor(ulong Id);
    private static readonly string[] Keys = ["selfId", "position", "nearbyPlayers", "playerShopStates", "personalShopListing"];
    private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed),
            checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new("targeted_dispatch_" + name, true, "Synthetic reads/context only; process starts=0; LC reads=0; native requests=0.")); }
            catch (Exception error) { checks.Add(new("targeted_dispatch_" + name, false, error.ToString())); }
        }
        Check("public_target_overload_and_legacy_overload", () =>
        {
            Need(typeof(CurrentLcSource).GetMethod("ResolveShopDispatchSource", [typeof(SourceIdentity), typeof(CancellationToken)]) is not null, "Legacy reader overload was removed.");
            Need(typeof(CurrentLcSource).GetMethod("ResolveShopDispatchSource", [typeof(SourceIdentity), typeof(CancellationToken), typeof(ulong)]) is not null, "Target reader overload is missing.");
            Need(typeof(ShopDispatchSample).GetProperty("SelectedOwnerId")?.PropertyType == typeof(ulong?), "Service sample has no explicit selected-owner marker.");
        });
        Check("legacy_leaf_plan_reads_all_actors_then_closes", () =>
        {
            var read = new List<ulong>(); bool closed = false;
            LeafPlan([new(11), new(24), new(25)], null, a => read.Add(a.Id), () => { Need(read.SequenceEqual(new ulong[] { 11, 24, 25 }), "Full capture lost leaves."); closed = true; });
            Need(closed, "Full capture skipped closure.");
        });
        Check("target_only_leaf_reads_but_full_closure_mandatory", () =>
        {
            var read = new List<ulong>(); int closures = 0;
            LeafPlan([new(11), new(24), new(25)], 24, a => { if (a.Id != 24) throw new IOException("Unrelated player's leaf must never be read."); read.Add(a.Id); },
                () => { closures++; Need(read.SequenceEqual(new ulong[] { 24 }), "Target leaf was not captured before closure."); });
            Need(closures == 1 && read.Count == 1, "Target leaf or original closure was skipped/duplicated.");
        });
        Check("target_absence_closes_without_replacement_leaf", () =>
        {
            int leaves = 0, closures = 0;
            LeafPlan([new(11), new(25)], 24, _ => leaves++, () => closures++);
            Need(leaves == 0 && closures == 1, "Absent target selected another actor or skipped closure.");
        });
        Check("duplicate_target_fails_before_any_leaf_or_publication", () =>
        {
            int leaves = 0, publications = 0;
            ProbeCode(Reject(() => LeafPlan([new(24), new(24)], 24, _ => leaves++, () => publications++)), "ACTOR_ID_AMBIGUOUS");
            Need(leaves == 0 && publications == 0, "Duplicate ID was allowed to publish.");
        });
        Check("unrelated_actor_membership_change_still_rejects", () =>
        {
            bool published = false; int leaves = 0;
            var fence = typeof(CurrentLcSource).Assembly.GetType("MemoryHub.Reader.ActorMembershipFence")!.GetMethod("Verify", Static)!;
            var proofs = new (ulong Address, byte[] Expected)[] { (0x10000, new byte[] { 1, 2, 3, 4 }) };
            ProbeCode(Reject(() => LeafPlan([new(24), new(25)], 24, _ => leaves++, () =>
            {
                Invoke(fence, null, [proofs, new Func<ulong, int, byte[]>((_, _) => [1, 2, 3, 9]), (Action)(() => { }), new Action<ulong, int>((_, _) => { })]);
                published = true;
            })), "ACTOR_COLLECTION_CHANGED");
            Need(leaves == 1 && !published, "Target-only leaves weakened the full membership fence.");
        });
        Check("unrelated_actor_identity_change_still_rejects", () =>
        {
            bool published = false;
            var assembly = typeof(CurrentLcSource).Assembly;
            var closure = assembly.GetType("MemoryHub.Reader.ActorFinalProofClosure")!;
            var proofType = closure.GetNestedType("Proof", BindingFlags.Public | BindingFlags.NonPublic)!;
            var proof = Activator.CreateInstance(proofType, 0x10000UL, new byte[] { 1 }, false, false, false, "ActorId")!;
            var proofs = Array.CreateInstance(proofType, 1); proofs.SetValue(proof, 0);
            // No changed callback is required before the production closure throws on mismatching proof bytes.
            var changed = ChangedCallback(proofType);
            ProbeCode(Reject(() => LeafPlan([new(24), new(25)], 24, _ => { }, () =>
            {
                Invoke(closure.GetMethod("Verify", Static)!, null, [proofs, new Func<ulong, int, byte[]>((_, _) => [2]), (Action)(() => { }), changed]);
                published = true;
            })), "ACTOR_COLLECTION_CHANGED");
            Need(!published, "Unrelated actor identity mutation escaped final proof closure.");
        });
        Check("leaf_failure_cannot_publish_partial_success", () =>
        {
            bool published = false;
            Reject(() => LeafPlan([new(24), new(25)], 24, _ => throw new IOException("selected read failed"), () => published = true));
            Need(!published, "Failed target leaves reached publication.");
        });

        Check("full_refresh_context_remains_complete", () =>
        {
            var f = new Fixture(); var result = f.Result(partial: false);
            var context = ShopRuntimeContext.FromFreshResult(result, f.Source, f.Selection, f.Source.SessionKey, f.Now, requireShopStates: true);
            context.RequireFreshForDispatch(f.Now);
            Need(context.Players.Length == 1 && ShopDispatchPolicy.RequireOwner(context, f.Targets, f.Owner, f.Now, false).Id == 24, "Legacy full-list context changed.");
        });
        Check("full_consumer_rejects_private_partial_scope", () =>
        {
            var f = new Fixture();
            Reject(() => ShopRuntimeContext.FromFreshResult(f.Result(), f.Source, f.Selection, f.Source.SessionKey, f.Now, requireShopStates: true));
        });
        Check("complete_true_cannot_promote_selected_scope", () =>
        {
            var f = new Fixture();
            Reject(() => ShopRuntimeContext.FromFreshResult(f.Result(complete: true), f.Source, f.Selection, f.Source.SessionKey, f.Now, requireShopStates: true));
        });
        Check("selected_context_requires_exact_shop_evidence_and_rechecks_freshness", () =>
        {
            var f = new Fixture(); var context = Selected(f, f.Result(), 24);
            context.RequireFreshForDispatch(f.Now);
            Need(ShopDispatchPolicy.RequireOwner(context, f.Targets, f.Owner, f.Now, false).ShopState == ShopEligibilityPolicy.ShopActionObserved, "Closed selected ShopAction was rejected.");
            Reject(() => context.RequireFreshForDispatch(f.Now.AddMilliseconds(HubProtocol.FreshMilliseconds + 1)));
        });
        Check("closed_absent_target_preserves_target_not_nearby_failure", () =>
        {
            var f = new Fixture(); var context = Selected(f, f.Result(absent: true), 24);
            var error = Reject(() => ShopDispatchPolicy.RequireOwner(context, f.Targets, f.Owner, f.Now, false));
            Need(error is ShopRequestFailure { Failure.Code: "TargetNoLongerNearby", Failure.Scope: "Target" }, "Closed target absence became a global retry or substitute actor.");
        });
        foreach (string state in new[] { ShopEligibilityPolicy.Unknown, ShopEligibilityPolicy.KnownOtherActionObserved })
            Check("selected_" + state + "_never_dispatches", () =>
            {
                var f = new Fixture(); var context = Selected(f, f.Result(shopState: state), 24);
                PolicyCode(Reject(() => ShopDispatchPolicy.RequireOwner(context, f.Targets, f.Owner, f.Now, false)),
                    state == ShopEligibilityPolicy.Unknown ? "TargetShopStateUnknown" : "TargetNotShoppingObserved", "Target");
            });
        foreach (string mutation in new[] { "foreign_marker", "foreign_item", "duplicate_item", "incomplete_identity", "selected_complete", "different_sample_time", "wrong_identity", "non_player", "stale_action", "players_empty", "states_empty", "state_identity", "full_with_marker", "partial_without_marker" })
            Check("reject_" + mutation, () =>
            {
                var f = new Fixture();
                Reject(() => { var context = Selected(f, f.Result(mutation: mutation), 24); context.RequireFreshForDispatch(f.Now); ShopDispatchPolicy.RequireOwner(context, f.Targets, f.Owner, f.Now, false); });
            });
        foreach (ulong ownerId in new[] { 0UL, 11UL, (ulong)int.MaxValue + 1 })
            Check("reject_invalid_or_self_" + ownerId, () =>
            {
                var f = new Fixture(); Reject(() => Selected(f, f.Result(marker: ownerId, rowId: ownerId), ownerId));
            });
        Check("normal_prepared_target_sample_reaches_original_owner_gate", () =>
        {
            var f = new Fixture(); var sample = f.Sample(true);
            var prepared = Capture(f, sample, 24);
            Need(ShopDispatchPolicy.RequireOwner(prepared.Context, f.Targets, f.Owner, f.Now).Id == 24, "Normal preparation changed the requested owner.");
            PolicyCode(Reject(() => ShopDispatchPolicy.RequireOwner(prepared.Context, f.Targets with { CanDispatch = false }, f.Owner, f.Now)), "TargetRefreshRequired", "Global");
        });
        Check("force_preparation_preserves_selected_owner_and_shop_gate", () =>
        {
            var f = new Fixture(); var sample = f.Sample(true); int calls = 0;
            Func<ShopDispatchSample> read = () => { calls++; return sample; };
            var method = typeof(ShopForceDispatchPolicy).GetMethods(Static).SingleOrDefault(m => m.Name == "PrepareShoppingDispatch" && m.GetParameters().Length == 8)
                ?? throw new InvalidOperationException("Force preparation has no expected selected-owner parameter.");
            var prepared = (ShopPreparedDispatch)Invoke(method, null, [read, f.Source, f.Selection, f.Targets, f.Owner, new Func<DateTimeOffset>(() => f.Now), null, (ulong?)24])!;
            Need(calls == 1 && prepared.Context.Players.Single().Id == 24, "Force route read more than once or used another owner.");
        });
        Check("unscoped_capture_cannot_accept_target_sample", () =>
        {
            var f = new Fixture(); var sample = f.Sample(true); Reject(() => Capture(f, sample, null));
        });
        Check("target_capture_cannot_accept_legacy_full_sample", () =>
        {
            var f = new Fixture(); Reject(() => Capture(f, f.Sample(false), 24));
        });
        Check("sample_marker_and_expected_owner_must_match", () =>
        {
            var f = new Fixture(); Reject(() => Capture(f, f.Sample(true), 25));
        });
        foreach (string route in new[] { "OpenAsync", "OpenForcedAsync" })
            Check("compiled_" + route + "_passes_actual_owner_id", () => VerifyBrokerRoute(route));
        foreach (string mutation in new[] { "binding_source", "observation_source", "no_source_read", "baseline_time", "baseline_source", "baseline_owner", "binding_time" })
            Check("prepared_reject_" + mutation, () =>
            {
                var f = new Fixture(); var sample = f.Sample(true);
                var foreign = f.Source with { ProcessId = 4243 };
                sample = mutation switch {
                    "binding_source" => sample with { Binding = sample.Binding with { Source = foreign } },
                    "observation_source" => sample with { Observations = sample.Observations with { Source = foreign } },
                    "no_source_read" => sample with { Observations = sample.Observations with { NoSourceRead = true } },
                    "baseline_time" => sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { SampledAt = f.Now.AddMilliseconds(-1) } } },
                    "baseline_source" => sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { SourceSessionKey = "foreign" } } },
                    "baseline_owner" => sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { OwnerIdRaw = 25 } } },
                    "binding_time" => sample with { Binding = sample.Binding with { SampledAt = f.Now.AddSeconds(-3) } },
                    _ => throw new InvalidOperationException("Unknown fixture mutation.") };
                Reject(() => Capture(f, sample, 24));
            });
        foreach (string key in Keys)
        {
            Check("prepared_reject_stale_" + key, () =>
            {
                var f = new Fixture(); var sample = f.Sample(true);
                sample.Observations.Fields[key] = sample.Observations.Fields[key] with { SampledAt = f.Now.AddSeconds(-3) };
                Reject(() => Capture(f, sample, 24));
            });
            Check("prepared_reject_disabled_" + key, () =>
            {
                var f = new Fixture(); var sample = f.Sample(true);
                Reject(() => Capture(f, sample, 24, new ReadSelection(Keys.Where(k => k != key))));
            });
        }
        Check("state_identity_mismatch_preserves_unknown_and_owner_identity_mismatch_changed", () =>
        {
            var f = new Fixture();
            PolicyCode(Reject(() => ShopDispatchPolicy.RequireOwner(Selected(f, f.Result(mutation: "state_identity"), 24), f.Targets, f.Owner, f.Now)), "TargetShopStateUnknown", "Target");
            PolicyCode(Reject(() => ShopDispatchPolicy.RequireOwner(Selected(f, f.Result(mutation: "wrong_identity"), 24), f.Targets, f.Owner, f.Now)), "TargetChanged", "Target");
        });
        return checks.ToArray();
    }

    private static ShopPreparedDispatch Capture(Fixture f, ShopDispatchSample sample, ulong? target, ReadSelection? selected = null)
    {
        var method = typeof(ShopPreparedDispatch).GetMethods(Static).SingleOrDefault(m => m.Name == "Capture" && m.GetParameters().Length == 7)
            ?? throw new InvalidOperationException("Prepared capture has no selected-owner contract.");
        return (ShopPreparedDispatch)Invoke(method, null, [new Func<ShopDispatchSample>(() => sample), f.Source, selected ?? f.Selection, f.Source.SessionKey, new Func<DateTimeOffset>(() => f.Now), null, target])!;
    }
    private static ShopRuntimeContext Selected(Fixture f, ReadResult result, ulong target)
    {
        var method = typeof(ShopRuntimeContext).GetMethod("FromSelectedDispatchResult", Static)
            ?? throw new InvalidOperationException("Selected dispatch context is missing.");
        return (ShopRuntimeContext)Invoke(method, null, [result, f.Source, f.Selection, f.Source.SessionKey, f.Now, target, null])!;
    }
    private static void LeafPlan(FakeActor[] actors, ulong? target, Action<FakeActor> leaf, Action closure)
    {
        var type = typeof(CurrentLcSource).Assembly.GetType("MemoryHub.Reader.ActorDispatchLeafPlan")
            ?? throw new InvalidOperationException("Target leaf capture plan is missing.");
        var method = type.GetMethod("Run", Static)!.MakeGenericMethod(typeof(FakeActor));
        Invoke(method, null, [actors, target, new Func<FakeActor, ulong>(a => a.Id), leaf, closure]);
    }
    private static Delegate ChangedCallback(Type proofType)
        => typeof(TargetedDispatchVerification).GetMethod(nameof(IgnoreChanged), Static)!.MakeGenericMethod(proofType)
            .CreateDelegate(typeof(Action<>).MakeGenericType(proofType));
    private static void IgnoreChanged<T>(T value) { }
    private static object? Invoke(MethodInfo method, object? target, object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner) { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private static Exception Reject(Action action)
    {
        try { action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Unsafe or malformed selected sample was accepted.");
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PolicyCode(Exception error, string code, string scope)
        => Need(error is ShopRequestFailure failure && failure.Failure.Code == code && failure.Failure.Scope == scope,
            $"Expected {scope}/{code}; got {error.GetType().Name}: {error.Message}");
    private static void ProbeCode(Exception error, string code)
        => Need(error.GetType().FullName == "MemoryHub.Reader.ProbeFailure" && error.GetType().GetProperty("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(error) as string == code,
            $"Expected ProbeFailure/{code}; got {error.GetType().Name}: {error.Message}");

    // Verify the real compiled broker lambda, without starting a broker/helper or reading LC.
    private static void VerifyBrokerRoute(string route)
    {
        if (route == "OpenAsync")
        {
            var entry = typeof(ShopBroker).GetMethod("OpenAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Need(CalledMethods(entry).Any(m => m.DeclaringType == typeof(ShopBroker) && m.Name == "OpenForcedAsync"),
                "Ordinary open does not use the shared fixed-list one-read pipeline.");
            return;
        }
        static IEnumerable<Type> Types(Type root) => new[] { root }.Concat(root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(Types));
        var matching = Types(typeof(ShopBroker)).SelectMany(t => t.GetMethods(BindingFlags.Instance | Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("<" + route + ">", StringComparison.Ordinal)).Select(m => CalledMethods(m).ToArray())
            .Where(calls => calls.Any(c => c.DeclaringType == typeof(CurrentLcSource) && c.Name == "ResolveFrozenShopDispatchSource")).ToArray();
        Need(matching.Length > 0, "Actual broker read lambda was not found.");
        foreach (var calls in matching)
            for (int i = 0; i < calls.Length; i++)
                if (calls[i].DeclaringType == typeof(CurrentLcSource) && calls[i].Name == "ResolveFrozenShopDispatchSource")
                {
                    var args = calls[i].GetParameters();
                    Need(args.Length == 3 && args[2].ParameterType == typeof(ulong), "Actual broker still invokes the full-list overload.");
                    Need(i > 0 && calls[i - 1].DeclaringType == typeof(MarketOwner) && calls[i - 1].Name == "get_Id", "Actual broker does not pass the selected owner Id.");
                }
    }
    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        for (int at = 0; at < bytes.Length;)
        {
            ushort key = bytes[at++]; if (key == 0xfe) key = (ushort)(0xfe00 | bytes[at++]);
            var op = opcodes[key];
            int size = op.OperandType switch {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(bytes, at)),
                _ => 4 };
            Need(at + size <= bytes.Length, "Malformed IL operand.");
            if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt))
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, at), method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments())!;
            at += size;
        }
    }

    private sealed class Fixture
    {
        internal readonly SourceIdentity Source = new(4242, 987654321, "offline-LC", "0x100000", 1, 4096, new string('A', 64), "offline-targeted-dispatch");
        internal readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal ReadSelection Selection => new(Keys);
        internal MarketOwner Owner => new() { Id=24, LocalIdentity="owner-24", Name="synthetic shop", Position=new(100,101), ObservedAt=Now,
            ActionType=".?AVShopAction@lineage@@", ShopState=ShopEligibilityPolicy.ShopActionObserved,
            ShopStateSampledAt=Now, ShopStateEvidence=ShopEligibilityPolicy.ActionEvidence };
        internal ShopTargetSnapshot Targets => new() { SnapshotId="offline-targets",SourceSessionKey=Source.SessionKey,RefreshedAt=Now,SelfId=11,SelfPosition=new(100,100),Owners=[Owner],CanDispatch=true };
        internal ReadResult Result(bool partial=true, bool complete=false, bool absent=false, string shopState="ShopActionObserved", string mutation="", ulong marker=24, ulong rowId=24)
        {
            if(mutation=="foreign_marker")marker=25;
            if(mutation=="foreign_item")rowId=25;
            string identity=mutation=="wrong_identity"?"replacement":"owner-24";
            var player = new { Id=rowId,LocalIdentity=identity,Kind=mutation=="non_player"?"Npc":"Player",RawType=".?AVPlayer@lineage@@",
                Name="synthetic shop",NameState="Observed",Position=new {X=100,Y=101},PositionState="Observed",SampledAt=Now };
            object[] players=absent||mutation=="players_empty"?[]:mutation=="duplicate_item"?[player,player]:[player];
            var state = new { Id=rowId,LocalIdentity=mutation=="state_identity"?"different-state-owner":identity,ActionType=shopState=="ShopActionObserved"?".?AVShopAction@lineage@@":".?AVMovingAction@lineage@@",
                ShopState=shopState,ShopStateSampledAt=mutation=="stale_action"?Now.AddSeconds(-3):Now,
                ShopStateEvidence=ShopEligibilityPolicy.ActionEvidence,ShopStateDetail="synthetic exact-action proof" };
            object[] states=absent||mutation=="states_empty"?[]:[state];
            object Shape(object[] items)=>new { Complete=!partial||complete||mutation is "selected_complete" or "full_with_marker",Items=items,
                Scope=partial&&mutation!="full_with_marker"?"SelectedDispatchTarget":"LocalActorCollections",SelectedOwnerId=partial&&mutation!="partial_without_marker"?(ulong?)marker:null,
                FullIdentityCollectionVerified=mutation!="incomplete_identity",ScreenVisibilityVerified=false };
            var baseline=new PersonalShopListing {SourceSessionKey=Source.SessionKey,SampledAt=Now,OwnerIdRaw=24};
            return new(Source,new Dictionary<string,FieldReading> {
                ["selfId"]=FieldReading.Observed(11UL,Now),["position"]=FieldReading.Observed(new {X=100,Y=100},Now),
                ["nearbyPlayers"]=FieldReading.Observed(Shape(players),Now),
                ["playerShopStates"]=FieldReading.Observed(Shape(states),mutation=="different_sample_time"?Now.AddMilliseconds(-1):Now),
                ["personalShopListing"]=FieldReading.Observed(baseline,Now)},"Reading","synthetic source and full identity closure");
        }
        internal ShopDispatchSample Sample(bool partial)
        {
            var result=Result(partial);
            var baseline=result.Fields["personalShopListing"].Value!.Value.Deserialize<PersonalShopListing>(HubProtocol.Json)!;
            var sample=new ShopDispatchSample(new(Source,Now,0x20000,24,0x30000,0x40000,0x50000,0x60000) {BaselineListing=baseline},result);
            if(partial)
            {
                var marker=typeof(ShopDispatchSample).GetProperty("SelectedOwnerId")??throw new InvalidOperationException("Selected sample marker is missing.");
                marker.SetValue(sample,(ulong?)24);
            }
            return sample;
        }
    }
}
