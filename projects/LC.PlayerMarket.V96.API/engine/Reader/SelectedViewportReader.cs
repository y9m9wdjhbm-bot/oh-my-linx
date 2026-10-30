using System.Text.Json;

namespace MemoryHub.Reader;

internal sealed record SelectedViewportObservation(DateTimeOffset SampledAt, JsonElement? Value, bool ProjectionAvailable, string Detail);

internal static partial class ViewportFieldReader
{
    /// <summary>
    /// Only two exact hash keys. The subsystem registry is visited to bind the
    /// typed root; the actor vectors at +8A8/+8C0/+8D8 are never visited here.
    /// A cache key is not evidence of a currently present/live actor identity.
    /// </summary>
    internal static SelectedViewportObservation CaptureSelected(ReadSession memory, Journal journal, Budget budget, ulong expectedSelfId, ulong targetId)
    {
        var at = DateTimeOffset.UtcNow;
        var c = new Context(memory, journal, budget);
        int checkpoint = memory.MetadataCheckpoint();
        try
        {
            Need(expectedSelfId > 0 && targetId > 0 && expectedSelfId != targetId,
                "VIEWPORT_SELECTED_IDS", "導航需不同的自身與已保存店主 ID");
            ulong gl = c.SceneGl();
            var frameRect = Rect(c.Track(gl + 0x20, 16));
            var scales = c.Track(gl + 0x50, 8);
            float scaleX = F(scales), scaleY = F(scales, 4);
            Need(Valid(frameRect) && frameRect.X == 0 && frameRect.Y == 0 &&
                float.IsFinite(scaleX) && float.IsFinite(scaleY) && scaleX is > 0 and <= 64 && scaleY is > 0 and <= 64,
                "VIEWPORT_FRAME_VALUE", "GLView 矩形或倍率超出有界候選範圍");
            var frame = new Frame(frameRect, scaleX, scaleY, frameRect.Width / scaleX, frameRect.Height / scaleY);
            Need(float.IsFinite(frame.CandidateWidth) && float.IsFinite(frame.CandidateHeight) &&
                frame.CandidateWidth is > 0 and <= 65536 && frame.CandidateHeight is > 0 and <= 65536,
                "VIEWPORT_FRAME_VALUE", "候選視野大小無效");

            ulong root = U(c.Track(memory.Address(0x1642B60), 8, true));
            c.Exact(root, ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@", true);
            Need(c.Track(root + 0xD8, 1, true)[0] != 0, "VIEWPORT_SERVICE_NOT_READY", "遊戲服務尚未就緒");
            ulong holder = U(c.Track(root + 0xD0, 8, true));
            c.Exact(holder, ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@", true);
            var services = c.Vector(holder + 0x180, 128, 128, true, out _, out _);
            ulong actors = 0, character = 0;
            foreach (var pair in services)
            {
                var type = c.TypeAt(pair.Object);
                if (type?.Name == ".?AVActorSubsystem@lineage@@")
                {
                    Need(actors == 0, "VIEWPORT_SERVICE_AMBIGUOUS", "ActorSubsystem 不唯一");
                    c.Exact(pair.Object, type.Name, true); actors = pair.Object;
                }
                else if (type?.Name == ".?AVCharacterDataSubsystem@lineage@@")
                {
                    Need(character == 0, "VIEWPORT_SERVICE_AMBIGUOUS", "CharacterDataSubsystem 不唯一");
                    c.Exact(pair.Object, type.Name, true); character = pair.Object;
                }
            }
            Pointer(actors); Pointer(character);
            ulong own = U(c.Track(character + 0x6D8, 8, true));
            Need(own == expectedSelfId && journal.SelfId == expectedSelfId,
                "GROUP_SOURCE_CHANGED", "導航與保存名單的自身來源不同");

            var table = c.Track(actors + 0x9E0, 64);
            ulong head = U(table, 8), size = U(table, 16), begin = U(table, 24), end = U(table, 32), capacity = U(table, 40), mask = U(table, 48), count = U(table, 56);
            Pointer(head); Pointer(begin); Pointer(end); Pointer(capacity);
            Need(size <= 2048 && count is > 0 and <= 8192 && (count & (count - 1)) == 0 && mask == count - 1 &&
                end >= begin && end - begin == count * 16 && capacity >= end && capacity - begin <= 262144,
                "VIEWPORT_RECT_TABLE", "指定 ID 的矩形雜湊表界線不符");
            var cameraBytes = c.Track(actors + 0xAA0, 24);
            var camera = new Camera(F(cameraBytes), F(cameraBytes, 4), F(cameraBytes, 8), F(cameraBytes, 12), F(cameraBytes, 16), F(cameraBytes, 20));
            Need(new[] { camera.SnapshotX, camera.SnapshotY, camera.SnapshotScale, camera.CurrentX, camera.CurrentY, camera.CurrentScale }.All(float.IsFinite) &&
                Math.Abs(camera.SnapshotX) <= 1_000_000 && Math.Abs(camera.SnapshotY) <= 1_000_000 && Math.Abs(camera.CurrentX) <= 1_000_000 && Math.Abs(camera.CurrentY) <= 1_000_000 &&
                Math.Abs(camera.SnapshotScale) <= 16 && Math.Abs(camera.CurrentScale) <= 16,
                "VIEWPORT_CAMERA_VALUE", "矩形候選相機欄位不是有限有界数值");

            var results = new List<object>();
            bool bothProjected = true;
            foreach (var selected in new[] { (Id: expectedSelfId, Kind: "MyPlayer"), (Id: targetId, Kind: "Player") })
            {
                // This lookup has a read-delegate seam for offline bounds tests.
                var rawBytes = SelectedRectangleLookup.Find(selected.Id, head, size, begin, mask, (address, length) => c.Track(address, length));
                var raw = rawBytes is null ? null : Rect(rawBytes);
                Need(raw is null || Valid(raw), "VIEWPORT_RECT_VALUE", "矩形候選不是有限有界值");
                RectangleData? corrected = null;
                bool? geometric = null;
                if (raw is not null && camera.SnapshotScale == camera.CurrentScale)
                {
                    // Historical arithmetic only: neither current code semantics
                    // nor world/physical-screen mapping are promoted to verified.
                    float divisor = camera.SnapshotScale > BitConverter.UInt32BitsToSingle(0x34000000) ? camera.SnapshotScale : 1;
                    corrected = raw with { X = raw.X - (camera.CurrentX - camera.SnapshotX) / divisor,
                        Y = raw.Y - (camera.CurrentY - camera.SnapshotY) / divisor };
                    if (!Valid(corrected)) corrected = null;
                    else geometric = corrected.X < frame.CandidateWidth && corrected.Y < frame.CandidateHeight &&
                        corrected.X + corrected.Width > 0 && corrected.Y + corrected.Height > 0;
                }
                bothProjected &= corrected is not null;
                results.Add(new { LocalId = selected.Id, selected.Kind, PresentInSampledCollection = false,
                    PresentInSelectedLookup = raw is not null, RectangleState = raw is null ? "CacheEntryMissing" : "RawRectangleObserved",
                    RawRectangle = raw, CorrectedRectangleCandidate = corrected, GeometricCandidate = geometric, InViewport = (bool?)null,
                    CurrentActorIdentityVerified = false, Evidence = "SelectedHashKeyStableAB_ActorIdentityNotRevalidated",
                    Detail = "只查指定 ID 矩形快取；未重新枚舉玩家、未證明目前仍是同一人物或確實可見。" });
            }
            c.Close();
            var value = JsonSerializer.SerializeToElement(new
            {
                Complete = true, CollectionEnumerated = false, QueryScope = "SelfAndSavedOwnerExactKeys", Items = results.ToArray(),
                RawFrame = frame, RawCamera = camera, CurrentCodeSemanticsVerified = false, CurrentActorIdentityVerified = false,
                PhysicalPixelMappingVerified = false, OcclusionKnown = false, InViewport = (bool?)null
            }, HubProtocol.Json);
            return new(at, value, bothProjected, bothProjected
                ? "自身與保存店主的矩形候選已閉合；黃／紅點投影仍供人工比對，未驗證目前人物身分。"
                : "指定 ID 矩形缺失或相機倍率不同；保留自身與保存座標，不推測紅點位置。");
        }
        catch (ProbeFailure error) when (!Fatal(error))
        {
            if (PureDynamicFailure(error)) { c.VerifyRejectedSampleSource(); memory.RollbackMetadataCandidate(checkpoint); }
            else memory.DiscardMetadataCache(error.Code);
            return new(at, null, false, error.Code + "：" + error.Message);
        }
    }
}

/// <summary>Bounded exact-key lookup; no enumeration, game handle or side effects.</summary>
internal static class SelectedRectangleLookup
{
    internal static byte[]? Find(ulong id, ulong head, ulong size, ulong begin, ulong mask, Func<ulong, int, byte[]> read)
    {
        static void Need(bool value, string code, string detail) { if (!value) throw new ProbeFailure(code, detail); }
        static void Pointer(ulong value) => Need(Probe.Pointer(value), "VIEWPORT_POINTER", "矩形雜湊鏈指標無效");
        static ulong U(byte[] bytes, int at = 0) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8));
        Pointer(head); Pointer(begin);
        Need(id > 0 && size <= 2048 && mask <= 8191 && ((mask + 1) & mask) == 0,
            "VIEWPORT_RECT_TABLE", "矩形查詢界線無效");
        ulong hash = 0xCBF29CE484222325;
        for (int i = 0; i < 8; i++) hash = unchecked((hash ^ ((id >> (i * 8)) & 255)) * 0x100000001B3);
        var bucket = read(checked(begin + 16 * (hash & mask)), 16);
        ulong first = U(bucket), cursor = U(bucket, 8); Pointer(first); Pointer(cursor);
        Need((first == head) == (cursor == head), "VIEWPORT_RECT_BUCKET", "矩形桶端點不一致");
        if (cursor == head) return null;
        var seen = new HashSet<ulong>(); byte[]? found = null;
        while (true)
        {
            Need(cursor != head && seen.Count < 16 && seen.Add(cursor) && (ulong)seen.Count <= size,
                "VIEWPORT_RECT_CHAIN", "矩形碰撞鏈循環或超過 16 筆");
            var node = read(cursor, 40);
            if (U(node, 16) == id)
            {
                Need(found is null, "VIEWPORT_RECT_DUPLICATE", "指定 ID 的矩形不唯一");
                found = node.AsSpan(24, 16).ToArray();
            }
            if (cursor == first) break;
            cursor = U(node, 8); Pointer(cursor);
        }
        return found;
    }
}
