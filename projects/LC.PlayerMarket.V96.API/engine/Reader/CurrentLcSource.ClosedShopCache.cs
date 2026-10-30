using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
namespace MemoryHub.Reader;

public sealed partial class CurrentLcSource
{
    private ClosedShopReadCacheEntry? closedFullShop;
    private void InvalidateClosedShopRead() => Volatile.Write(ref closedFullShop, null);

    private void ObserveClosedShopRead(SourceIdentity source, ShopActionSourceCandidate? candidate,
        Dictionary<string, FieldReading> fields, ReadSelection configuration, ReaderProfile profile,
        ulong? selfId, DateTimeOffset identitySampledAt, long diskLength, DateTime diskModifiedUtc, bool bindingOnly, bool shopRequested)
    {
        if (Volatile.Read(ref closedFullShop) is { } previous && previous.Binding.Source != source) InvalidateClosedShopRead();
        if (!ReferenceEquals(configuration, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile) || selfId is null or 0)
        { InvalidateClosedShopRead(); return; }
        if (!shopRequested || bindingOnly) return;
        if (candidate is null || candidate.BindingOnly || candidate.SourceSessionKey != source.SessionKey
            || !fields.TryGetValue("personalShopListing", out var shop) || shop.Value is not { ValueKind: JsonValueKind.Object } raw
            || !shop.Fresh(DateTimeOffset.UtcNow) || shop.SampledAt != candidate.SampledAt)
        { InvalidateClosedShopRead(); return; }
        var baseline = raw.Deserialize<PersonalShopListing>(HubProtocol.Json);
        if (baseline is null || baseline.LayoutState == "BindingOnly" || baseline.SourceSessionKey != source.SessionKey
            || baseline.SampledAt != candidate.SampledAt || baseline.OwnerIdRaw != candidate.CurrentOwnerIdRaw)
        { InvalidateClosedShopRead(); return; }
        var self = FieldReading.Observed(selfId.Value, identitySampledAt,
            "CharacterSourceFence original identity timestamp; same full-shop pass and final source closure.");
        var binding = new ShopActionSourceBinding(source, candidate.SampledAt, candidate.PlayerShopSubsystem,
            candidate.CurrentOwnerIdRaw, candidate.ServiceRoot, candidate.UISubsystem, candidate.Director, candidate.Scene)
            { BaselineListing = baseline, BindingOnly = false };
        Volatile.Write(ref closedFullShop, new(binding, selfId.Value, identitySampledAt, configuration, profile, self, shop, diskLength, diskModifiedUtc));
    }

    private ClosedShopReuseAttempt TryClosedShopRead(SourceIdentity expected, ReadSelection configured, ReaderProfile profile,
        ulong ownerId, CancellationToken token)
    {
        var entry = Volatile.Read(ref closedFullShop);
        var attempt = ClosedShopReadReusePolicy.Try(entry, expected, configured, profile, ownerId,
            () => DateTimeOffset.UtcNow, item => ProbeClosedShopOwner(item, token));
        if (attempt.Sample is null) InvalidateClosedShopRead();
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(configured, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile))
        { InvalidateClosedShopRead(); throw new InvalidOperationException("SHOP_CONFIGURATION_CHANGED"); }
        return attempt;
    }

    private ClosedShopOwnerProbe ProbeClosedShopOwner(ClosedShopReadCacheEntry entry, CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); var budget = new Budget(clock, token,
            () => ReferenceEquals(entry.Configuration, Volatile.Read(ref selection)) && ReferenceEquals(entry.Profile, activeProfile));
        var expected = entry.Binding.Source;
        // Preserve unique-source admission, without re-reading shop layout,
        // actors or products. This probe never grants new age to the cache.
        var candidates = new List<(int Pid, long Creation, ulong Image, int Size)>();
        foreach (var process in Process.GetProcessesByName("LC"))
        {
            using (process)
            {
                budget.Check();
                try
                {
                    var module = process.MainModule;
                    if (module is not null && string.Equals(module.FileName, Probe.ExePath, StringComparison.OrdinalIgnoreCase))
                        candidates.Add((process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), (ulong)module.BaseAddress, module.ModuleMemorySize));
                }
                catch (InvalidOperationException) { }
                // Access failures are not excluded when deciding uniqueness.
            }
        }
        if (candidates.Count != 1 || candidates[0].Pid != expected.ProcessId || candidates[0].Creation != expected.CreationFileTime)
            return new(false, 0, 0, default, "ProbeSourceChangedOrAmbiguous");
        var observed = candidates[0];
        string imageText = expected.ImageBase.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? expected.ImageBase[2..] : expected.ImageBase;
        if (!ulong.TryParse(imageText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong image)
            || image != observed.Image || observed.Size != expected.ImageSize || entry.Profile.Sha256 != expected.DiskSha256)
            return new(false, 0, 0, default, "ProbeModuleChanged");
        var disk = new FileInfo(Probe.ExePath);
        if (disk.Length != entry.DiskLength || disk.LastWriteTimeUtc != entry.DiskModifiedUtc)
            return new(false, 0, 0, default, "ProbeDiskIdentityChanged");
        using var memory = new ReadSession(expected.ProcessId, expected.CreationFileTime, budget, audit);
        memory.Verify(); memory.Bind(image, observed.Size); memory.UseProfile(entry.Profile);
        byte[] beforePe = memory.Required(entry.Profile.Identity.PeOffset, 0x108);
        if (Probe.U32(beforePe, 0) != 0x4550 || Probe.U16(beforePe, 4) != entry.Profile.Identity.Machine
            || Probe.U16(beforePe, 24) != 0x20B || Probe.U32(beforePe, 8) != expected.PeTimestamp || Probe.U32(beforePe, 80) != expected.ImageSize)
            return new(false, 0, 0, default, "ProbePeIdentityChanged");
        ulong shop = entry.Binding.PlayerShopSubsystem;
        if (shop < 0x10000 || shop > 0x7FFFFFFFFFFFUL - 0x2C8 || (shop & 7) != 0)
            return new(false, 0, 0, default, "ProbeShopPointerInvalid");
        var sampledAt = DateTimeOffset.UtcNow;
        if (!memory.Pointed(shop + 0x2C0, 8, out var first) || first.Length != 8)
            return new(false, 0, 0, sampledAt, "OwnerProbeReadFailed");
        memory.Verify();
        if (!memory.Pointed(shop + 0x2C0, 8, out var second) || second.Length != 8)
            return new(false, 0, 0, sampledAt, "OwnerProbeReadFailed");
        memory.Verify();
        if (!memory.Required(entry.Profile.Identity.PeOffset, 0x108).AsSpan().SequenceEqual(beforePe))
            return new(false, 0, 0, sampledAt, "ProbePeChangedDuringRead");
        disk.Refresh();
        if (disk.Length != entry.DiskLength || disk.LastWriteTimeUtc != entry.DiskModifiedUtc)
            return new(false, 0, 0, sampledAt, "ProbeDiskChangedDuringRead");
        budget.Check();
        return new(true, Probe.U64(first, 0), Probe.U64(second, 0), sampledAt, "OwnerScalarABClosed");
    }
}
