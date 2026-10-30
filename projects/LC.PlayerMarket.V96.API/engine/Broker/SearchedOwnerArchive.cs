using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryHub.Service;

internal sealed record SearchedOwnerArchiveMetadata(
    [property: JsonRequired] int Version,
    [property: JsonRequired] int EventCount,
    [property: JsonRequired] int TailSequence,
    [property: JsonRequired] long ArchiveBytes,
    [property: JsonRequired] string ArchiveSha256,
    [property: JsonRequired] string TailOriginalFileSHA,
    [property: JsonRequired] string LegacyHeadSHA);

internal sealed record SearchedOwnerArchiveReadResult(IReadOnlyList<SearchedOwnerEvent> Events,
    int TailSequence, string TailOriginalFileSHA, SearchedOwnerArchiveMetadata Metadata);

/// <summary>
/// An exact-byte prefix of an append-only ledger, never a semantic snapshot.
/// Callers must Apply every returned event through the existing Validate path,
/// then apply only subsequent numbered JSON events and validate the current head.
/// SHA256 detects damage; it is not a malicious-tampering authenticity guarantee.
/// </summary>
internal static class SearchedOwnerArchive
{
    private const int Version = 1, MaximumEvents = 262144, MaximumEventBytes = 128 * 1024;
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const string ArchiveName = "events.archive", CommitName = "archive.checkpoint", PendingName = ".archive-pending";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LC-SOA-1");

    internal static SearchedOwnerArchiveMetadata CreateFromLegacy(string sourceDir, string newDir, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(newDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive destination must be independent of the legacy source");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Legacy ledger directory missing");
        // Open existing lock read-only: acquiring this lock must never create or alter legacy files.
        using var sourceLock = new FileStream(Path.Combine(source, "writer.lock"), FileMode.Open, FileAccess.Read, FileShare.None);
        if (File.Exists(Path.Combine(source, ArchiveName)) || File.Exists(Path.Combine(source, CommitName)) || File.Exists(Path.Combine(source, PendingName)))
            throw new InvalidDataException("Source is not a plain legacy event ledger");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidDataException("Archive destination must be new or empty");
        var files = Directory.EnumerateFiles(source, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).Take(MaximumEvents + 1).ToArray();
        if (files.Length is < 1 or > MaximumEvents) throw new InvalidDataException("Legacy event count limit");
        byte[] headBytes = ReadBytes(Path.Combine(source, "head.checkpoint"), 4096);
        var head = JsonSerializer.Deserialize<SearchedOwnerHead>(headBytes, HubProtocol.Json)
            ?? throw new InvalidDataException("Legacy head missing");
        if (head.Sequence != files.Length || !ValidHash(head.Sha256))
            throw new InvalidDataException("Legacy head does not commit the complete numbered event tail");
        Directory.CreateDirectory(target);
        using var destinationLock = new FileStream(Path.Combine(target, "writer.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        // Surviving artifacts, including this marker, require a valid commit on Read; never silently downgrade to empty state.
        using (var pending = new FileStream(Path.Combine(target, PendingName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { pending.WriteByte(1); pending.Flush(true); }
        string archiveTemp = Path.Combine(target, ArchiveName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        string tailHash = "";
        using (var archive = new FileStream(archiveTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
        {
            using var writer = new BinaryWriter(archive, Encoding.UTF8, true);
            writer.Write(Magic); writer.Write(Version); writer.Write(files.Length);
            DateTimeOffset previousAt = default;
            for (int i = 0; i < files.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                byte[] bytes = ReadBytes(files[i], MaximumEventBytes);
                var item = ReadEvent(bytes, i + 1, previousAt);
                if (Path.GetFileName(files[i]) != Name(item.Sequence)) throw new InvalidDataException("Legacy event file name or sequence mismatch");
                previousAt = item.At;
                if (archive.Position + 4 + bytes.Length > MaximumArchiveBytes) throw new InvalidDataException("Archive byte limit");
                writer.Write(bytes.Length); writer.Write(bytes);
                if (i == files.Length - 1) tailHash = Hash(bytes);
            }
            writer.Flush(); archive.Flush(true);
        }
        if (!string.Equals(tailHash, head.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("Legacy committed tail checksum mismatch");
        token.ThrowIfCancellationRequested();
        var metadata = new SearchedOwnerArchiveMetadata(Version, files.Length, head.Sequence, new FileInfo(archiveTemp).Length,
            FileHash(archiveTemp), tailHash, Hash(headBytes));
        // Commit order: durable blob, exact legacy head, then durable metadata atomically renamed last.
        File.Move(archiveTemp, Path.Combine(target, ArchiveName), false);
        AtomicWrite(Path.Combine(target, "head.checkpoint"), headBytes);
        AtomicWrite(Path.Combine(target, CommitName), JsonSerializer.SerializeToUtf8Bytes(metadata, HubProtocol.Json));
        File.Delete(Path.Combine(target, PendingName));
        return metadata;
    }

    internal static SearchedOwnerArchiveReadResult? Read(string root, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string archivePath = Path.Combine(root, ArchiveName), commitPath = Path.Combine(root, CommitName);
        if (!File.Exists(archivePath) && !File.Exists(commitPath) && !File.Exists(Path.Combine(root, PendingName)))
        {
            if (Directory.Exists(root) && (Directory.EnumerateFiles(root, ArchiveName + ".*.tmp").Any()
                || Directory.EnumerateFiles(root, CommitName + ".*.tmp").Any()))
                throw new InvalidDataException("Uncommitted archive artifacts");
            return null;
        }
        if (!File.Exists(archivePath) || !File.Exists(commitPath)) throw new InvalidDataException("Archive commit is incomplete");
        var metadata = JsonSerializer.Deserialize<SearchedOwnerArchiveMetadata>(ReadBytes(commitPath, 4096), HubProtocol.Json)
            ?? throw new InvalidDataException("Archive checkpoint is invalid");
        if (metadata.Version != Version || metadata.EventCount is < 1 or > MaximumEvents || metadata.TailSequence != metadata.EventCount
            || metadata.ArchiveBytes is < 16 or > MaximumArchiveBytes || !ValidHash(metadata.ArchiveSha256)
            || !ValidHash(metadata.TailOriginalFileSHA) || !ValidHash(metadata.LegacyHeadSHA))
            throw new InvalidDataException("Archive checkpoint bounds or schema invalid");
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (archive.Length != metadata.ArchiveBytes || !string.Equals(StreamHash(archive, token), metadata.ArchiveSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Archive checksum or length mismatch");
        archive.Position = 0;
        using var reader = new BinaryReader(archive, Encoding.UTF8, true);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadInt32() != Version || reader.ReadInt32() != metadata.EventCount)
            throw new InvalidDataException("Archive header invalid");
        var events = new SearchedOwnerEvent[metadata.EventCount];
        DateTimeOffset previousAt = default;
        string tailHash = "";
        for (int i = 0; i < events.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            int length = reader.ReadInt32();
            if (length is < 1 or > MaximumEventBytes || length > archive.Length - archive.Position)
                throw new InvalidDataException("Archive event size invalid");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            events[i] = ReadEvent(bytes, i + 1, previousAt); previousAt = events[i].At;
            if (i == events.Length - 1) tailHash = Hash(bytes);
        }
        if (archive.Position != archive.Length || !string.Equals(tailHash, metadata.TailOriginalFileSHA, StringComparison.Ordinal))
            throw new InvalidDataException("Archive tail or trailing data invalid");
        byte[] headBytes = ReadBytes(Path.Combine(root, "head.checkpoint"), 4096);
        var head = JsonSerializer.Deserialize<SearchedOwnerHead>(headBytes, HubProtocol.Json) ?? throw new InvalidDataException("Current head invalid");
        if (head.Sequence < metadata.TailSequence || head.Sequence > MaximumEvents || !ValidHash(head.Sha256))
            throw new InvalidDataException("Current head precedes the committed archive");
        if (head.Sequence == metadata.TailSequence && (!string.Equals(head.Sha256, tailHash, StringComparison.Ordinal)
            || !string.Equals(Hash(headBytes), metadata.LegacyHeadSHA, StringComparison.Ordinal)))
            throw new InvalidDataException("Original legacy head or tail checksum mismatch");
        // A newer head is verified against subsequent JSON by the existing Store.ValidateHead.
        return new(events, metadata.TailSequence, tailHash, metadata);
    }

    private static SearchedOwnerEvent ReadEvent(byte[] bytes, int sequence, DateTimeOffset previousAt)
    {
        var value = JsonSerializer.Deserialize<SearchedOwnerEvent>(bytes, HubProtocol.Json) ?? throw new InvalidDataException("Archive event null");
        if (value.Schema != 1 || value.Sequence != sequence || value.At == default || value.At < previousAt
            || !Guid.TryParseExact(value.Epoch, "N", out _)) throw new InvalidDataException("Archive event sequence/time/epoch invalid");
        return value;
    }
    private static byte[] ReadBytes(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Archive input file size invalid");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, false);
    }
    private static string Name(int sequence) => sequence.ToString("D10", CultureInfo.InvariantCulture) + ".json";
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string FileHash(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan); return StreamHash(file); }
    private static string StreamHash(Stream stream, CancellationToken token = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; int length;
        while ((length = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, length); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
