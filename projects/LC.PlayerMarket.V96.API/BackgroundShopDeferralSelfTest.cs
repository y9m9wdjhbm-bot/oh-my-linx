using System.Collections;
using System.Reflection;
using MemoryHub;

namespace LcPlayerMarket;

internal static class BackgroundShopDeferralSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try
            {
                await body();
                checks.Add(new("background_shop_deferral_" + name, true,
                    "Fake Core policy/schedule/source only; no scheduler start, LC read, native call, settings write, or game input."));
            }
            catch (Exception error) { checks.Add(new("background_shop_deferral_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

        await Check("unleased_schedule_and_admission_are_unchanged", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing", "selfId", "position", "nearbyPlayers");
            Need(!policy.Deferred && ReferenceEquals(policy.Eligible(schedule), schedule)
                && policy.Admit(Slice(schedule, "personalShop")), "Unleased policy changed scheduling or admission.");
            return Task.CompletedTask;
        });
        await Check("one_lease_blocks_only_background_personal_shop", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing", "selfId", "position", "nearbyPlayers");
            int wakes = 0; using var lease = policy.Acquire(() => wakes++);
            Need(policy.Deferred && !policy.Admit(Slice(schedule, "personalShop"))
                && policy.Admit(Slice(schedule, "priority")) && policy.Admit(Slice(schedule, "actors"))
                && Names(policy.Eligible(schedule)).SequenceEqual(new[] { "priority", "actors" }) && wakes == 0,
                "Deferral blocked other consumers or woke/resumed before release.");
            return Task.CompletedTask;
        });
        await Check("nested_leases_resume_only_after_last_release", () =>
        {
            var policy = new Policy(); int wakes = 0;
            var first = policy.Acquire(() => wakes++); var second = policy.Acquire(() => wakes++);
            first.Dispose(); Need(policy.Deferred && wakes == 0, "First nested release resumed scheduling.");
            second.Dispose(); Need(!policy.Deferred && wakes == 1, "Final nested release did not resume exactly once.");
            return Task.CompletedTask;
        });
        await Check("reverse_release_order_preserves_refcount", () =>
        {
            var policy = new Policy(); int wakes = 0;
            var first = policy.Acquire(() => wakes++); var second = policy.Acquire(() => wakes++);
            second.Dispose(); Need(policy.Deferred && wakes == 0, "Reverse inner release resumed scheduling.");
            first.Dispose(); Need(!policy.Deferred && wakes == 1, "Reverse final release lost its wake.");
            return Task.CompletedTask;
        });
        await Check("lease_dispose_is_idempotent", () =>
        {
            var policy = new Policy(); int wakes = 0; var lease = policy.Acquire(() => wakes++);
            lease.Dispose(); lease.Dispose(); lease.Dispose();
            Need(!policy.Deferred && wakes == 1, "Repeated Dispose underflowed or woke more than once.");
            return Task.CompletedTask;
        });
        await Check("concurrent_same_lease_dispose_releases_once", async () =>
        {
            var policy = new Policy(); int wakes = 0; var lease = policy.Acquire(() => Interlocked.Increment(ref wakes));
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(lease.Dispose)));
            Need(!policy.Deferred && wakes == 1, "Concurrent Dispose released the same holder more than once.");
        });
        await Check("concurrent_independent_leases_have_one_final_wake", async () =>
        {
            var policy = new Policy(); int wakes = 0;
            IDisposable[] leases = Enumerable.Range(0, 16).Select(_ => policy.Acquire(() => Interlocked.Increment(ref wakes))).ToArray();
            await Task.WhenAll(leases.Select(lease => Task.Run(lease.Dispose)));
            Need(!policy.Deferred && wakes == 1, "Independent releases lost a reference or generated multiple final wakes.");
        });
        await Check("selected_before_lease_is_rechecked_at_admission", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing");
            object selected = Pick(policy.Eligible(schedule), 0)!;
            using var lease = policy.Acquire(() => { });
            Need(Name(selected) == "personalShop" && !policy.Admit(selected), "PickDue-to-admission race admitted a newly deferred slice.");
            return Task.CompletedTask;
        });
        await Check("previously_admitted_slice_may_complete_without_new_admission", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing"); object shop = Slice(schedule, "personalShop");
            Need(policy.Admit(shop), "Initial slice was not admitted.");
            using var lease = policy.Acquire(() => { });
            int completions = 0; completions++; // Complete only the already-owned fake transaction.
            Need(completions == 1 && !policy.Admit(shop), "Deferral cancelled prior ownership or admitted a second transaction.");
            return Task.CompletedTask;
        });
        await Check("held_lease_rejects_concurrent_personal_shop_admissions", async () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing"); object shop = Slice(schedule, "personalShop");
            using var lease = policy.Acquire(() => { });
            bool[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => policy.Admit(shop))));
            Need(results.All(admitted => !admitted), "A concurrent admission bypassed an acquired lease.");
        });
        await Check("priority_self_and_position_still_win_when_shop_is_deferred", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing", "selfId", "position", "nearbyPlayers");
            using var lease = policy.Acquire(() => { });
            object selected = Pick(policy.Eligible(schedule), 0)!;
            Need(Name(selected) == "priority" && policy.Admit(selected), "Deferral interfered with due self/position priority work.");
            return Task.CompletedTask;
        });
        await Check("other_background_work_does_not_starve_behind_deferred_shop", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing", "selfId", "position", "nearbyPlayers");
            Set(Slice(schedule, "priority"), "NextDue", 1000d);
            Set(Slice(schedule, "personalShop"), "NextDue", -100d);
            using var lease = policy.Acquire(() => { });
            Need(Name(Pick(policy.Eligible(schedule), 0)!) == "actors", "An overdue deferred shop slice starved eligible background work.");
            return Task.CompletedTask;
        });
        await Check("shop_only_deferral_has_empty_eligible_schedule", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing");
            using var lease = policy.Acquire(() => { }); object eligible = policy.Eligible(schedule);
            Need(!Names(eligible).Any() && Pick(eligible, 0) is null, "Shop-only deferral kept selecting an inadmissible overdue slice.");
            return Task.CompletedTask;
        });
        await Check("release_restores_original_due_and_retry_state", () =>
        {
            var policy = new Policy(); object schedule = Schedule("personalShopListing"); object shop = Slice(schedule, "personalShop");
            Set(shop, "NextDue", 123d); Set(shop, "LastStarted", 20d); Set(shop, "LastCompleted", 30d); Set(shop, "RetryAt", 55d);
            using (policy.Acquire(() => { })) Need(!Names(policy.Eligible(schedule)).Any(), "Deferred shop remained eligible.");
            Need(ReferenceEquals(policy.Eligible(schedule), schedule) && ReferenceEquals(Pick(policy.Eligible(schedule), 200), shop)
                && (double)Get(shop, "NextDue")! == 123d && (double?)Get(shop, "LastStarted") == 20d
                && (double?)Get(shop, "LastCompleted") == 30d && (double?)Get(shop, "RetryAt") == 55d,
                "Deferral rewrote deadlines, retry state, or the original schedule.");
            return Task.CompletedTask;
        });
        await Check("wake_callback_is_outside_refcount_lock_and_can_reenter", () =>
        {
            var policy = new Policy(); IDisposable? nested = null; int wakes = 0;
            var lease = policy.Acquire(() =>
            {
                Need(!policy.GateHeldByCaller, "Final wake ran while the refcount lock was held.");
                wakes++; nested = policy.Acquire(() => wakes++);
            });
            lease.Dispose(); Need(policy.Deferred && wakes == 1, "Final wake did not permit a new independent lease.");
            nested!.Dispose(); Need(!policy.Deferred && wakes == 2, "Reentered lease lost its final wake.");
            return Task.CompletedTask;
        });
        await Check("null_resume_callback_is_rejected_without_a_holder", () =>
        {
            var policy = new Policy(); bool refused = false;
            try { policy.Acquire(null!); } catch (TargetInvocationException error) when (error.InnerException is ArgumentNullException) { refused = true; }
            Need(refused && !policy.Deferred, "Invalid lease left a deferral holder behind.");
            return Task.CompletedTask;
        });
        await Check("runtime_api_does_not_reconfigure_or_change_intervals", async () =>
        {
            var source = new FakeSource(); await using var runtime = CreateRuntime(source);
            int configurations = source.Configurations; int interval = runtime.IntervalMilliseconds;
            var backgrounds = runtime.BackgroundIntervalMilliseconds.ToArray();
            using (runtime.DeferBackgroundPersonalShop())
            using (runtime.DeferBackgroundPersonalShop())
                Need(source.Configurations == configurations && source.Reads == 0 && !runtime.IsRunning,
                    "Deferral reconfigured the source, started sampling, or performed a source read.");
            Need(runtime.IntervalMilliseconds == interval && runtime.BackgroundIntervalMilliseconds.SequenceEqual(backgrounds)
                && source.Configurations == configurations, "Deferral changed saved scheduling configuration.");
        });
        await Check("runtime_lease_never_retimes_or_clears_existing_fields", async () =>
        {
            var source = new FakeSource(); await using var runtime = CreateRuntime(source);
            DateTimeOffset sampled = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
            var observed = FieldReading.Observed(new { owner = 17 }, sampled, "OfflineFake");
            var snapshot = new HubSnapshot { Sequence = 7, State = "Reading", PublishedAt = sampled,
                Fields = new() { ["personalShopListing"] = observed, ["selfId"] = FieldReading.Observed(1, sampled) } };
            typeof(HubRuntime).GetField("latest", Instance)!.SetValue(runtime, snapshot);
            using (runtime.DeferBackgroundPersonalShop())
            {
                var held = runtime.Snapshot;
                Need(held.Sequence == 7 && held.PublishedAt == sampled && ReferenceEquals(held.Fields["personalShopListing"], observed),
                    "Acquiring a lease republished, cleared, or retimed existing fields.");
                var expired = held.AtTime(sampled.AddMilliseconds(HubProtocol.FreshMilliseconds + 1));
                Need(expired.Fields["personalShopListing"].State == "Stale" && expired.Fields["personalShopListing"].Value is null
                    && expired.Fields["personalShopListing"].SampledAt == sampled && observed.State == "Observed",
                    "Deferral extended freshness or changed the historical observation.");
            }
            Need(runtime.Snapshot.Fields["personalShopListing"].SampledAt == sampled && runtime.Snapshot.Sequence == 7,
                "Releasing a lease fabricated a new observation.");
        });
        await Check("explicit_fake_source_read_is_not_intercepted_by_runtime_lease", async () =>
        {
            var source = new FakeSource(); await using var runtime = CreateRuntime(source);
            using var lease = runtime.DeferBackgroundPersonalShop();
            var result = source.ReadSlice(new ReadSelection(new[] { "personalShopListing" }), CancellationToken.None);
            Need(source.Reads == 1 && result.Fields.ContainsKey("personalShopListing"), "A direct source read was suppressed by a scheduler-only lease.");
        });
        await Check("cancellation_finally_releases_last_runtime_lease", async () =>
        {
            var source = new FakeSource(); await using var runtime = CreateRuntime(source);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { using var lease = runtime.DeferBackgroundPersonalShop(); cancellation.Token.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { }
            object policy = typeof(HubRuntime).GetField("backgroundPersonalShopDeferral", Instance)!.GetValue(runtime)!;
            Need(!(bool)policy.GetType().GetProperty("IsDeferred", Instance)!.GetValue(policy)!, "Cancelled using scope retained its last holder.");
        });
        await Check("runtime_dispose_then_lease_dispose_is_idempotent", async () =>
        {
            var source = new FakeSource(); var runtime = CreateRuntime(source);
            IDisposable lease = runtime.DeferBackgroundPersonalShop(); await runtime.DisposeAsync();
            lease.Dispose(); lease.Dispose();
            bool refused = false; try { runtime.DeferBackgroundPersonalShop(); } catch (ObjectDisposedException) { refused = true; }
            Need(refused && source.Disposed, "Disposed runtime accepted a new lease or old Dispose lost its safety.");
        });
        return checks.ToArray();
    }

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Core = typeof(HubRuntime).Assembly;
    private static object Schedule(params string[] fields) => Core.GetType("MemoryHub.PriorityReadSchedule", true)!
        .GetMethod("Create", Static)!.Invoke(null, new object[] { new ReadSelection(fields), false })!;
    private static IEnumerable<object> Items(object schedule) => ((IEnumerable)schedule).Cast<object>();
    private static IEnumerable<string> Names(object schedule) => Items(schedule).Select(Name);
    private static string Name(object slice) => (string)Get(slice, "Name")!;
    private static object Slice(object schedule, string name) => Items(schedule).Single(slice => Name(slice) == name);
    private static object? Get(object value, string property) => value.GetType().GetProperty(property, Instance)!.GetValue(value);
    private static void Set(object value, string property, object? next) => value.GetType().GetProperty(property, Instance)!.SetValue(value, next);
    private static object? Pick(object schedule, double now) => Core.GetType("MemoryHub.PriorityReadSchedule", true)!
        .GetMethod("PickDue", Static)!.Invoke(null, new[] { schedule, (object)now, false });
    private static HubRuntime CreateRuntime(FakeSource source) => new(source,
        stateDirectory: @"D:\lineage\LC_PlayerMarket_LOG\maintenance\20261030-background-shop-deferral-offline\never-created-" + Guid.NewGuid().ToString("N"),
        pipeName: "offline-background-shop-deferral", compatibilityPipeNames: Array.Empty<string>(), enableTransport: false);

    private sealed class Policy
    {
        private readonly Type type = Core.GetType("MemoryHub.BackgroundPersonalShopDeferral", true)!;
        private readonly object value;
        internal Policy() => value = Activator.CreateInstance(type, nonPublic: true)!;
        internal bool Deferred => (bool)type.GetProperty("IsDeferred", Instance)!.GetValue(value)!;
        internal bool GateHeldByCaller => Monitor.IsEntered(type.GetField("gate", Instance)!.GetValue(value)!);
        internal IDisposable Acquire(Action wake) => (IDisposable)type.GetMethod("Acquire", Instance)!.Invoke(value, new object[] { wake })!;
        internal object Eligible(object schedule) => type.GetMethod("Eligible", Instance)!.Invoke(value, new[] { schedule })!;
        internal bool Admit(object slice) => (bool)type.GetMethod("TryAdmit", Instance)!.Invoke(value, new[] { slice })!;
    }

    private sealed class FakeSource : ISlicedMemorySource
    {
        internal int Configurations, Reads;
        internal bool Disposed;
        public void Configure(ReadSelection selection) => Configurations++;
        public ReadResult Read(CancellationToken token) => throw new InvalidOperationException("No sampler may start in this offline suite.");
        public ReadResult ReadSlice(ReadSelection fields, CancellationToken token)
        {
            Reads++; token.ThrowIfCancellationRequested();
            return new(null, fields.EnabledKeys.ToDictionary(key => key, key => FieldReading.Unknown("Explicit offline fake")), "Unknown", "Offline fake source");
        }
        public void Dispose() => Disposed = true;
    }
}
