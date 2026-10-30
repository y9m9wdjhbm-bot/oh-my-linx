using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record HistoryCacheReadResult(MarketListing[] Listings, int FileCount, int ParsedFileCount, int ReusedFileCount);

/// <summary>
/// Display-only acceleration. File name/length/mtime are metadata fingerprints, NOT
/// proof of authenticity or protection against deliberate same-metadata tampering.
/// Always apply LocalStore.IsListingVisible again at the point of UI adoption.
/// No static object retention; one bounded binary snapshot avoids thousands of small reads.
/// </summary>
internal static class HistoryReadCache
{
    private const int Version = 4, MaximumEntries = 20000, MaximumRows = 262144;
    private const long MaximumCacheBytes = 64L * 1024 * 1024, MaximumSourceBytes = 64L * 1024 * 1024;
    private const int MaximumFileBytes = 1024 * 1024;
    private const string Magic = "LC-HISTORY-CACHE";
    private sealed record Entry(string Name, long Length, long ModifiedTicks, MarketListing Value);
    // A contract addition invalidates this codec instead of silently losing an added field.
    private static readonly string Contract = string.Join("|", new[] { typeof(MarketListing), typeof(MarketOwner), typeof(PersonalShopRow), typeof(ShopPoint), typeof(PersonalShopItemStateEvidence), typeof(ShopEmptyPageObservation), typeof(ShopEmptySlotObservation), typeof(PersonalShopBankSummary) }
        .SelectMany(t => t.GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => t.Name + "." + p.Name + ":" + p.PropertyType.FullName)).Append("ItemStateCertificate=" + PersonalShopBuildCertificate.CertificateSha256));
    private static readonly bool SupportedContract = typeof(MarketListing).GetProperties().Length == 22
        && typeof(MarketOwner).GetProperties().Length == 13 && typeof(PersonalShopRow).GetProperties().Length == 17
        && typeof(ShopPoint).GetProperties().Length == 2 && typeof(PersonalShopItemStateEvidence).GetProperties().Length == 5
        && typeof(ShopEmptyPageObservation).GetProperties().Length == 20 && typeof(ShopEmptySlotObservation).GetProperties().Length == 4
        && typeof(PersonalShopBankSummary).GetProperties().Length == 6;

    internal static string CachePath(string directory) => Path.Combine(Path.GetFullPath(directory), ".history-read-cache-v4.bin");

    internal static HistoryCacheReadResult Load(string historyDirectory, CancellationToken token = default, Action<string>? error = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(historyDirectory)) return new([], 0, 0, 0);
        string cachePath = CachePath(historyDirectory);
        var files = new DirectoryInfo(historyDirectory).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        token.ThrowIfCancellationRequested();
        Dictionary<string, Entry> previous;
        try { previous = SupportedContract ? ReadSnapshot(cachePath, token) : new(StringComparer.Ordinal); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { error?.Invoke("History cache ignored: " + ex.Message); previous = new(StringComparer.Ordinal); }
        var values = new List<MarketListing>(files.Length);
        var next = new List<Entry>(Math.Min(files.Length, MaximumEntries));
        int parsed = 0, reused = 0, rows = 0;
        long sourceBytes = 0;
        bool cacheable = SupportedContract, changed = previous.Count != files.Length;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                file.Refresh();
                if (!file.Exists) { changed = true; continue; }
                long length = file.Length, ticks = file.LastWriteTimeUtc.Ticks;
                if (length > MaximumFileBytes) throw new InvalidDataException("History file exceeds 1 MiB");
                MarketListing value;
                if (previous.TryGetValue(file.Name, out var old) && old.Length == length && old.ModifiedTicks == ticks)
                { value = old.Value; reused++; }
                else
                {
                    parsed++; changed = true;
                    // Bound the actual opened file too, including replacements after directory enumeration.
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                    if (stream.Length > MaximumFileBytes) throw new InvalidDataException("History file exceeds 1 MiB");
                    value = JsonSerializer.Deserialize<MarketListing>(stream, HubProtocol.Json)!;
                    if (!LocalStore.Valid(value)) throw new InvalidDataException("Invalid history listing");
                }
                token.ThrowIfCancellationRequested();
                // Never retain/return a cached value if the source disappeared or changed during this read.
                file.Refresh();
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != ticks)
                { changed = true; continue; }
                values.Add(value);
                sourceBytes += length; rows += value.Rows.Length;
                if (cacheable && (next.Count >= MaximumEntries || sourceBytes > MaximumSourceBytes || rows > MaximumRows))
                { cacheable = false; next.Clear(); }
                if (cacheable) next.Add(new(file.Name, length, ticks, value));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { changed = true; error?.Invoke($"History read skipped {file.Name}: {ex.Message}"); }
        }
        token.ThrowIfCancellationRequested();
        if (cacheable && (changed || !File.Exists(cachePath)))
        {
            try { WriteSnapshot(cachePath, next, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { error?.Invoke("History cache write skipped: " + ex.Message); }
        }
        // The output is never truncated by cache budgets; oversized histories use source reads.
        return new(values.ToArray(), files.Length, parsed, reused);
    }

    private static Dictionary<string, Entry> ReadSnapshot(string path, CancellationToken token)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(path)) return entries;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);
        if (file.Length < 32 || file.Length > MaximumCacheBytes) throw new InvalidDataException("Cache size limit");
        long payloadEnd = file.Length - 32;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long remaining = payloadEnd;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, count); remaining -= count;
            }
            byte[] expected = new byte[32]; file.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset())) throw new InvalidDataException("Cache checksum mismatch");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        file.Position = 0;
        using var reader = new BinaryReader(file, Encoding.UTF8, true);
        if (ReadString(reader) != Magic || reader.ReadInt32() != Version || ReadString(reader) != Contract)
            throw new InvalidDataException("Cache schema mismatch");
        int countEntries = reader.ReadInt32();
        if (countEntries < 0 || countEntries > MaximumEntries) throw new InvalidDataException("Cache entry limit");
        long sourceBytes = 0; int rows = 0;
        for (int i = 0; i < countEntries; i++)
        {
            token.ThrowIfCancellationRequested();
            string name = ReadString(reader) ?? throw new InvalidDataException("Cache file name");
            long length = reader.ReadInt64(), ticks = reader.ReadInt64();
            if (Path.GetFileName(name) != name || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || length < 0 || length > MaximumFileBytes || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Cache metadata");
            sourceBytes += length;
            if (sourceBytes > MaximumSourceBytes) throw new InvalidDataException("Cache source budget");
            var value = ReadListing(reader);
            rows += value.Rows.Length;
            if (rows > MaximumRows || !LocalStore.Valid(value) || !entries.TryAdd(name, new(name, length, ticks, value)))
                throw new InvalidDataException("Invalid cache listing");
        }
        if (file.Position != payloadEnd) throw new InvalidDataException("Cache trailing or truncated data");
        return entries;
    }

    private static void WriteSnapshot(string path, List<Entry> entries, CancellationToken token)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.SequentialScan))
            {
                using var writer = new BinaryWriter(file, Encoding.UTF8, true);
                WriteString(writer, Magic); writer.Write(Version); WriteString(writer, Contract); writer.Write(entries.Count);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    WriteString(writer, entry.Name); writer.Write(entry.Length); writer.Write(entry.ModifiedTicks); WriteListing(writer, entry.Value);
                    if (file.Position > MaximumCacheBytes - 32) throw new InvalidDataException("Cache disk budget");
                }
                writer.Flush(); file.Position = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    int count;
                    while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                    { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
                    file.Write(hash.GetHashAndReset());
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            // Atomic replacement on the same volume; a canceled/failed write retains the old snapshot.
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteString(BinaryWriter w, string? value)
    {
        if (value is null) { w.Write(-1); return; }
        int length = Encoding.UTF8.GetByteCount(value);
        if (length > MaximumFileBytes) throw new InvalidDataException("Cache string size");
        w.Write(length); w.Write(Encoding.UTF8.GetBytes(value));
    }
    private static string? ReadString(BinaryReader r)
    {
        int length = r.ReadInt32();
        if (length == -1) return null;
        if (length < 0 || length > MaximumFileBytes || length > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Cache string size");
        byte[] bytes = r.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    private static void WriteTime(BinaryWriter w, DateTimeOffset t) { w.Write(t.Ticks); w.Write((short)t.Offset.TotalMinutes); }
    private static DateTimeOffset ReadTime(BinaryReader r) => new(r.ReadInt64(), TimeSpan.FromMinutes(r.ReadInt16()));
    private static void WriteNullable<T>(BinaryWriter w, T? v, Action<BinaryWriter, T> write) where T : struct
    { w.Write(v.HasValue); if (v is { } value) write(w, value); }
    private static T? ReadNullable<T>(BinaryReader r, Func<BinaryReader, T> read) where T : struct => r.ReadBoolean() ? read(r) : null;
    private static void WritePoint(BinaryWriter w, ShopPoint? p) { w.Write(p is not null); if (p is not null) { w.Write(p.X); w.Write(p.Y); } }
    private static ShopPoint? ReadPoint(BinaryReader r) => r.ReadBoolean() ? new(r.ReadInt32(), r.ReadInt32()) : null;

    private static void WriteListing(BinaryWriter w, MarketListing v)
    {
        WriteString(w, v.Revision); WriteString(w, v.SourceSessionKey); WriteString(w, v.RequestId);
        var o = v.Owner;
        w.Write(o.Id); WriteString(w, o.LocalIdentity); WriteString(w, o.Name); WritePoint(w, o.Position); WriteString(w, o.MapName);
        WriteTime(w, o.ObservedAt); WriteNullable(w, o.InViewport, (b, n) => b.Write(n)); WriteString(w, o.VisibilityEvidence); WriteString(w, o.ActionType);
        WriteString(w, o.ShopState); WriteNullable(w, o.ShopStateSampledAt, WriteTime); WriteString(w, o.ShopStateEvidence); WriteString(w, o.ShopStateDetail);
        WriteString(w, v.ViewerDirection); WriteTime(w, v.CapturedAt); WriteNullable(w, v.RequestedAt, WriteTime);
        WriteNullable(w, v.PageDurationMs, (b, n) => b.Write(n)); WriteNullable(w, v.SourceReadDurationMs, (b, n) => b.Write(n));
        WriteNullable(w, v.NativeDurationMs, (b, n) => b.Write(n)); WriteNullable(w, v.ResponseReadDurationMs, (b, n) => b.Write(n));
        w.Write(v.ResponseReadAttempts); WritePoint(w, v.SelfPositionAtRequest); WriteNullable(w, v.DistanceAtRequest, (b, n) => b.Write(n));
        WriteString(w, v.State); WriteString(w, v.Detail); w.Write(v.OwnerAndResponseMatched); w.Write(v.PageEmpty); w.Write(v.Complete);
        w.Write(v.EmptyPageObserved); WriteEmptyObservation(w, v.EmptyPageObservation); w.Write(v.Rows.Length);
        foreach (var row in v.Rows)
        {
            w.Write(row.RowIndex); WriteString(w, row.State); w.Write(row.IncludedInListing); WriteString(w, row.Name); WriteString(w, row.PriceRaw);
            WriteNullable(w, row.Price, (b, n) => b.Write(n)); WriteString(w, row.UnitRaw); WriteNullable(w, row.Quantity, (b, n) => b.Write(n));
            WriteString(w, row.QuantityState); WriteString(w, row.Detail); WriteNullable(w, row.ItemKindRaw, (b, n) => b.Write(n));
            WriteNullable(w, row.ItemKindDetailFlag, (b, n) => b.Write(n)); WriteString(w, row.ItemKind); WriteString(w, row.ItemKindState); WriteString(w, row.ItemIdentificationState); WriteString(w, row.ItemIdentificationEvidence); WriteItemEvidence(w, row.ItemStateEvidence);
        }
    }
    // Preserve the complete neutral evidence graph; shared string bounds remain 1 MiB.
    private static void WriteEmptyObservation(BinaryWriter writer, ShopEmptyPageObservation? value)
        => WriteString(writer, value is null ? null : JsonSerializer.Serialize(value, HubProtocol.Json));
    private static ShopEmptyPageObservation? ReadEmptyObservation(BinaryReader reader)
    {
        string? encoded = ReadString(reader);
        return encoded is null ? null : JsonSerializer.Deserialize<ShopEmptyPageObservation>(encoded, HubProtocol.Json)
            ?? throw new InvalidDataException("Cache empty observation is null");
    }
    private static void WriteItemEvidence(BinaryWriter writer, PersonalShopItemStateEvidence? value)
    {
        writer.Write(value is not null); if (value is null) return;
        WriteString(writer,value.Mode);WriteString(writer,value.SourceBuildSha256);WriteString(writer,value.CertificateSha256);
        writer.Write(value.CurrentRuntimeCodeVerified);writer.Write(value.CurrentMetadataVerified);
    }
    private static PersonalShopItemStateEvidence? ReadItemEvidence(BinaryReader reader) => !reader.ReadBoolean() ? null : new()
    {
        Mode=ReadString(reader)!,SourceBuildSha256=ReadString(reader)!,CertificateSha256=ReadString(reader)!,
        CurrentRuntimeCodeVerified=reader.ReadBoolean(),CurrentMetadataVerified=reader.ReadBoolean()
    };
    private static MarketListing ReadListing(BinaryReader r)
    {
        string revision = ReadString(r)!, source = ReadString(r)!, request = ReadString(r)!;
        var owner = new MarketOwner
        {
            Id = r.ReadUInt64(), LocalIdentity = ReadString(r)!, Name = ReadString(r)!, Position = ReadPoint(r), MapName = ReadString(r),
            ObservedAt = ReadTime(r), InViewport = ReadNullable(r, b => b.ReadBoolean()), VisibilityEvidence = ReadString(r)!, ActionType = ReadString(r),
            ShopState = ReadString(r)!, ShopStateSampledAt = ReadNullable(r, ReadTime), ShopStateEvidence = ReadString(r)!, ShopStateDetail = ReadString(r)!
        };
        var value = new MarketListing
        {
            Revision = revision, SourceSessionKey = source, RequestId = request, Owner = owner,
            ViewerDirection = ReadString(r)!, CapturedAt = ReadTime(r), RequestedAt = ReadNullable(r, ReadTime),
            PageDurationMs = ReadNullable(r, b => b.ReadDouble()), SourceReadDurationMs = ReadNullable(r, b => b.ReadDouble()),
            NativeDurationMs = ReadNullable(r, b => b.ReadDouble()), ResponseReadDurationMs = ReadNullable(r, b => b.ReadDouble()),
            ResponseReadAttempts = r.ReadInt32(), SelfPositionAtRequest = ReadPoint(r), DistanceAtRequest = ReadNullable(r, b => b.ReadInt64()),
            State = ReadString(r)!, Detail = ReadString(r)!, OwnerAndResponseMatched = r.ReadBoolean(), PageEmpty = r.ReadBoolean(), Complete = r.ReadBoolean(),
            EmptyPageObserved = r.ReadBoolean(), EmptyPageObservation = ReadEmptyObservation(r)
        };
        int count = r.ReadInt32();
        if (count < 0 || count > 256) throw new InvalidDataException("Cache row limit");
        var rows = new PersonalShopRow[count];
        for (int i = 0; i < count; i++)
            rows[i] = new(r.ReadInt32(), ReadString(r)!, r.ReadBoolean(), ReadString(r), ReadString(r), ReadNullable(r, b => b.ReadUInt64()),
                ReadString(r), ReadNullable(r, b => b.ReadUInt64()), ReadString(r)!, ReadString(r)!)
            {
                ItemKindRaw = ReadNullable(r, b => b.ReadInt32()), ItemKindDetailFlag = ReadNullable(r, b => b.ReadByte()),
                ItemKind = ReadString(r), ItemKindState = ReadString(r)!, ItemIdentificationState = ReadString(r)!, ItemIdentificationEvidence = ReadString(r)!, ItemStateEvidence = ReadItemEvidence(r)
            };
        return value with { Rows = rows };
    }
}
