using System.Buffers.Binary;

namespace MemoryHub.Reader;

/// <summary>
/// Read-only extensions of V5's current typed map/GLView graphs. These fields
/// expose bounded raw observations, not pathfinding, click events or world mapping.
/// </summary>
internal static class MapCursorExtraReader
{
    internal static void Capture(ReadSession memory, Journal journal, Budget budget, ReadSelection selection)
    {
        if (selection.Enabled("mapTile") || selection.Enabled("mapNeighborhood")) CaptureMap(memory, journal, budget, selection);
        if (selection.Enabled("cursorInputState")) CaptureCursor(memory, journal, budget);
    }

    private static void CaptureMap(ReadSession memory, Journal journal, Budget budget, ReadSelection selection)
    {
        var at = DateTimeOffset.UtcNow;
        var graph = new CurrentTypedGraph(memory, journal, budget, "MAP_EXTRA");
        ulong map = graph.Subsystem(".?AVMapSubsystem@lineage@@", 0x11ABCB8, 0x1643E28);
        ulong mapData = graph.PointerAt(map + 0x6E0);
        var own = graph.Self();
        ulong transform = graph.Component(own.Actor, ".?AVTransformComponent@lineage@@");
        byte[] raw = graph.Track(transform + 0x38, 80);
        var current = Coordinate.Read(raw.AsSpan(0, 40));
        var next = Coordinate.Read(raw.AsSpan(40, 40));
        var coordinateEvidence=CoordinateEncodingEvidence.Capture(CoordinateEncodingSource.Current(memory,own.Id,own.Actor,transform),raw);
        var reader = new TileGraph(graph, mapData);
        Tile? foot = selection.Enabled("mapTile") ? reader.Read(0, 0, current) : null;
        List<Tile>? neighborhood = null;
        if (selection.Enabled("mapNeighborhood"))
        {
            neighborhood = [];
            for (int dv = -1; dv <= 1; dv++)
                for (int du = -2; du <= 2; du++)
                    neighborhood.Add(reader.Read(du, dv, current.AddSpace4(du, dv)));
        }
        coordinateEvidence.Verify(CoordinateEncodingSource.Current(memory,own.Id,own.Actor,transform),graph.Track(transform+0x38,80));
        graph.Close();
        graph.AttachSourceClosure();
        if (foot is not null)
        {
            if (foot.Flags is null) journal.Unknown("mapTile", "目前位置的 chunk 尚未載入；不能當作障礙或可通行。 ");
            else journal.Set("mapTile", new
            {
                SelfId = own.Id, Current = current, Next = next,
                CurrentEqualsNext = current.X == next.X && current.Y == next.Y,
                Tile = foot, MapId = (uint?)null, ChunkKeyIsMapId = false,
                CollisionClassificationVerified = false, AtomicSnapshot = false
            }, at, "TypedMapCurrentCoordinateAndRawTileStableAB_NotCollisionClassification");
        }
        if (neighborhood is not null) journal.Set("mapNeighborhood", new
        {
            SelfId = own.Id, Center = current, RadiusWorld = 1,
            CoordinateSpace = "Space4HalfGridU_WorldY", Count = neighborhood.Count,
            LoadedCount = neighborhood.Count(t => t.Flags.HasValue), Tiles = neighborhood,
            MapId = (uint?)null, ChunkKeyIsMapId = false,
            CollisionClassificationVerified = false, ActorOccupancyIncluded = false,
            AtomicSnapshot = false
        }, at, "TypedMapBounded15RawTilesStableAB_NotWalkabilityOrDynamicOccupancy");
    }

    private static void CaptureCursor(ReadSession memory, Journal journal, Budget budget)
    {
        var at = DateTimeOffset.UtcNow;
        var graph = new CurrentTypedGraph(memory, journal, budget, "CURSOR_EXTRA");
        ulong director = graph.PointerAt(memory.Address(0x17E00D8), true);
        graph.Exact(director, ".?AVDirector@cocos2d@@", 0x121AC40, 0x176A2A0);
        ulong gl = graph.PointerAt(director + 0x100, true);
        graph.Exact(gl, ".?AVGLViewImpl@cocos2d@@");
        byte[] viewport = graph.Track(gl + 0x20, 16);
        byte[] scales = graph.Track(gl + 0x50, 8);
        byte[] flags = graph.Track(gl + 0x60, 8);
        byte[] zoom = graph.Track(gl + 0x68, 4);
        byte[] input = graph.Track(gl + 0xB0, 20);
        float F(byte[] bytes, int offset = 0)
        {
            float value = BitConverter.Int32BitsToSingle(CurrentTypedGraph.I32(bytes, offset));
            graph.Need(float.IsFinite(value) && Math.Abs(value) <= 1_000_000, "FLOAT", "GLView float 候選非有限值或超出上限。 ");
            return value;
        }
        var rectangle = new { X = F(viewport), Y = F(viewport, 4), Width = F(viewport, 8), Height = F(viewport, 12) };
        var scale = new { X = F(scales), Y = F(scales, 4) };
        var cursor = new { X = F(input), Y = F(input, 4) };
        var saved = new { X = F(input, 8), Y = F(input, 12) };
        float zoomCandidate = F(zoom);
        graph.Need(rectangle.Width is > 0 and <= 65536 && rectangle.Height is > 0 and <= 65536 &&
            scale.X is > 0 and <= 64 && scale.Y is > 0 and <= 64 && zoomCandidate is > 0 and <= 64,
            "VIEW", "GLView 視窗／倍率候選不在預期有限範圍。 ");
        graph.Close();
        graph.AttachSourceClosure();
        journal.Set("cursorInputState", new
        {
            CoordinateSpace = "GLViewRawFloat2", Cursor = cursor,
            SavedPointCandidate = saved, SavedPointEventTime = (DateTimeOffset?)null,
            RawFlag60 = flags[0], RawFlags60To67Hex = Convert.ToHexString(flags),
            RawStateC0 = CurrentTypedGraph.U32(input, 16),
            RawViewport = rectangle, RawScale = scale, ZoomCandidate = zoomCandidate,
            CursorMode = (string?)null, IsClickEvent = false,
            SavedPointIsFreshClickVerified = false, GroundClickVerified = false,
            PhysicalPixelMappingVerified = false, GameWorldMappingVerified = false,
            AtomicSnapshot = false
        }, at, "TypedDirectorGLViewRawInputStableAB_NoEventOrCursorModeSemantics");
    }

    internal sealed record Coordinate(int ChunkX, int ChunkY, int X, int Y, int U, int V)
    {
        public uint ChunkKey => unchecked(((uint)ChunkX << 16) | (uint)ChunkY);
        public int LocalX => checked((0x7F00 - ChunkX) * 128 + U);
        public int LocalY => checked((0x7E00 - ChunkY) * 64 + V);
        internal static Coordinate Read(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 40) throw new ProbeFailure("MAP_EXTRA_COORDINATE", "Coordinate40 長度不符。 ");
            int x = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            int y = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
            if (x is < 0 or > 65535 || y is < 0 or > 65535)
                throw new ProbeFailure("MAP_EXTRA_COORDINATE", "Coordinate40 世界座標範圍不符。 ");
            float px = (x + y - 65535) * 48, py = (x - y) * 24;
            int[] expected = [x / 64 + 0x7E00, y / 64 + 0x7E00, x, y, x * 2 - 0x8000, y,
                BitConverter.SingleToInt32Bits(px), BitConverter.SingleToInt32Bits(py),
                BitConverter.SingleToInt32Bits(px - 48f), BitConverter.SingleToInt32Bits(py + 0f)];
            for (int i = 0; i < expected.Length; i++)
                if (BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]) != expected[i])
                    throw new ProbeFailure("MAP_EXTRA_COORDINATE", "Coordinate40 的 chunk／XY／UV／像素編碼不一致。 ");
            return new(expected[0], expected[1], x, y, expected[4], expected[5]);
        }
        internal Coordinate AddSpace4(int du, int dv)
        {
            int u = checked(U + du), v = checked(V + dv);
            int x = checked(((u - 0x8000) >> 1) + 0x8000);
            if (x is < 0 or > 65535 || v is < 0 or > 65535)
                throw new ProbeFailure("MAP_EXTRA_NEIGHBOR_RANGE", "近鄰超出可確認世界座標範圍，不截斷成可用地圖。 ");
            return new(x / 64 + 0x7E00, v / 64 + 0x7E00, x, v, u, v);
        }
    }

    internal sealed record Tile(int Space4DeltaU, int Space4DeltaV, Coordinate Coordinate,
        string Source, string? Raw8Hex, ushort? Flags, bool? RawBit1, bool? RawBit4000);

    private sealed class TileGraph(CurrentTypedGraph g, ulong mapData)
    {
        private readonly Dictionary<uint, ulong> chunks = [];
        private ulong Chunk(uint target)
        {
            if (chunks.TryGetValue(target, out ulong result)) return result;
            ulong head = g.PointerAt(mapData + 8);
            byte[] sentinel = g.Track(head, 56);
            g.Need(sentinel[0x19] == 1, "CHUNK_SENTINEL", "地圖 chunk sentinel 不符。 ");
            ulong node = CurrentTypedGraph.U64(sentinel, 8), parent = head;
            long low = -1, high = (long)uint.MaxValue + 1;
            var visited = new HashSet<ulong>();
            for (int depth = 0; depth < 64; depth++)
            {
                g.Pointer(node);
                g.Need(visited.Add(node), "CHUNK_CYCLE", "chunk 搜尋有循環。 ");
                if (node == head) return chunks[target] = 0;
                byte[] n = g.Track(node, 56);
                uint key = CurrentTypedGraph.U32(n, 0x20);
                g.Need(n[0x19] == 0 && n[0x18] <= 1 && CurrentTypedGraph.U64(n, 8) == parent && key > low && key < high,
                    "CHUNK_NODE", "chunk 樹節點／排序不符。 ");
                if (target == key)
                {
                    ulong chunk = CurrentTypedGraph.U64(n, 0x28);
                    g.Pointer(chunk); g.Control(CurrentTypedGraph.U64(n, 0x30));
                    return chunks[target] = chunk;
                }
                parent = node;
                if (target < key) { high = key; node = CurrentTypedGraph.U64(n); }
                else { low = key; node = CurrentTypedGraph.U64(n, 0x10); }
            }
            throw new ProbeFailure("MAP_EXTRA_CHUNK_DEPTH", "chunk 搜尋超過 64 節點。 ");
        }
        internal Tile Read(int du, int dv, Coordinate coordinate)
        {
            ulong chunk = Chunk(coordinate.ChunkKey);
            if (chunk == 0) return new(du, dv, coordinate, "ChunkMissing", null, null, null, null);
            int localX = coordinate.LocalX, localY = coordinate.LocalY;
            g.Need(localX is >= 0 and < 128 && localY is >= 0 and < 64, "LOCAL_RANGE", "地圖局部座標超出 128×64。 ");
            ulong head = g.PointerAt(chunk + 0x18);
            byte[] sentinel = g.Track(head, 48);
            g.Need(sentinel[0x19] == 1, "OVERRIDE_SENTINEL", "地磚覆寫 sentinel 不符。 ");
            ulong node = CurrentTypedGraph.U64(sentinel, 8), parent = head;
            var visited = new HashSet<ulong>();
            (int X, int Y)? lower = null, upper = null;
            for (int depth = 0; depth < 64; depth++)
            {
                g.Pointer(node);
                g.Need(visited.Add(node), "OVERRIDE_CYCLE", "地磚覆寫搜尋有循環。 ");
                if (node == head) return Base(chunk, du, dv, coordinate);
                byte[] n = g.Track(node, 48);
                var key = (X: CurrentTypedGraph.I32(n, 0x1C), Y: CurrentTypedGraph.I32(n, 0x20));
                g.Need(n[0x19] == 0 && n[0x18] <= 1 && CurrentTypedGraph.U64(n, 8) == parent &&
                    (lower is null || Compare(key, lower.Value) > 0) && (upper is null || Compare(key, upper.Value) < 0),
                    "OVERRIDE_NODE", "地磚覆寫樹節點／排序不符。 ");
                int comparison = Compare((localX, localY), key);
                if (comparison == 0) return Make(du, dv, coordinate, "Override", n.AsSpan(0x24, 8));
                parent = node;
                if (comparison < 0) { upper = key; node = CurrentTypedGraph.U64(n); }
                else { lower = key; node = CurrentTypedGraph.U64(n, 0x10); }
            }
            throw new ProbeFailure("MAP_EXTRA_OVERRIDE_DEPTH", "地磚覆寫搜尋超過 64 節點。 ");
        }
        private Tile Base(ulong chunk, int du, int dv, Coordinate coordinate)
        {
            byte[] rows = g.Track(chunk, 24);
            ulong begin = CurrentTypedGraph.U64(rows), end = CurrentTypedGraph.U64(rows, 8), cap = CurrentTypedGraph.U64(rows, 16);
            g.Pointer(begin); g.Pointer(end); g.Pointer(cap);
            g.Need(begin <= end && end <= cap && (end - begin) % 24 == 0 && (cap - begin) % 24 == 0 &&
                (end - begin) / 24 == 64 && (cap - begin) / 24 <= 128, "ROWS", "地磚列陣列不符合 64 列。 ");
            byte[] row = g.Track(begin + (ulong)coordinate.LocalY * 24, 24);
            ulong tiles = CurrentTypedGraph.U64(row), tileEnd = CurrentTypedGraph.U64(row, 8), tileCap = CurrentTypedGraph.U64(row, 16);
            g.Pointer(tiles); g.Pointer(tileEnd); g.Pointer(tileCap);
            g.Need(tiles <= tileEnd && tileEnd <= tileCap && (tileEnd - tiles) % 8 == 0 && (tileCap - tiles) % 8 == 0 &&
                (tileEnd - tiles) / 8 == 128 && (tileCap - tiles) / 8 <= 256, "COLUMNS", "地磚欄陣列不符合 128 欄。 ");
            return Make(du, dv, coordinate, "Base", g.Track(tiles + (ulong)coordinate.LocalX * 8, 8));
        }
        private static Tile Make(int du, int dv, Coordinate coordinate, string source, ReadOnlySpan<byte> raw)
        {
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
            return new(du, dv, coordinate, source, Convert.ToHexString(raw), flags, (flags & 1) != 0, (flags & 0x4000) != 0);
        }
        private static int Compare((int X, int Y) a, (int X, int Y) b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y);
    }
}
