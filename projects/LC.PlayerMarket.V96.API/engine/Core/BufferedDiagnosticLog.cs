using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
namespace MemoryHub;

// Only an explicit allowlist is optional. Native/business journals and every
// unknown kind remain on HubRuntime's original synchronous critical path.
internal sealed class BufferedDiagnosticLog : IDisposable
{
    internal static readonly HashSet<string> OptionalKinds = new(StringComparer.Ordinal)
    {
        "READ_ADMISSION", "READ_CYCLE", "ROUND_METADATA_CLOSURE", "METADATA_CACHE",
        "ACTOR_CAPTURE_COST", "ACTOR_VECTOR_LAYOUT", "NEARBY_COLLECTION_SAMPLE",
        "HUD_VITAL_SAMPLE", "HUD_INFORMATION_SAMPLE", "SERVICE_SELF_SAMPLE", "PERSONAL_SHOP_BANK_PAGE_SAMPLE"
    };
    private readonly BlockingCollection<string> pending = new(64);
    private readonly Thread worker;
    private readonly string directory;
    private readonly Action<string?> reportError;
    private long dropped;
    private int disposed;
    internal long Dropped => Interlocked.Read(ref dropped);
    internal BufferedDiagnosticLog(string directory, Action<string?> reportError)
    {
        this.directory = directory; this.reportError = reportError;
        worker = new Thread(WriteLoop) { Name = "LC optional diagnostics writer", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }
    internal void Append(HubLog entry)
    {
        // Materialize the payload now; a queued anonymous value cannot later
        // change with a live collection. No heap/game value is retimestamped.
        if (!OptionalKinds.Contains(entry.Kind)) throw new InvalidOperationException("Critical diagnostics cannot enter the optional queue.");
        string line = JsonSerializer.Serialize(entry, HubProtocol.Json);
        if (line.Length > 262144 || Volatile.Read(ref disposed) != 0) { Interlocked.Increment(ref dropped); return; }
        try { if (!pending.TryAdd(line)) Interlocked.Increment(ref dropped); }
        catch (InvalidOperationException) { Interlocked.Increment(ref dropped); }
    }
    private void WriteLoop()
    {
        StreamWriter? writer = null; long reported = 0; int batch = 0;
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!pending.IsCompleted)
            {
                bool found = pending.TryTake(out string? line, 100);
                if (found)
                {
                    if (writer is null || writer.BaseStream.Position > 10 * 1024 * 1024)
                    {
                        writer?.Dispose(); Directory.CreateDirectory(directory);
                        writer = new(new FileStream(Path.Combine(directory, $"hub-verbose-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.jsonl"),
                            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 65536);
                    }
                    writer.WriteLine(line); batch++;
                }
                if (writer is not null && (!found || batch >= 64 || sinceFlush.ElapsedMilliseconds >= 100))
                { WriteDropNotice(writer, ref reported); writer.Flush(); batch = 0; sinceFlush.Restart(); }
            }
            if (writer is null && Dropped > 0)
            {
                Directory.CreateDirectory(directory);
                writer = new(new FileStream(Path.Combine(directory, $"hub-verbose-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.jsonl"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 65536);
            }
            if (writer is not null) { WriteDropNotice(writer, ref reported); writer.Flush(); }
        }
        catch (Exception error) { ReportError("非關鍵 LOG 緩衝寫入失敗：" + error.Message); }
        finally { try { writer?.Dispose(); } catch (Exception error) { ReportError("非關鍵 LOG 排空關閉失敗：" + error.Message); } }
    }
    private void ReportError(string message)
    {
        try { reportError(message); } catch { /* Diagnostic callbacks cannot terminate the process. */ }
    }
    private void WriteDropNotice(StreamWriter writer, ref long reported)
    {
        long current = Dropped;
        if (current == reported) return;
        writer.WriteLine(JsonSerializer.Serialize(new HubLog(DateTimeOffset.UtcNow, "VERBOSE_LOG_DROPPED",
            "非關鍵 LOG 佇列已滿；原生意圖／結果／結束與其他關鍵紀錄未經此佇列。",
            new { TotalDropped = current, AdditionalDropped = current - reported, Capacity = 64, MaximumLineCharacters = 262144 }), HubProtocol.Json));
        reported = current;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        pending.CompleteAdding();
        if (!worker.Join(TimeSpan.FromSeconds(10))) throw new IOException("非關鍵 LOG 正常排空尚未完成；未宣稱所有 LOG 已落盤。");
        pending.Dispose();
    }
}
