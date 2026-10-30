using System.Buffers.Binary;
using System.Text;

namespace MemoryHub.Reader;

/// <summary>
/// Bounded, borrowed-handle observations. No HWND, OS input, actor XY/name leaf,
/// game function call, memory write or cached send-path address is exposed.
/// Current rectangle-code semantics remain unverified: InViewport is always null.
/// </summary>
internal static partial class ViewportFieldReader
{
    private const int MaximumActors = 128;
    private sealed record TypeInfo(string Name, ulong Vtable);
    private sealed record Pair(ulong Object, ulong Control);
    private sealed record RectangleData(float X, float Y, float Width, float Height);
    private sealed record Frame(RectangleData RawRectangle, float ScaleX, float ScaleY,
        float CandidateWidth, float CandidateHeight);
    private sealed record Camera(float SnapshotX, float SnapshotY, float SnapshotScale,
        float CurrentX, float CurrentY, float CurrentScale);
    private sealed record ActorResult(ulong LocalId, string Kind, bool PresentInSampledCollection,
        string RectangleState, RectangleData? RawRectangle, RectangleData? CorrectedRectangleCandidate,
        bool? GeometricCandidate, bool? InViewport, string Evidence, string Detail);

    internal static void Capture(ReadSession memory, Journal journal, Budget budget, ReadSelection selection)
    {
        if (selection.Enabled("cursorPosition")) Run("cursorPosition", CaptureCursor);
        if (selection.Enabled("actorViewport")) Run("actorViewport", CaptureActors);

        void Run(string key, Action<Context> read)
        {
            var context = new Context(memory, journal, budget);
            int metadataCheckpoint = memory.MetadataCheckpoint();
            try { read(context); }
            catch (ProbeFailure error) when (!Fatal(error))
            {
                if (PureDynamicFailure(error))
                {
                    // This field never completed its value closure. Drop only
                    // metadata newly staged by it; preserve earlier closed work.
                    // A concurrent source change is fatal even for a value error.
                    context.VerifyRejectedSampleSource();
                    memory.RollbackMetadataCandidate(metadataCheckpoint);
                }
                else memory.DiscardMetadataCache(error.Code);
                journal.Unknown(key, error.Code + "：" + error.Message +
                    (error.NativeError == 0 ? "" : $"（Win32 {error.NativeError}）"));
            }
        }
    }

    private static void CaptureCursor(Context c)
    {
        var at = DateTimeOffset.UtcNow;
        ulong gl = c.SceneGl();
        // This field alone never touches actor vectors, camera, saved click or XY.
        var raw = c.Track(gl + 0xB0, 8);
        float x = F(raw), y = F(raw, 4);
        Need(float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x) <= 1_000_000 && Math.Abs(y) <= 1_000_000,
            "VIEWPORT_CURSOR_VALUE", "GLView 游標快取不是有限有界 float2");
        c.Close();
        c.Journal.Set("cursorPosition", new
        {
            X = x, Y = y,
            CoordinateSpace = "GLViewRawFloat2",
            IsSavedClick = false,
            PhysicalPixelMappingVerified = false,
            GameWorldMappingVerified = false
        }, at, "TypedGLViewStableAB_RawCursorOnly");
    }

    private static void CaptureActors(Context c)
    {
        var at = DateTimeOffset.UtcNow;
        ulong gl = c.SceneGl();
        var rectangle = c.Track(gl + 0x20, 16);
        var scales = c.Track(gl + 0x50, 8);
        var frameRect = Rect(rectangle);
        float scaleX = F(scales), scaleY = F(scales, 4);
        Need(Valid(frameRect) && frameRect.X == 0 && frameRect.Y == 0 &&
            float.IsFinite(scaleX) && float.IsFinite(scaleY) && scaleX is > 0 and <= 64 && scaleY is > 0 and <= 64,
            "VIEWPORT_FRAME_VALUE", "GLView 矩形或倍率超出有界候選範圍");
        var frame = new Frame(frameRect, scaleX, scaleY, frameRect.Width / scaleX, frameRect.Height / scaleY);
        Need(float.IsFinite(frame.CandidateWidth) && float.IsFinite(frame.CandidateHeight) &&
            frame.CandidateWidth is > 0 and <= 65536 && frame.CandidateHeight is > 0 and <= 65536,
            "VIEWPORT_FRAME_VALUE", "候選視野大小無效");

        ulong root = U(c.Track(c.Memory.Address(0x1642B60), 8, true));
        c.Exact(root, ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@", true);
        Need(c.Track(root + 0xD8, 1, true)[0] != 0, "VIEWPORT_SERVICE_NOT_READY", "遊戲服務尚未就緒");
        ulong holder = U(c.Track(root + 0xD0, 8, true));
        c.Exact(holder, ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@", true);
        var services = c.Vector(holder + 0x180, 128, 128, true, out _, out _);
        ulong subsystem = 0, character = 0;
        foreach (var pair in services)
        {
            var type = c.TypeAt(pair.Object);
            if (type?.Name == ".?AVActorSubsystem@lineage@@")
            {
                Need(subsystem == 0, "VIEWPORT_SERVICE_AMBIGUOUS", "ActorSubsystem 不唯一");
                c.Exact(pair.Object, type.Name, true); subsystem = pair.Object;
            }
            else if (type?.Name == ".?AVCharacterDataSubsystem@lineage@@")
            {
                Need(character == 0, "VIEWPORT_SERVICE_AMBIGUOUS", "CharacterDataSubsystem 不唯一");
                c.Exact(pair.Object, type.Name, true); character = pair.Object;
            }
        }
        Pointer(subsystem); Pointer(character);
        ulong ownId = U(c.Track(character + 0x6D8, 8, true));
        Need(ownId is > 0 and < ulong.MaxValue, "VIEWPORT_SELF_KEY", "自身 key 尚未有效");
        if (c.Journal.SelfId is { } expected) Need(expected == ownId, "GROUP_SOURCE_CHANGED", "視野與本輪自身 ID 不同");

        var pairs = new Dictionary<ulong, Pair>();
        bool complete = true;
        int inspectedMemberships = 0, totalMemberships = 0;
        foreach (ulong offset in new ulong[] { 0x8A8, 0x8C0, 0x8D8 })
        {
            var rows = c.Vector(subsystem + offset, 512, MaximumActors - inspectedMemberships, false, out int total, out int inspected);
            totalMemberships += total; inspectedMemberships += inspected;
            complete &= inspected == total;
            foreach (var pair in rows)
            {
                if (pairs.TryGetValue(pair.Object, out var prior))
                    Need(prior.Control == pair.Control, "VIEWPORT_ACTOR_OWNER", "同一物件的 shared_ptr 所屬衝突");
                else pairs.Add(pair.Object, pair);
            }
        }
        var selected = new List<(ulong Id, string Kind)>();
        var ids = new HashSet<ulong>();
        int ownMatches = 0;
        foreach (var pair in pairs.Values)
        {
            var type = c.TypeAt(pair.Object);
            string? kind = type?.Name switch
            {
                ".?AVMyPlayer@lineage@@" => "MyPlayer",
                ".?AVPlayer@lineage@@" => "Player",
                ".?AVMonster@lineage@@" => "Monster",
                ".?AVInteractiveNPC@lineage@@" => "InteractiveNPC",
                _ => null
            };
            if (kind is null) continue;
            c.Exact(pair.Object, type!.Name);
            ulong getter = U(c.Meta(type.Vtable + 8, 8));
            c.MetaRange(getter, 5);
            Need(c.Memory.Executable(getter), "VIEWPORT_ID_GETTER", "Actor ID getter 不在已載入可執行頁");
            Need(c.Meta(getter, 5).AsSpan().SequenceEqual(new byte[] { 0x48, 0x8B, 0x41, 0x18, 0xC3 }),
                "VIEWPORT_ID_GETTER", "所選 Actor 的完整 ID getter 需要重新驗證");
            ulong id = U(c.Track(pair.Object + 0x18, 8));
            Need(id is > 0 and < ulong.MaxValue && ids.Add(id), "VIEWPORT_ID_AMBIGUOUS", "所選 Actor ID 無效或重複");
            if (kind == "MyPlayer" && id == ownId) ownMatches++;
            selected.Add((id, kind));
        }
        Need(ownMatches == 1, "VIEWPORT_SELF_UNCONFIRMED", "128 筆界線內尚無唯一相符的 MyPlayer；不聲稱完整場景");

        // Saved-layout candidate only. No old code/global/constant RVA is read.
        var table = c.Track(subsystem + 0x9E0, 64);
        ulong head = U(table, 8), size = U(table, 16), begin = U(table, 24), end = U(table, 32), capacity = U(table, 40), mask = U(table, 48), count = U(table, 56);
        Pointer(head); Pointer(begin); Pointer(end); Pointer(capacity);
        Need(size <= 2048 && count is > 0 and <= 8192 && (count & (count - 1)) == 0 && mask == count - 1 &&
            end >= begin && end - begin == count * 16 && capacity >= end && capacity - begin <= 262144,
            "VIEWPORT_RECT_TABLE", "矩形候選雜湊表界線不符");
        var cameraBytes = c.Track(subsystem + 0xAA0, 24);
        var camera = new Camera(F(cameraBytes), F(cameraBytes, 4), F(cameraBytes, 8), F(cameraBytes, 12), F(cameraBytes, 16), F(cameraBytes, 20));
        Need(new[] { camera.SnapshotX, camera.SnapshotY, camera.SnapshotScale, camera.CurrentX, camera.CurrentY, camera.CurrentScale }.All(float.IsFinite) &&
            Math.Abs(camera.SnapshotX) <= 1_000_000 && Math.Abs(camera.SnapshotY) <= 1_000_000 && Math.Abs(camera.CurrentX) <= 1_000_000 && Math.Abs(camera.CurrentY) <= 1_000_000 &&
            Math.Abs(camera.SnapshotScale) <= 16 && Math.Abs(camera.CurrentScale) <= 16,
            "VIEWPORT_CAMERA_VALUE", "矩形候選相機欄位不是有限有界數值");

        var results = new List<ActorResult>();
        foreach (var actor in selected)
        {
            var raw = Lookup(actor.Id);
            RectangleData? candidate = null;
            bool? geometric = null;
            if (raw is not null && camera.SnapshotScale == camera.CurrentScale)
            {
                // This numeric rule is a labelled historical hypothesis. The current
                // code and relocated constants have NOT been accepted as verified.
                float candidateScale = camera.SnapshotScale > BitConverter.UInt32BitsToSingle(0x34000000) ? camera.SnapshotScale : 1;
                candidate = raw with { X = raw.X - (camera.CurrentX - camera.SnapshotX) / candidateScale,
                    Y = raw.Y - (camera.CurrentY - camera.SnapshotY) / candidateScale };
                if (!Valid(candidate)) candidate = null;
                else geometric = candidate.X < frame.CandidateWidth && candidate.Y < frame.CandidateHeight && candidate.X + candidate.Width > 0 && candidate.Y + candidate.Height > 0;
            }
            results.Add(new(actor.Id, actor.Kind, true, raw is null ? "CacheEntryMissing" : "RawRectangleObserved",
                raw, candidate, geometric, null, "CurrentTypedSourceStableAB_SavedLayoutCandidateOnly",
                raw is null ? "此 ID 沒有矩形快取，不能據此判斷物件不存在或不在畫面。" :
                "矩形與相機欄位穩定；幾何換算為歷史算法候選，尚無新版程式碼證據，InViewport 未確認。"));
        }
        c.Close();
        c.Journal.BindSelf(ownId);
        c.Journal.Set("actorViewport", new
        {
            Complete = complete, TotalMemberships = totalMemberships, InspectedMemberships = inspectedMemberships,
            MaximumActors, Items = results.ToArray(), RawFrame = frame, RawCamera = camera,
            CurrentCodeSemanticsVerified = false, PhysicalPixelMappingVerified = false,
            OcclusionKnown = false, InViewport = (bool?)null
        }, at, "CurrentTypedSourceStableAB_RawRectangles_GeometryUnverified");

        RectangleData? Lookup(ulong id)
        {
            ulong hash = 0xCBF29CE484222325;
            for (int i = 0; i < 8; i++) hash = unchecked((hash ^ ((id >> (i * 8)) & 255)) * 0x100000001B3);
            var bucket = c.Track(begin + 16 * (hash & mask), 16);
            ulong first = U(bucket), cursor = U(bucket, 8); Pointer(first); Pointer(cursor);
            Need((first == head) == (cursor == head), "VIEWPORT_RECT_BUCKET", "矩形候選桶端點不一致");
            if (cursor == head) return null;
            var seen = new HashSet<ulong>(); RectangleData? found = null;
            while (true)
            {
                Need(cursor != head && seen.Count < 16 && seen.Add(cursor) && (ulong)seen.Count <= size,
                    "VIEWPORT_RECT_CHAIN", "矩形候選碰撞鏈循環或超過 16 筆");
                var node = c.Track(cursor, 40);
                if (U(node, 16) == id)
                {
                    Need(found is null, "VIEWPORT_RECT_DUPLICATE", "同一 ID 有多個矩形候選");
                    found = Rect(node, 24); Need(Valid(found), "VIEWPORT_RECT_VALUE", "矩形候選不是有限有界值");
                }
                if (cursor == first) break;
                cursor = U(node, 8); Pointer(cursor);
            }
            return found;
        }
    }

    private sealed class Context(ReadSession memory, Journal journal, Budget budget)
    {
        internal ReadSession Memory => memory;
        internal Journal Journal => journal;
        private readonly Dictionary<(ulong Address, int Size), byte[]> proofs = new();
        private readonly HashSet<(ulong Address, int Size)> sourceKeys = new();
        private readonly HashSet<(ulong Address, int Size)> metadataKeys = new();
        private readonly Dictionary<ulong, TypeInfo?> types = new();
        private long reads, bytes;
        private ulong director, scene;

        private byte[] Read(ulong at, int size)
        {
            budget.Check();
            Need(size is > 0 and <= 4096 && at >= 0x10000 && at <= 0x7FFFFFFFFFFFUL - (uint)size && ++reads <= 8192 && (bytes += size) <= 512 * 1024,
                "VIEWPORT_LOCAL_BUDGET", "視野讀取超過單組有界讀取預算");
            if (at >= memory.Image && at - memory.Image <= (ulong)memory.ImageSize - (uint)size) return memory.Required((uint)(at - memory.Image), size);
            if (!memory.Pointed(at, size, out var value)) throw new ProbeFailure("VIEWPORT_READ_FAILED", "視野資料未完整讀取", memory.LastNativeError);
            return value;
        }
        internal byte[] Track(ulong at, int size, bool source = false)
        {
            budget.Check(); var key = (at, size);
            if (source) sourceKeys.Add(key);
            if (proofs.TryGetValue(key, out var value)) return value;
            Need(proofs.Count < 4096, "VIEWPORT_PROOF_LIMIT", "視野證據超過 4096 筆");
            value = Read(at, size); proofs.Add(key, value); return value;
        }
        internal void MetaRange(ulong at, int size) => Need(at >= memory.Image && at - memory.Image <= (ulong)memory.ImageSize - (uint)size,
            "VIEWPORT_META_RANGE", "精確型別或函式超出目前載入映像");
        internal byte[] Meta(ulong at, int size)
        {
            budget.Check();MetaRange(at,size);var key=(at,size);metadataKeys.Add(key);
            if(proofs.TryGetValue(key,out var prior))return prior;
            Need(proofs.Count<4096,"VIEWPORT_PROOF_LIMIT","視野證據超過 4096 筆");
            var bytes=memory.MetaPointed(at,size);proofs.Add(key,bytes);return bytes;
        }
        private byte[] ReadProof(ulong at,int size)=>metadataKeys.Contains((at,size))?memory.MetaPointed(at,size):Read(at,size);
        internal TypeInfo? TypeAt(ulong obj)
        {
            Pointer(obj); ulong vt = U(Track(obj, 8));
            if (types.TryGetValue(vt, out var cached)) return cached;
            Need(types.Count < 128, "VIEWPORT_TYPE_LIMIT", "型別候選超過 128 種");
            int metadataCheckpoint=memory.MetadataCheckpoint();
            var candidateProofs=new List<(ulong Address,int Size)>();
            byte[] CandidateMeta(ulong at,int size)
            {
                bool existing=proofs.ContainsKey((at,size));var bytes=Meta(at,size);
                if(!existing)candidateProofs.Add((at,size));return bytes;
            }
            TypeInfo? result = null;
            if (vt >= memory.Image + 8 && vt - memory.Image < (ulong)memory.ImageSize - 24 && (vt & 7) == 0)
            {
                var prefix = CandidateMeta(vt - 8, 24); ulong colAddress = U(prefix);
                if (colAddress >= memory.Image && colAddress - memory.Image <= (ulong)memory.ImageSize - 24 && (colAddress & 3) == 0)
                {
                    var col = CandidateMeta(colAddress, 24); uint td = W(col, 12), hierarchy = W(col, 16);
                    if (W(col) == 1 && W(col, 4) == 0 && W(col, 8) == 0 && W(col, 20) == colAddress - memory.Image &&
                        td < (uint)memory.ImageSize - 160 && hierarchy < (uint)memory.ImageSize - 16)
                    {
                        var tdHeader = CandidateMeta(memory.Image + td, 16);
                        Pointer(U(tdHeader));
                        var hierarchyHeader = CandidateMeta(memory.Image + hierarchy, 16);
                        uint bases = W(hierarchyHeader, 8), baseArray = W(hierarchyHeader, 12);
                        Need(bases is > 0 and <= 128 && baseArray < (uint)memory.ImageSize - bases * 4,
                            "VIEWPORT_TYPE_HIERARCHY", "型別階層範圍無效");
                        var text = new List<byte>(); bool ended = false;
                        for (int offset = 0; offset < 128 && !ended; offset += 8)
                            foreach (byte b in CandidateMeta(memory.Image + td + 16 + (uint)offset, 8))
                            { if (b == 0) { ended = true; break; } text.Add(b); }
                        if (ended && text.Count is > 5 and < 128 && text.All(b => b is >= 33 and <= 126))
                        {
                            string name = Encoding.ASCII.GetString(text.ToArray());
                            bool delegated = name is ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@" or ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@";
                            bool earlySlot=memory.Executable(U(prefix, 8)) || memory.Executable(U(prefix, 16));
                            bool lateSlot=false;
                            if(!earlySlot&&RelocationControlSlots.IsLateSlotType(name)&&
                                memory.Profile.TypeBindings.TryGetValue(name,out var binding)&&
                                vt==memory.Image+binding.Vtable&&colAddress==memory.Image+binding.Col&&td==binding.Descriptor)
                            {
                                var slots=CandidateMeta(vt,32);
                                lateSlot=RelocationControlSlots.Valid(name,slots,memory.Image,(uint)memory.ImageSize,memory.Executable);
                            }
                            if (delegated || earlySlot || lateSlot) result = new(name, vt);
                        }
                    }
                }
            }
            if(result is null)
            {
                memory.RollbackMetadataCandidate(metadataCheckpoint);
                foreach(var key in candidateProofs){proofs.Remove(key);metadataKeys.Remove(key);}
            }
            types.Add(vt, result); return result;
        }
        internal void Exact(ulong obj, string name, bool source = false)
        {
            var type = TypeAt(obj);
            Need(type?.Name == name, "VIEWPORT_EXACT_TYPE", "目前物件精確 RTTI／primary COL 不符必要型別");
            Track(obj, 8, source);
        }
        internal ulong SceneGl()
        {
            director = U(Track(memory.Address(0x17E00D8), 8, true)); Exact(director, ".?AVDirector@cocos2d@@", true);
            scene = U(Track(director + 0x150, 8, true)); Exact(scene, ".?AVScene@cocos2d@@", true);
            var owner = Track(scene + 0x190, 16, true);
            Need(U(owner) == 0 && U(owner, 8) == director, "GROUP_SOURCE_CHANGED", "視野場景所屬不符");
            journal.RequireScene(director, scene);
            ulong gl = U(Track(director + 0x100, 8, true)); Exact(gl, ".?AVGLViewImpl@cocos2d@@", true);
            return gl;
        }
        internal List<Pair> Vector(ulong at, int capacityLimit, int takeLimit, bool source, out int total, out int inspected)
        {
            Need(takeLimit is >= 0 and <= 128, "VIEWPORT_VECTOR_LIMIT", "物件集合讀取上限無效");
            var header = Track(at, 24, source); ulong begin = U(header), end = U(header, 8), cap = U(header, 16);
            if (begin == 0 && end == 0 && cap == 0) { total = inspected = 0; return []; }
            Pointer(begin); Pointer(end); Pointer(cap);
            Need(begin <= end && end <= cap && (end - begin) % 16 == 0 && (cap - begin) % 16 == 0 && (cap - begin) / 16 <= (ulong)capacityLimit,
                "VIEWPORT_VECTOR_RANGE", "shared_ptr 集合範圍不符");
            total = checked((int)((end - begin) / 16)); int take = Math.Min(total, takeLimit);
            inspected = take;
            var result = new List<Pair>();
            if (take == 0) return result;
            var rows = Track(begin, take * 16, source);
            for (int i = 0; i < take; i++)
            {
                ulong obj = U(rows, i * 16), control = U(rows, i * 16 + 8);
                if (obj == 0) { Need(control == 0, "VIEWPORT_VECTOR_EMPTY", "空物件仍有控制來源"); continue; }
                Pointer(obj); Pointer(control); result.Add(new(obj, control));
            }
            return result;
        }
        internal void Close()
        {
            foreach (var proof in proofs)
            {
                bool same = ReadProof(proof.Key.Address, proof.Key.Size).AsSpan().SequenceEqual(proof.Value);
                // A metadata or source mismatch must never be reclassified as an
                // ordinary moving cursor/rectangle/collection value mismatch.
                string code = metadataKeys.Contains(proof.Key) ? "VIEWPORT_METADATA_CHANGED" :
                    sourceKeys.Contains(proof.Key) ? "GROUP_SOURCE_CHANGED" : "VIEWPORT_AB_CHANGED";
                Need(same, code, "視野欄位／所屬集合在採樣內改變，捨棄該組");
            }
            VerifySources(); memory.Verify(); journal.RequireScene(director, scene);
            journal.Write("VIEWPORT_METADATA_MODE",new{MetadataEvidence=memory.MetadataEvidence,DynamicSourcesAndValuesFresh=true});
            var previous = journal.SourceClosure;
            journal.SourceClosure = () => { previous?.Invoke(); VerifySources(); memory.Verify(); journal.RequireScene(director, scene); };
        }
        internal void VerifyRejectedSampleSource()
        {
            VerifySources(); memory.Verify(); journal.RequireScene(director, scene);
        }
        private void VerifySources()
        {
            foreach (var key in sourceKeys)
                Need(Read(key.Address, key.Size).AsSpan().SequenceEqual(proofs[key]),
                    "GROUP_SOURCE_CHANGED", "跨組視野根／場景／自身來源改變");
        }
    }

    private static bool Fatal(ProbeFailure error) => error.Code is "CONFIGURATION_CHANGED" or "TIME_LIMIT" or "READ_BYTE_LIMIT" or
        "GROUP_SOURCE_CHANGED" or "PROCESS_CHANGED" or "PROCESS_EXITED" or "PROCESS_PATH_CHANGED" or "IDENTITY_API_FAILED" or "PID_CHANGED";
    private static bool PureDynamicFailure(ProbeFailure error) => error.NativeError == 0 && error.Code is
        "VIEWPORT_AB_CHANGED" or "VIEWPORT_CURSOR_VALUE" or "VIEWPORT_FRAME_VALUE" or "VIEWPORT_CAMERA_VALUE" or "VIEWPORT_RECT_VALUE";
    private static ulong U(byte[] bytes, int offset = 0) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
    private static uint W(byte[] bytes, int offset = 0) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static float F(byte[] bytes, int offset = 0) => BitConverter.UInt32BitsToSingle(W(bytes, offset));
    private static RectangleData Rect(byte[] bytes, int offset = 0) => new(F(bytes, offset), F(bytes, offset + 4), F(bytes, offset + 8), F(bytes, offset + 12));
    private static bool Valid(RectangleData rectangle) => new[] { rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height }.All(float.IsFinite) &&
        Math.Abs(rectangle.X) <= 1_000_000 && Math.Abs(rectangle.Y) <= 1_000_000 && rectangle.Width is > 0 and <= 16384 && rectangle.Height is > 0 and <= 16384;
    private static void Pointer(ulong pointer) => Need(Probe.Pointer(pointer), "VIEWPORT_POINTER", "視野衍生指標無效");
    private static void Need(bool condition, string code, string detail) { if (!condition) throw new ProbeFailure(code, detail); }
}
