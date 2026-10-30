using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Synthetic capacity, serialization and durable-admission regressions; no source is started or read.</summary>
internal static class ActorCapacityContractVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Keys = ["selfId", "position", "nearbyPlayers", "playerShopStates"];
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
        void Check(string name, Action body)
        {
            try { body(); checks.Add(new("actor_capacity_" + name, true, "Synthetic complete collections/MemoryStream/temp state only; LC reads=0; native requests=0; no game input.")); }
            catch (Exception ex) { checks.Add(new("actor_capacity_" + name, false, ex.ToString())); }
        }
        Check("bounded_count_and_unrelated_limits_unchanged", () =>
        {
            Need(ActorCollectionContract.MaximumActors == 1024, "Shared actor ceiling must remain original raw 1024.");
            var t = ReaderType("ActorCollectionLimits");
            foreach (string name in new[] { "MaximumSlots", "MaximumObjects", "MaximumSelectedActors" })
                Need((int)t.GetField(name, Hidden)!.GetRawConstantValue()! == 1024, name + " differs from common ceiling.");
            Need(HubProtocol.MaxMessageBytes == 1024 * 1024 && HubProtocol.FreshMilliseconds == 2000, "Byte/freshness limit changed with actor count.");
        });
        foreach (int count in new[] { 129, 1024 })
        {
            Check("raw_and_typed_accept_" + count, () =>
            {
                Invoke(ReaderType("ActorCollectionLimits").GetMethod("RequireObjectCount", Hidden)!, null, count);
                Invoke(ReaderType("ActorCollectionLimits").GetMethod("RequireSelectedCount", Hidden)!, null, count);
                var header = new byte[24]; ulong begin = 0x100000;
                BinaryPrimitives.WriteUInt64LittleEndian(header, begin);
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), begin + (ulong)count * 16);
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), begin + 4096UL * 16);
                var vector = Invoke(ReaderType("ActorVectorLayout").GetMethod("Inspect", Hidden)!, null, 0x200000UL, header, 1024)!;
                Need((int)vector.GetType().GetProperty("ActiveBytes")!.GetValue(vector)! == count * 16, "Reserved capacity was read or active rows truncated.");
            });
            Check("consumer_roundtrip_preserves_all_" + count, () =>
            {
                var f = new Fixture(count); var context = f.Context();
                Need(context.Players.Length == count && context.Players[^1].Id == f.Owners[^1].Id, "Runtime context dropped tail actors.");
                context.RequireFreshForDispatch(f.Now);
                var targets = f.Targets with { Owners = context.Players };
                ShopTargetPolicy.ValidateStored(targets, f.Source);
                var response = f.Response(targets);
                var roundtrip = Roundtrip(response);
                Need(roundtrip.TargetSnapshot!.Owners.Select(x => x.Id).SequenceEqual(f.Owners.Select(x => x.Id)), "Wire lost or reordered owners.");
                var manual = new ManualNearbyContext(targets.SnapshotId, f.Source, f.Now, targets.SelfPosition, targets.Owners, true);
                Need(manual.Count == count, "Manual context truncated owners.");
                Need(ManualNearbyContext.TryAdoptRefresh(null, f.Source, f.Source, response.RequestId, roundtrip, out var adopted, out _)
                    && adopted!.Count == count && adopted.CanDispatch, "Explicit matching refresh cannot adopt complete count.");
            });
        }
        Check("1025_rejected_at_all_count_boundaries", () =>
        {
            ProbeCode(Reject(() => Invoke(ReaderType("ActorCollectionLimits").GetMethod("RequireObjectCount", Hidden)!, null, 1025)), "ACTOR_COLLECTION_LIMIT");
            ProbeCode(Reject(() => Invoke(ReaderType("ActorCollectionLimits").GetMethod("RequireSelectedCount", Hidden)!, null, 1025)), "ACTOR_TYPED_COLLECTION_LIMIT");
            var header = new byte[24]; BinaryPrimitives.WriteUInt64LittleEndian(header, 0x100000);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 0x100000 + 1025UL * 16);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), 0x100000 + 2048UL * 16);
            ProbeCode(Reject(() => Invoke(ReaderType("ActorVectorLayout").GetMethod("Inspect", Hidden)!, null, 0x200000UL, header, 1024)), "ACTOR_VECTOR_LIMIT");
            var f = new Fixture(1025);
            Reject(() => f.Context());
            Reject(() => ShopTargetPolicy.ValidateStored(f.Targets, f.Source));
            Reject(() => new ManualNearbyContext("overflow", f.Source, f.Now, new(100,100), f.Owners));
        });
        Check("duplicate_id_still_rejected_at_129", () =>
        {
            var f = new Fixture(129); var owners = f.Owners.ToArray(); owners[^1] = owners[0];
            Reject(() => f.Context(owners));
            Reject(() => ShopTargetPolicy.ValidateStored(f.Targets with { Owners = owners }, f.Source));
            Reject(() => new ManualNearbyContext("duplicate", f.Source, f.Now, new(100,100), owners));
        });
        Check("duplicate_action_evidence_remains_unknown", () =>
        {
            var f = new Fixture(129); var result = f.Result();
            var rows = result.Fields["playerShopStates"].Value!.Value.GetProperty("items").EnumerateArray().Select(x => x.Clone()).ToArray();
            result.Fields["playerShopStates"] = FieldReading.Observed(new { Complete = true, Scope = "LocalActorCollections", Items = rows.Append(rows[0]).ToArray() }, f.Now);
            var context = f.Context(result: result);
            Need(context.Players.Length == 129 && context.Players[0].ShopState == ShopEligibilityPolicy.Unknown && !ShopEligibilityPolicy.CanScan(context.Players[0]),
                "Duplicate action evidence was promoted to an eligible shop.");
            PolicyCode(Reject(() => ShopDispatchPolicy.RequireOwner(context, f.Targets, f.Owners[0], f.Now, false)), "TargetShopStateUnknown");
        });
        foreach (string key in Keys)
            Check("stale_field_rejects_" + key, () =>
            {
                var f = new Fixture(129); var result = f.Result();
                result.Fields[key] = result.Fields[key] with { SampledAt = f.Now.AddMilliseconds(-HubProtocol.FreshMilliseconds - 1) };
                Reject(() => f.Context(result: result));
            });
        Check("dispatch_freshness_and_same_capture_still_required", () =>
        {
            var f = new Fixture(129); var context = f.Context();
            context.RequireFreshForDispatch(f.Now.AddMilliseconds(HubProtocol.FreshMilliseconds));
            Reject(() => context.RequireFreshForDispatch(f.Now.AddMilliseconds(HubProtocol.FreshMilliseconds + 1)));
            Reject(() => f.Context(now: f.Now.AddTicks(-1)));
            var result = f.Result(); result.Fields["playerShopStates"] = result.Fields["playerShopStates"] with { SampledAt = f.Now.AddTicks(-1) };
            Reject(() => f.Context(result: result));
        });
        Check("history_and_poll_cannot_authorize_1024", () =>
        {
            var f = new Fixture(1024); var response = f.Response(f.Targets);
            var restored = ManualNearbyContext.RestoreSaved(null, f.Source, response)!;
            Need(restored.Count == 1024 && !restored.CanDispatch, "Historical restore authorized dispatch or lost rows.");
            restored.ObserveBroker(response);
            Need(!restored.CanDispatch, "GetState true flag authorized dispatch.");
            Need(!ManualNearbyContext.TryAdoptRefresh(restored, f.Source, f.Source, response.RequestId, response, out _, out _), "Same old snapshot reauthorized refresh.");
            PolicyCode(Reject(() => ShopTargetPolicy.Validate(f.Targets with { CanDispatch = false }, f.Source, f.Source,
                f.Source.SessionKey, f.Targets.SnapshotId)), "TargetRefreshRequired");
        });
        Check("response_budget_exact_and_one_byte_over", () =>
        {
            var response = new ShopCommandResponse { Detail = "" };
            int padding = HubProtocol.MaxMessageBytes - ShopTargetSnapshotBudget.ReservedBytes - Bytes(response with { RequestDurationMs = double.MaxValue });
            Need(padding > 0, "Base compact fixture unexpectedly exceeds response budget.");
            var exact = response with { Detail = new string('a', padding) };
            Need(Bytes(exact with { RequestDurationMs = double.MaxValue }) + ShopTargetSnapshotBudget.ReservedBytes == HubProtocol.MaxMessageBytes, "Boundary fixture is not exact.");
            Need(ShopResponseBudget.Fit(exact, ShopTargetSnapshotBudget.ReservedBytes).Detail == exact.Detail, "Exact complete response was mutated.");
            PolicyCode(Reject(() => ShopResponseBudget.Fit(exact with { Detail = exact.Detail + "a" }, ShopTargetSnapshotBudget.ReservedBytes)), "TargetSnapshotTooLarge");
            Need(ShopResponseBudget.Fit(exact with { Detail = exact.Detail + "a" }).Detail.Length == exact.Detail.Length + 1,
                "Runtime metadata reserve did not retain the gap after conservative admission.");
        });
        Check("saved_envelope_exact_and_one_byte_over", () =>
        {
            var f = new Fixture(1); int padding = HubProtocol.MaxMessageBytes - ShopTargetSnapshotBudget.ReservedBytes - SavedBytes(f.Source, f.Targets);
            var exact = f.Targets with { Detail = new string('a', padding) };
            Need(SavedBytes(f.Source, exact) + ShopTargetSnapshotBudget.ReservedBytes == HubProtocol.MaxMessageBytes, "Saved envelope boundary is not exact.");
            ShopTargetSnapshotBudget.RequireSavedFits(f.Source, exact);
            PolicyCode(Reject(() => ShopTargetSnapshotBudget.RequireSavedFits(f.Source, exact with { Detail = exact.Detail + "a" })), "TargetSnapshotTooLarge");
        });
        Check("utf8_json_and_complete_response_are_counted", () =>
        {
            var f = new Fixture(1); ShopTargetSnapshotBudget.RequireSavedFits(f.Source, f.Targets);
            var ascii = f.Response(f.Targets) with { Detail = new string('a', 180000) };
            ShopTargetSnapshotBudget.RequireFits(f.Source, f.Targets, ascii);
            var escaped = ascii with { Detail = new string('\u5e97', 180000) };
            Need(Bytes(escaped) > HubProtocol.MaxMessageBytes && escaped.Detail.Length < HubProtocol.MaxMessageBytes, "Fixture does not expose JSON byte/character distinction.");
            PolicyCode(Reject(() => ShopTargetSnapshotBudget.RequireFits(f.Source, f.Targets, escaped)), "TargetSnapshotTooLarge");
            var listings = ascii with { Detail = "", Listings = [new MarketListing { Detail = new string('x', HubProtocol.MaxMessageBytes), Owner = f.Owners[0] }] };
            var fitted = ShopResponseBudget.Fit(listings);
            Need(fitted.Listings.Length == 0 && fitted.ListingsTruncated && fitted.TargetSnapshot == f.Targets && listings.Listings.Length == 1,
                "Response could not omit listing cache safely or modified owner snapshot/source data.");
        });
        Check("growing_progress_compacts_only_public_details", () =>
        {
            var f = new Fixture(129); var progress = Progress(f, new string('\u539f', 16000));
            var source = f.Response(f.Targets) with { SavedScan = progress, Listings = [new MarketListing { Detail = new string('x', 100000) }] };
            Need(Bytes(source) > HubProtocol.MaxMessageBytes, "Progress growth fixture must exceed transport ceiling.");
            var fitted = ShopResponseBudget.Fit(source);
            Need(Bytes(fitted with { RequestDurationMs = double.MaxValue }) + ShopTargetSnapshotBudget.ReservedBytes <= HubProtocol.MaxMessageBytes,
                "Compacted complete response still exceeds bounded wire budget.");
            Need(fitted.TargetSnapshot == source.TargetSnapshot && fitted.Listings.Length == 0 && fitted.ListingsTruncated,
                "Formatter removed owner snapshot or failed to declare omitted listing cache.");
            Need(fitted.SavedScan!.Owners.Length == progress.Owners.Length && (fitted.SavedScan with { Owners = progress.Owners }) == progress,
                "Formatter changed progress counts, authorization, identity or status.");
            for (int i = 0; i < progress.Owners.Length; i++)
            {
                var before = progress.Owners[i]; var after = fitted.SavedScan.Owners[i];
                Need((after with { BuyDetail = before.BuyDetail, SellDetail = before.SellDetail }) == before,
                    "Formatter changed owner name/ID/page state/timestamp.");
                Need(after.BuyDetail.Length < before.BuyDetail.Length && after.BuyDetail.Contains("完整原因", StringComparison.Ordinal),
                    "Public detail was silently shortened without retained-evidence notice.");
            }
            Need(progress.Owners.All(x => x.BuyDetail.Length == 16000 && x.SellDetail.Length == 16000), "Formatter mutated original historical details.");
        });
        Check("owner_identity_and_name_cannot_be_compressed_to_fit", () =>
        {
            var f = new Fixture(1024); var owners = f.Owners.Select(x => x with { Name = new string('\u5e97', 1024) }).ToArray();
            var targets = f.Targets with { Owners = owners }; var response = f.Response(targets) with { SavedScan = Progress(f, "") };
            ShopTargetPolicy.ValidateStored(targets, f.Source);
            PolicyCode(Reject(() => ShopResponseBudget.Fit(response)), "TargetSnapshotTooLarge");
            Need(targets.Owners.Length == 1024 && targets.Owners.All(x => x.Name.Length == 1024), "Rejected response truncated owner names/count.");
        });
        Check("future_complete_progress_admitted_before_snapshot_commit", () =>
        {
            var f = new Fixture(500); var targets = f.Targets with { Owners = f.Owners.Select(x => x with { Name = new string('n', 900) }).ToArray() };
            var response = f.Response(targets); ShopTargetPolicy.ValidateStored(targets, f.Source);
            ShopTargetSnapshotBudget.RequireSavedFits(f.Source, targets);
            ShopResponseBudget.Fit(response, ShopTargetSnapshotBudget.ReservedBytes);
            PolicyCode(Reject(() => ShopTargetSnapshotBudget.RequireFits(f.Source, targets, response)), "TargetSnapshotTooLarge");
            Need(targets.Owners.Length == 500 && targets.Owners[^1].Name.Length == 900, "Future-progress rejection truncated names or owners.");
        });
        Check("shop_wire_exact_1mib_and_overflow_before_header", () =>
        {
            var empty = new ShopCommandResponse { Detail = "" }; var exact = empty with { Detail = new string('a', HubProtocol.MaxMessageBytes - Bytes(empty)) };
            Need(Bytes(exact) == HubProtocol.MaxMessageBytes, "Shop response is not exact 1 MiB.");
            using var stream = new MemoryStream(); ShopCommandWire.WriteAsync(stream, exact, default).GetAwaiter().GetResult();
            Need(stream.Length == HubProtocol.MaxMessageBytes + 4L, "Length-prefixed frame boundary changed."); stream.Position = 0;
            Need(ShopCommandWire.ReadAsync<ShopCommandResponse>(stream, default).GetAwaiter().GetResult().Detail.Length == exact.Detail.Length, "Exact-size response failed receive.");
            using var rejected = new MemoryStream(); Reject(() => ShopCommandWire.WriteAsync(rejected, exact with { Detail = exact.Detail + "a" }, default).GetAwaiter().GetResult());
            Need(rejected.Length == 0, "Oversized Shop response wrote a partial header/body.");
        });
        Check("hub_wire_exact_1mib_and_overflow_before_header", () =>
        {
            var empty = new HubSnapshot { Detail = "" }; var exact = empty with { Detail = new string('a', HubProtocol.MaxMessageBytes - Bytes(empty)) };
            using var stream = new MemoryStream(); HubWire.WriteAsync(stream, exact, default).GetAwaiter().GetResult(); stream.Position = 0;
            Need(HubWire.ReadAsync(stream, default).GetAwaiter().GetResult().Detail.Length == exact.Detail.Length, "Exact-size hub frame failed receive.");
            using var rejected = new MemoryStream(); Reject(() => HubWire.WriteAsync(rejected, exact with { Detail = exact.Detail + "a" }, default).GetAwaiter().GetResult());
            Need(rejected.Length == 0, "Oversized Hub response wrote a partial frame.");
        });
        Check("wire_length_guards_and_field_count_remain_independent", () =>
        {
            foreach (int length in new[] { -1, 0, HubProtocol.MaxMessageBytes + 1 })
            {
                var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
                using var shop = new MemoryStream(header); Need(Reject(() => ShopCommandWire.ReadAsync<ShopCommandResponse>(shop, default).GetAwaiter().GetResult()) is InvalidDataException, "Shop frame was not rejected before body read.");
                using var hub = new MemoryStream(header); Need(Reject(() => HubWire.ReadAsync(hub, default).GetAwaiter().GetResult()) is InvalidDataException, "Hub frame was not rejected before body read.");
            }
            using var fields = new MemoryStream(); HubWire.WriteAsync(fields, new HubSnapshot { Fields = Enumerable.Range(0, 129).ToDictionary(i => "field" + i, _ => FieldReading.Unknown("synthetic")) }, default).GetAwaiter().GetResult(); fields.Position = 0;
            Need(Reject(() => HubWire.ReadAsync(fields, default).GetAwaiter().GetResult()) is InvalidDataException, "Unrelated 128 top-level field ceiling was widened.");
        });
        Check("compiled_refresh_no_take_and_budget_before_prepare_commit", VerifyCompiledRefresh);
        Check("actual_broker_failed_refresh_revokes_and_oversize_prepare_writes_nothing", () => BrokerAdmission().GetAwaiter().GetResult());
        Check("scan_plan_byte_guard_before_write_preserves_old_plan", VerifyPlanAdmission);
        return checks.ToArray();
    }

    private static async Task BrokerAdmission()
    {
        var f = new Fixture(129); string root = NewStateDirectory("broker"); var fake = new NoReadSource();
        await using var runtime = new HubRuntime(fake, Path.Combine(root, "runtime"), enableTransport: false); runtime.SetLogging(false);
        using var reader = new CurrentLcSource(profileDirectory: Path.Combine(root, "absent-profile"));
        string shops = Path.Combine(root, "shops");
        await using var broker = new ShopBroker(runtime, reader, shops);
        Set(broker, "targetSource", f.Source); Set(broker, "targetSnapshot", f.Targets);
        var rejected = await broker.HandleAsync(new ShopCommandRequest { RequestId = "offline-capacity-refresh", Command = ShopCommands.RefreshTargets, ExpectedSourceSessionKey = f.Source.SessionKey });
        var retained = (ShopTargetSnapshot)Get(broker, "targetSnapshot");
        Need(rejected.State != "TargetsRefreshed" && !retained.CanDispatch && retained.SnapshotId == f.Targets.SnapshotId && retained.RefreshedAt == f.Now
            && retained.Owners.Select(x => x.Id).SequenceEqual(f.Owners.Select(x => x.Id)), "Failed actual refresh retained authorization or altered old observations.");
        string saved = Path.Combine(shops, "targets.json"); string before = Hash(saved);
        var huge = new Fixture(1024); var owners = huge.Owners.Select(x => x with { Name = new string('\u5e97', 1024) }).ToArray();
        var oversized = huge.Targets with { Owners = owners }; ShopTargetPolicy.ValidateStored(oversized, huge.Source);
        var error = Reject(() => Invoke(typeof(ShopBroker).GetMethod("PrepareTargets", Hidden)!, broker, huge.Source, oversized)); PolicyCode(error, "TargetSnapshotTooLarge");
        Need(Hash(saved) == before && Directory.GetFiles(shops, ".targets-*.tmp").Length == 0 && !((ShopTargetSnapshot)Get(broker, "targetSnapshot")).CanDispatch,
            "Oversized prepare wrote/committed a candidate or reauthorized old targets.");
        Need(fake.Reads == 0 && !runtime.IsRunning && !rejected.NativeEntered && !rejected.NativeReturned, "Offline failure test performed source/native work.");
    }

    private static void VerifyPlanAdmission()
    {
        var f = new Fixture(129); string root = NewStateDirectory("plans"); var store = new ShopScanProgressStore(root);
        var old = store.StartNew(f.Source, f.Targets); var before = Directory.GetFiles(root, "*.plan.json").ToDictionary(p => p, Hash);
        Need(old.Owners.Length == 129, "Plan silently truncated confirmed owners.");
        var many = new Fixture(1024); var huge = many.Targets with { Owners = many.Owners.Select(x => x with { Name = new string('\u5e97', 1024) }).ToArray() };
        Reject(() => store.StartNew(many.Source, huge));
        Need(store.PlanId == old.PlanId && Directory.GetFiles(root, "*.plan.json").Length == before.Count && before.All(p => Hash(p.Key) == p.Value)
            && Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "Oversized plan mutated prior plan or left durable/temp output.");
        PolicyCode(Reject(() => store.StartNew(f.Source, f.Targets, responseEnvelope: f.Response(f.Targets) with { Detail = new string('x', HubProtocol.MaxMessageBytes) })), "TargetSnapshotTooLarge");
        Need(store.PlanId == old.PlanId && Directory.GetFiles(root, "*.plan.json").Length == before.Count && before.All(p => Hash(p.Key) == p.Value),
            "Complete response precheck failed after plan commit.");
        var reopened = new ShopScanProgressStore(root); Need(reopened.Error is null && reopened.PlanId == old.PlanId, "Rejected plan broke historical replay.");
        Reject(() => reopened.StartNew(f.Source, f.Targets with { CanDispatch = false }));
        Need(reopened.PlanId == old.PlanId, "Restored history authorized a new plan.");
    }

    private static void VerifyCompiledRefresh()
    {
        var refresh = typeof(ShopBroker).GetMethod("RefreshTargetsAsync", Hidden)!;
        var stateMachine = refresh.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var calls = CalledMethods(stateMachine.GetMethod("MoveNext", Hidden)!).ToArray();
        int budget = Array.FindIndex(calls, m => m.DeclaringType == typeof(ShopTargetSnapshotBudget) && m.Name == "RequireFits");
        int prepare = Array.FindIndex(calls, m => m.DeclaringType == typeof(ShopBroker) && m.Name == "PrepareTargets");
        int commit = Array.FindIndex(calls, m => m.DeclaringType == typeof(File) && m.Name == "Move");
        Need(budget >= 0 && prepare > budget && commit > prepare, "Real compiled refresh must gate complete envelopes before persistence/commit.");
        Need(!calls.Any(m => m.DeclaringType == typeof(Enumerable) && m.Name == "Take"), "Refresh still silently truncates owners with Take.");
    }
    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        for (int at = 0; at < bytes.Length;)
        {
            ushort key = bytes[at++]; if (key == 0xfe) key = (ushort)(0xfe00 | bytes[at++]); var op = codes[key];
            int size = op.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(bytes, at)), _ => 4 };
            Need(at + size <= bytes.Length, "Malformed IL.");
            if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt))
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, at), method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments())!;
            at += size;
        }
    }

    private sealed class Fixture
    {
        internal readonly SourceIdentity Source = new(4242, 987654321, "offline-LC", "0x100000", 1, 4096, new string('A',64), "offline-capacity");
        internal readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal readonly MarketOwner[] Owners;
        internal Fixture(int count) => Owners = Enumerable.Range(0, count).Select(i => new MarketOwner { Id=(ulong)(100+i), LocalIdentity="owner-"+i,
            Name="S", Position=new(100+i,101), ObservedAt=Now, VisibilityEvidence="LocalActorCollection_VisibilityUnknown", ActionType=".?AVShopAction@lineage@@",
            ShopState=ShopEligibilityPolicy.ShopActionObserved, ShopStateSampledAt=Now, ShopStateEvidence=ShopEligibilityPolicy.ActionEvidence, ShopStateDetail="" }).ToArray();
        internal ShopTargetSnapshot Targets => new() { SnapshotId="offline-capacity-targets", SourceSessionKey=Source.SessionKey, RefreshedAt=Now,
            SelfId=11, SelfPosition=new(100,100), Owners=Owners, CanDispatch=true, DispatchBlockReason="", Detail="" };
        internal ShopCommandResponse Response(ShopTargetSnapshot target) => new() { RequestId="offline-capacity-refresh", State="TargetsRefreshed", Detail="", At=Now,
            CurrentSourceSessionKey=Source.SessionKey, TargetSnapshot=target };
        internal ReadResult Result(MarketOwner[]? input=null)
        {
            var owners=input??Owners;
            var players=owners.Select(p=>new { p.Id,p.LocalIdentity,Kind="Player",p.Name,NameState="Observed",p.Position,PositionState="Observed",SampledAt=Now }).ToArray();
            var states=owners.Select(p=>new {p.Id,p.LocalIdentity,p.ActionType,p.ShopState,p.ShopStateSampledAt,p.ShopStateEvidence,p.ShopStateDetail}).ToArray();
            return new(Source,new Dictionary<string,FieldReading> { ["selfId"]=FieldReading.Observed(11UL,Now), ["position"]=FieldReading.Observed(new {X=100,Y=100},Now),
                ["nearbyPlayers"]=FieldReading.Observed(new { Complete=true,Scope="LocalActorCollections",Items=players },Now),
                ["playerShopStates"]=FieldReading.Observed(new { Complete=true,Scope="LocalActorCollections",Items=states },Now) },"Reading","synthetic complete collection");
        }
        internal ShopRuntimeContext Context(MarketOwner[]? owners=null,ReadResult? result=null,DateTimeOffset? now=null) =>
            ShopRuntimeContext.FromFreshResult(result??Result(owners),Source,new ReadSelection(Keys),Source.SessionKey,now??Now,requireShopStates:true);
    }
    private sealed class NoReadSource : IMemorySource
    { internal int Reads; public ReadResult Read(CancellationToken token) { Reads++; throw new InvalidOperationException("Capacity test must not read LC."); } public void Dispose() {} }
    private static int Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value,HubProtocol.Json).Length;
    private static int SavedBytes(SourceIdentity source, ShopTargetSnapshot snapshot) => Bytes(new { Source=source,Snapshot=snapshot });
    private static ShopSavedScanProgress Progress(Fixture f, string detail) => new() { PlanId = "offline-plan", SourceSessionKey = f.Source.SessionKey,
        CreatedAt = f.Now, UpdatedAt = f.Now, TotalPages = f.Owners.Length * 2, RemainingPages = f.Owners.Length, SearchedPages = f.Owners.Length,
        AvailablePages = 0, UncertainPages = f.Owners.Length, CanContinue = false, ContinueBlockCode = "UncertainPages", ContinueBlockReason = "synthetic blocked",
        Owners = f.Owners.Select(x => new ShopOwnerSearchProgress { OwnerId = x.Id, OwnerName = x.Name, BuyState = "Uncertain", SellState = "NotSent",
            BuyDetail = detail, SellDetail = detail, UpdatedAt = f.Now }).ToArray() };
    private static ShopCommandResponse Roundtrip(ShopCommandResponse response)
    { using var stream=new MemoryStream();ShopCommandWire.WriteAsync(stream,response,default).GetAwaiter().GetResult();stream.Position=0;return ShopCommandWire.ReadAsync<ShopCommandResponse>(stream,default).GetAwaiter().GetResult(); }
    private static Type ReaderType(string name) => typeof(CurrentLcSource).Assembly.GetType("MemoryHub.Reader."+name,true)!;
    private static object? Invoke(MethodInfo method, object? instance, params object?[] values)
    { try{return method.Invoke(instance,values);}catch(TargetInvocationException ex)when(ex.InnerException is not null){ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;} }
    private static Exception Reject(Action action)
    { try{action();}catch(Exception ex)when(ex is not NullReferenceException and not MissingMemberException){return ex;}throw new InvalidOperationException("Expected rejection did not occur."); }
    private static void ProbeCode(Exception ex,string code) => Need(ex.GetType().FullName=="MemoryHub.Reader.ProbeFailure" && (string?)ex.GetType().GetProperty("Code",Hidden)?.GetValue(ex)==code,"Wrong probe failure: "+ex);
    private static void PolicyCode(Exception ex,string code) => Need(ex is ShopRequestFailure f && f.Failure.Code==code && f.Failure.Phase=="BeforePublish","Wrong admission failure: "+ex);
    private static void Need(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static object Get(object value,string field)=>value.GetType().GetField(field,Hidden)!.GetValue(value)!;
    private static void Set(object value,string field,object data)=>value.GetType().GetField(field,Hidden)!.SetValue(value,data);
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    private static string NewStateDirectory(string label)
    {string path=Path.Combine(Path.GetTempPath(),"LC-Market76-actor-capacity-"+label+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
}
