using System.Buffers.Binary;
using System.Text;

namespace MemoryHub.Reader;

/// <summary>Bounded, read-only discovery of module-relative anchors. It never scans heap regions.</summary>
internal static class RelocationScanner
{
    private const uint DirectorSlot = 0x17E00D8, ServiceSlot = 0x1642B60, CoordinateScale = 0x1251A10;
    private const int MaximumImageBytes = 128 * 1024 * 1024, MaximumGlobalObjects = 16384;
    private const string DirectorName = ".?AVDirector@cocos2d@@", SceneName = ".?AVScene@cocos2d@@";
    private const string RootName = ".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@";
    private const string HolderName = ".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@";
    private const string CharacterName = ".?AVCharacterDataSubsystem@lineage@@";

    internal static ReaderProfile Discover(ReadSession memory, LcBuildIdentity identity, ReadSelection selection,
        Action<RelocationProgress> progress, Action<HubLog> log, Budget budget, bool forceRootSearch = false)
    {
        var scan = new Scan(memory, identity, selection, progress, log, budget, forceRootSearch);
        return scan.Run();
    }

    private sealed record Binding(string Name, uint Descriptor, uint Col, uint Vtable, byte[] DescriptorBytes, byte[] ColBytes, byte[] VtableBytes,
        uint Hierarchy, byte[] HierarchyBytes, uint BaseArray, byte[] BaseArrayBytes);
    private enum ProofMode { Exact, SharedOwnershipAlive }
    private sealed record Proof(ulong At, byte[] Bytes, string Kind = "Source", ProofMode Mode = ProofMode.Exact);
    private sealed record RootMatch(uint Slot, ulong Object, ulong Related, IReadOnlyList<Binding> Bindings, IReadOnlyList<Proof> Proofs);
    private readonly record struct Covered(int Start, int End, bool Executable, bool Writable);

    private sealed class Snapshot(int size)
    {
        internal readonly byte[] Bytes = new byte[size];
        internal readonly List<Covered> Ranges = [];
        internal long Skipped;
        internal int QueryErrors, ReadErrors;
        internal void Add(int start, int count, bool executable, bool writable)
        {
            if (Ranges.Count > 0 && Ranges[^1].End == start && Ranges[^1].Executable == executable && Ranges[^1].Writable == writable)
                Ranges[^1] = Ranges[^1] with { End = start + count };
            else Ranges.Add(new(start, start + count, executable, writable));
        }
        private int Index(int at)
        {
            int lo = 0, hi = Ranges.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2; var r = Ranges[mid];
                if (at < r.Start) hi = mid - 1;
                else if (at >= r.End) lo = mid + 1;
                else return mid;
            }
            return -1;
        }
        internal bool Has(int at, int count)
        {
            if (at < 0 || count <= 0 || at > Bytes.Length - count) return false;
            int index = Index(at); if (index < 0) return false;
            int through = Ranges[index].End, end = at + count;
            while (through < end)
            {
                if (++index >= Ranges.Count || Ranges[index].Start != through) return false;
                through = Ranges[index].End;
            }
            return true;
        }
        internal bool NonExecutable(int at, int count)
        {
            int index = Index(at);
            return index >= 0 && !Ranges[index].Executable && at <= Ranges[index].End - count;
        }
    }

    private sealed class Scan(ReadSession memory, LcBuildIdentity identity, ReadSelection selection,
        Action<RelocationProgress> progress, Action<HubLog> log, Budget budget, bool forceRootSearch)
    {
        private readonly Dictionary<string, List<Binding>> bindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Binding> chosen = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, uint> mapped = [];
        private readonly List<Proof> sourceProofs = [];
        private Snapshot image = null!;
        private readonly Dictionary<uint,ReadOnlyBuffCallbackContract.Candidate> readonlyCallbacks=[];
        private readonly Dictionary<uint,ReadOnlyUiSubsystemContract.Candidate> readonlyUiSubsystems=[];
        private RootMatch? director, service;
        private int candidateObjectReads;

        internal ReaderProfile Run()
        {
            Need(memory.ImageSize is > 0 and <= MaximumImageBytes && identity.ImageSize == (uint)memory.ImageSize,
                "RELOCATION_IMAGE_LIMIT", "載入映像大小不符或超過 128 MiB 定位界限。");
            Event("RELOCATION_SCAN_MODE", "唯讀定位搜尋模式", new { DiagnosticForceRootSearch = forceRootSearch });
            Notify("Image", "讀取載入主映像的可讀頁面與覆蓋範圍", 5);
            image = Capture();
            Notify("Types", "重新連結精確 RTTI、COL 與 vtable", 30);
            LocateTypes();
            foreach (string name in new[] { DirectorName, SceneName, RootName, HolderName, CharacterName })
                Need(bindings.TryGetValue(name, out var found) && found.Count > 0, "RELOCATION_REQUIRED_TYPE", "必要型別沒有完整定位證據：" + name);
            Notify("Roots", "核對場景及角色服務根的型別與所屬關係", 60);
            if (!forceRootSearch)
            {
                director = TryRoot(DirectorSlot, true);
                service = TryRoot(ServiceSlot, false);
            }
            if (director is null || service is null)
            {
                var references = ReferenceGlobals();
                SearchGlobals(references, "ModuleRipRelativeCandidates");
            }
            if (forceRootSearch)
            {
                // Exercise both bounded fallback methods independently. Do not let a RIP match
                // suppress writable-global validation, or silently mix different root objects.
                var referencedDirector = director; var referencedService = service;
                director = null; service = null;
                var globals = WritableGlobals();
                SearchGlobals(globals, "ModuleWritableGlobals");
                Need(referencedDirector is null || director is null || referencedDirector.Object == director.Object && referencedDirector.Related == director.Related,
                    "RELOCATION_ROOT_AMBIGUOUS", "兩種有界搜尋找到不同的 Director／Scene 根關係。");
                Need(referencedService is null || service is null || referencedService.Object == service.Object && referencedService.Related == service.Related,
                    "RELOCATION_ROOT_AMBIGUOUS", "兩種有界搜尋找到不同的角色服務根關係。");
                if (referencedDirector is not null) sourceProofs.AddRange(referencedDirector.Proofs);
                if (referencedService is not null) sourceProofs.AddRange(referencedService.Proofs);
                director ??= referencedDirector; service ??= referencedService;
            }
            else if (director is null || service is null)
            {
                var globals = WritableGlobals();
                SearchGlobals(globals, "ModuleWritableGlobals");
            }
            Need(director is not null && service is not null, "RELOCATION_ROOT_NOT_FOUND", "有界主映像搜尋尚未找到必要的唯一型別根；沒有套用任何新位址。");
            ChooseRoots(director!, DirectorSlot); ChooseRoots(service!, ServiceSlot);
            foreach (var pair in bindings)
                if (!chosen.ContainsKey(pair.Key) && pair.Value.Count == 1) chosen.Add(pair.Key, pair.Value[0]);

            foreach (var spec in ProfileSymbols.Types)
            {
                if (!chosen.TryGetValue(spec.Name, out var binding)) continue;
                AddMapping(spec.Descriptor, binding.Descriptor); AddMapping(spec.Col, binding.Col); AddMapping(spec.Vtable, binding.Vtable);
            }
            var fieldEvidence=RelocationFieldPlan.Evaluate(chosen.Keys.ToHashSet(StringComparer.Ordinal),mapped.Keys.ToHashSet());
            var allowed=fieldEvidence.Where(f=>f.CanSample).Select(f=>f.Key).ToHashSet(StringComparer.Ordinal);
            bool sameBuild = string.Equals(identity.Sha256, ReaderProfile.Builtin.Sha256, StringComparison.OrdinalIgnoreCase);
            if (sameBuild && memory.InImage(CoordinateScale, 4) && U32(memory.Required(CoordinateScale, 4)) == 0x42400000)
                AddMapping(CoordinateScale, CoordinateScale);
            foreach(var field in fieldEvidence)Event("RELOCATION_FIELD_ANCHORS",field.Detail,field);
            Event("RELOCATION_SCOPE","定位範圍獨立於服務讀取開關，所有欄位逐項回報",new{AllFields=FieldCatalog.All.Count,SupportedFields=ReaderProfile.SupportedKeys.Length,CanSample=allowed.Count,SameBuild=sameBuild,
                DiscoveryFields=selection.EnabledKeys,CoordinateScaleEvidence="TypedCurrentAndNextCoordinate40_RequiresOwnerAndABClosure"});
            Notify("Closure", "重新核對所有選定型別與根關係", 75);
            foreach (var binding in chosen.Values) VerifyBinding(binding);
            Close(sourceProofs); memory.Verify(); budget.Check();
            string id = "relocated-" + identity.Sha256[..Math.Min(12, identity.Sha256.Length)] + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            Event("RELOCATION_ANCHORS_READY", "唯讀位址候選已閉合；仍須各欄位採樣驗證", new
            {
                ProfileId = id, identity, MappingCount = mapped.Count, MappedTypes = chosen.Keys.Order().ToArray(),
                DiagnosticForceRootSearch = forceRootSearch,
                MissingOrAmbiguousTypes = ProfileSymbols.Types.Select(t => t.Name).Distinct().Where(n => !chosen.ContainsKey(n)).Order().ToArray(),
                DirectorGlobalRva = Probe.Hex(director!.Slot), ServiceGlobalRva = Probe.Hex(service!.Slot),
                image.Skipped, image.QueryErrors, image.ReadErrors, CandidateObjectReads = candidateObjectReads,
                memory.RequestedBytes, memory.CompletedBytes, CoverageLimited = image.Skipped != 0,
                StructureLayoutCandidate = true, FieldValuesValidated = false, HeapRegionsScanned = false,
                MemoryWritten = false, GameplayInputSent = false
            });
            var typedBindings=chosen.ToDictionary(p=>p.Key,p=>new RelocationTypeBinding(p.Key,p.Value.Descriptor,p.Value.Col,p.Value.Vtable),StringComparer.Ordinal);
            return new ReaderProfile(id, identity, mapped, allowed.ToArray(),fieldEvidence,typedBindings);
        }

        private Snapshot Capture()
        {
            var snapshot = new Snapshot(memory.ImageSize); var buffer = new byte[4096]; int at = 0, reported = 0;
            try
            {
                while (at < memory.ImageSize)
                {
                    budget.Check();
                    if (!memory.Query((uint)at, out var region))
                    {
                        int skip = Math.Min(4096 - at % 4096, memory.ImageSize - at);
                        snapshot.Skipped += skip; snapshot.QueryErrors++; at += skip; continue;
                    }
                    ulong current = memory.Image + (uint)at;
                    Need(region.RegionSize > 0 && region.BaseAddress <= current && region.BaseAddress <= ulong.MaxValue - region.RegionSize && region.BaseAddress + region.RegionSize > current,
                        "RELOCATION_REGION_RANGE", "載入映像頁面查詢範圍不符。");
                    int remaining = (int)Math.Min((ulong)(memory.ImageSize - at), region.BaseAddress + region.RegionSize - current);
                    uint p = region.Protect & 0xFF;
                    bool readable = region.State == 0x1000 && (region.Protect & 0x101) == 0 && p is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
                    if (!readable) { snapshot.Skipped += remaining; at += remaining; continue; }
                    int count = Math.Min(buffer.Length, remaining);
                    if (memory.Read((uint)at, buffer, count))
                    {
                        Array.Copy(buffer, 0, snapshot.Bytes, at, count);
                        snapshot.Add(at, count, p is 0x20 or 0x40 or 0x80, p is 0x04 or 0x08 or 0x40 or 0x80);
                    }
                    else { snapshot.Skipped += count; snapshot.ReadErrors++; }
                    at += count;
                    if (at - reported >= 4 * 1024 * 1024)
                    { reported = at; Notify("Image", $"已檢查主映像 {at / 1048576} / {memory.ImageSize / 1048576} MiB", 5 + (int)(20L * at / memory.ImageSize)); }
                }
                return snapshot;
            }
            finally
            {
                Event("RELOCATION_IMAGE_COVERAGE", "主映像唯讀覆蓋紀錄", new
                {
                    ThroughRva = Probe.Hex((uint)at), snapshot.Skipped, snapshot.QueryErrors, snapshot.ReadErrors,
                    CompleteCoverage = at == memory.ImageSize && snapshot.Skipped == 0,
                    Ranges = snapshot.Ranges.Select(r => new { StartRva = Probe.Hex((uint)r.Start), EndRva = Probe.Hex((uint)r.End), r.Executable, r.Writable }).ToArray(),
                    memory.RequestedBytes, memory.CompletedBytes, RawDumpSaved = false
                });
            }
        }

        private void LocateTypes()
        {
            var descriptorNames = new Dictionary<uint, (string Name, byte[] Bytes)>();
            var requested = ProfileSymbols.Types.Select(t => t.Name).Distinct(StringComparer.Ordinal).ToArray();
            int completed = 0;
            foreach (string name in requested)
            {
                byte[] pattern = Encoding.ASCII.GetBytes(name + '\0'); int cursor = 0, hits = 0;
                while (cursor <= image.Bytes.Length - pattern.Length)
                {
                    budget.Check(); int relative = image.Bytes.AsSpan(cursor).IndexOf(pattern); if (relative < 0) break;
                    int at = cursor + relative; cursor = at + 1;
                    Need(++hits <= 16 && descriptorNames.Count < 512, "RELOCATION_TYPE_LIMIT", "精確型別候選超過有界數量。");
                    int td = at - 16;
                    if (td < 0 || (td & 7) != 0 || !image.Has(td, 16 + pattern.Length)) continue;
                    var bytes = ReadImage((uint)td, 16 + pattern.Length);
                    if (!Probe.Pointer(U64(bytes))) continue;
                    Need(bytes.AsSpan().SequenceEqual(image.Bytes.AsSpan(td, bytes.Length)), "RELOCATION_METADATA_CHANGED", "RTTI 名稱與描述在定位時已變更。");
                    descriptorNames[(uint)td] = (name, bytes);
                }
                if ((++completed & 7) == 0) Notify("Types", $"已搜尋 {completed} / {requested.Length} 個精確型別名稱", 30 + 10 * completed / requested.Length);
            }
            var cols = new Dictionary<uint, (uint Descriptor, byte[] Bytes, uint Hierarchy, byte[] HierarchyBytes, uint BaseArray, byte[] BaseArrayBytes)>();
            for (int at = 0; at <= image.Bytes.Length - 24; at += 4)
            {
                if ((at & 0xFFFF) == 0) budget.Check();
                if (U32(image.Bytes, at) != 1 || U32(image.Bytes, at + 4) != 0 || U32(image.Bytes, at + 8) != 0 || U32(image.Bytes, at + 20) != (uint)at) continue;
                uint descriptor = U32(image.Bytes, at + 12);
                if (!descriptorNames.ContainsKey(descriptor) || !image.Has(at, 24)) continue;
                uint hierarchy = U32(image.Bytes, at + 16);
                if (hierarchy > image.Bytes.Length - 16 || !image.Has((int)hierarchy, 16)) continue;
                uint bases = U32(image.Bytes, (int)hierarchy + 8), array = U32(image.Bytes, (int)hierarchy + 12);
                if (U32(image.Bytes, (int)hierarchy) != 0 || bases is 0 or > 256 || array > image.Bytes.Length - bases * 4 || !image.Has((int)array, (int)bases * 4)) continue;
                Need(cols.Count < 1024, "RELOCATION_COL_LIMIT", "主要 COL 候選超過有界數量。");
                var bytes = ReadImage((uint)at, 24); var hierarchyBytes = ReadImage(hierarchy, 16); var arrayBytes = ReadImage(array, (int)bases * 4);
                Need(bytes.AsSpan().SequenceEqual(image.Bytes.AsSpan(at, 24)) && hierarchyBytes.AsSpan().SequenceEqual(image.Bytes.AsSpan((int)hierarchy, 16)) &&
                    arrayBytes.AsSpan().SequenceEqual(image.Bytes.AsSpan((int)array, (int)bases * 4)),
                    "RELOCATION_METADATA_CHANGED", "RTTI 繼承關係在定位時已變更。");
                cols.Add((uint)at, (descriptor, bytes, hierarchy, hierarchyBytes, array, arrayBytes));
            }
            int tables = 0;
            var slotDiagnostics=new Dictionary<string,List<object>>(StringComparer.Ordinal);
            for (int at = 0; at <= image.Bytes.Length - 24; at += 8)
            {
                if ((at & 0xFFFF) == 0) budget.Check();
                ulong colPointer = U64(image.Bytes, at);
                if (colPointer < memory.Image || colPointer - memory.Image >= (ulong)memory.ImageSize || !cols.TryGetValue((uint)(colPointer - memory.Image), out var col) || !image.Has(at, 24)) continue;
                var descriptor = descriptorNames[col.Descriptor];
                bool leading = descriptor.Name is RootName or HolderName;
                ulong first = U64(image.Bytes, at + 8), second = U64(image.Bytes, at + 16);
                if(!slotDiagnostics.TryGetValue(descriptor.Name,out var diagnostics))slotDiagnostics.Add(descriptor.Name,diagnostics=[]);
                if(diagnostics.Count<16)
                {
                    bool fourCovered=image.Has(at,40);int available=fourCovered?4:2;
                    ulong[] slots=Enumerable.Range(0,available).Select(index=>U64(image.Bytes,at+8+index*8)).ToArray();
                    diagnostics.Add(new{DescriptorRva=Probe.Hex(col.Descriptor),ColRva=Probe.Hex(colPointer-memory.Image),VtableRva=Probe.Hex((uint)(at+8)),FourSlotsCovered=fourCovered,
                        Slots=slots.Select((pointer,index)=>new{Index=index,Pointer=Probe.Hex(pointer),Rva=pointer>=memory.Image&&pointer-memory.Image<(ulong)memory.ImageSize?Probe.Hex(pointer-memory.Image):null,Executable=memory.Executable(pointer)}).ToArray()});
                }
                int prefixLength=24;
                if(descriptor.Name==ReadOnlyBuffCallbackContract.Name)
                {
                    if(!image.Has(at,56))continue;
                    try
                    {
                        // Read-only RTTI/payload location, not a callable-function allowance.
                        var proof=ReadOnlyBuffCallbackContract.Inspect(
                            new(memory.Image,(uint)memory.ImageSize,identity.PeTimestamp),new((uint)(at+8),col.Descriptor,(uint)(colPointer-memory.Image)),
                            ReadAt,where=>ReadOnlyBuffCallbackContract.Query(memory,where),budget.Check);
                        proof.CloseLocal(ReadAt,where=>ReadOnlyBuffCallbackContract.Query(memory,where),budget.Check);
                        readonlyCallbacks[(uint)(at+8)]=proof;prefixLength=56;
                    }
                    catch(InvalidDataException error)
                    {Event("RELOCATION_BUFF_DATA_REJECTED",error.Message,new{VtableRva=Probe.Hex((uint)(at+8))});continue;}
                }
                else if(descriptor.Name is ReadOnlyUiSubsystemContract.Name or ReadOnlyUiSubsystemContract.PartyName)
                {
                    if(!image.Has(at,8+8*ReadOnlyUiSubsystemContract.SlotCount(descriptor.Name)))continue;
                    try
                    {
                        var proof=ReadOnlyUiSubsystemContract.Inspect(
                            new(memory.Image,(uint)memory.ImageSize,identity.PeTimestamp),new((uint)(at+8),col.Descriptor,(uint)(colPointer-memory.Image)),
                            ReadAt,where=>ReadOnlyUiSubsystemContract.Query(memory,where),budget.Check,descriptor.Name);
                        proof.CloseLocal(ReadAt,where=>ReadOnlyUiSubsystemContract.Query(memory,where),budget.Check);
                        readonlyUiSubsystems[(uint)(at+8)]=proof;prefixLength=8+8*ReadOnlyUiSubsystemContract.SlotCount(descriptor.Name);
                    }
                    catch(InvalidDataException error)
                    {Event("RELOCATION_UI_DATA_REJECTED",error.Message,new{VtableRva=Probe.Hex((uint)(at+8))});continue;}
                }
                else if (!leading && !memory.Executable(first) && !memory.Executable(second))
                {
                    if(!RelocationControlSlots.IsLateSlotType(descriptor.Name)||!image.Has(at,40)||
                        !RelocationControlSlots.Valid(descriptor.Name,image.Bytes.AsSpan(at+8,32),memory.Image,(uint)memory.ImageSize,memory.Executable))continue;
                    prefixLength=40;
                }
                Need(++tables <= 2048, "RELOCATION_VTABLE_LIMIT", "vtable 候選超過有界數量。");
                var prefix = ReadImage((uint)at, prefixLength);
                Need(prefix.AsSpan().SequenceEqual(image.Bytes.AsSpan(at, prefixLength)), "RELOCATION_METADATA_CHANGED", "vtable 關係在定位時已變更。");
                var binding = new Binding(descriptor.Name, col.Descriptor, (uint)(colPointer - memory.Image), (uint)(at + 8), descriptor.Bytes, col.Bytes, prefix,
                    col.Hierarchy, col.HierarchyBytes, col.BaseArray, col.BaseArrayBytes);
                VerifyBinding(binding);
                if (!bindings.TryGetValue(binding.Name, out var list)) bindings.Add(binding.Name, list = []);
                if (!list.Any(b => b.Descriptor == binding.Descriptor && b.Col == binding.Col && b.Vtable == binding.Vtable)) list.Add(binding);
            }
            foreach (string name in requested)
                Event("RELOCATION_TYPE_RESULT", "精確型別定位結果", new
                {
                    Name = name, Candidates = bindings.GetValueOrDefault(name, []).Select(b => new { DescriptorRva = Probe.Hex(b.Descriptor), ColRva = Probe.Hex(b.Col), VtableRva = Probe.Hex(b.Vtable) }).ToArray(),
                    DescriptorCandidates=descriptorNames.Where(p=>p.Value.Name==name).Select(p=>Probe.Hex(p.Key)).ToArray(),
                    ColCandidates=cols.Where(p=>descriptorNames[p.Value.Descriptor].Name==name).Select(p=>Probe.Hex(p.Key)).ToArray(),
                    PrimarySlotDiagnostics=slotDiagnostics.GetValueOrDefault(name,[]),
                    MissingOrAmbiguous = !bindings.TryGetValue(name, out var rows) || rows.Count != 1,
                    LeadingDelegateRequiresOwnerProof = name is RootName or HolderName,
                    ExternalExecutableSlotSupport = false, FullImageCoverage = image.Skipped == 0
                });
        }

        private Dictionary<ulong, List<uint>> ReferenceGlobals()
        {
            var globals = new Dictionary<ulong, List<uint>>();
            foreach (var range in image.Ranges.Where(r => r.Executable))
                for (int at = range.Start; at <= range.End - 7; at++)
                {
                    if ((at & 0xFFFF) == 0) budget.Check();
                    // These raw byte patterns only propose addresses; typed ownership proves acceptance.
                    if (image.Bytes[at] is not (0x48 or 0x4C) || image.Bytes[at + 1] is not (0x8B or 0x8D) || (image.Bytes[at + 2] & 0xC7) != 5) continue;
                    long target = (long)at + 7 + BinaryPrimitives.ReadInt32LittleEndian(image.Bytes.AsSpan(at + 3, 4));
                    if (target < 0 || target > image.Bytes.Length - 8 || (target & 7) != 0 || !image.NonExecutable((int)target, 8)) continue;
                    AddGlobal(globals, (uint)target);
                }
            return globals;
        }

        private Dictionary<ulong, List<uint>> WritableGlobals()
        {
            var globals = new Dictionary<ulong, List<uint>>();
            var examined = new HashSet<ulong>(); var typedRoots = new HashSet<ulong>();
            int unreadable = 0, rejectedType = 0; long ignoredNonRootAliases = 0; uint through = 0;
            try
            {
                foreach (var range in image.Ranges.Where(r => r.Writable && !r.Executable))
                    for (int at = (range.Start + 7) & ~7; at <= range.End - 8; at += 8)
                    {
                        if ((at & 0xFFFF) == 0) budget.Check();
                        through = (uint)at;
                        ulong value = U64(image.Bytes, at);
                        if (!GlobalObjectCandidate(value)) continue;
                        if (!examined.Contains(value))
                        {
                            Need(examined.Count < MaximumGlobalObjects, "RELOCATION_GLOBAL_LIMIT", "可寫模組槽超過 16384 個不同候選物件；停止定位，未跳過超界候選。");
                            examined.Add(value);
                            if (++candidateObjectReads > MaximumGlobalObjects * 2)
                                throw new ProbeFailure("RELOCATION_GLOBAL_LIMIT", "根物件核對達總讀取上限。");
                            if (!memory.Pointed(value, 8, out var first)) { unreadable++; continue; }
                            ulong vptr = U64(first);
                            if (Matches(DirectorName, vptr).Count == 0 && Matches(RootName, vptr).Count == 0)
                            { rejectedType++; continue; }
                            typedRoots.Add(value);
                        }
                        // Repeated non-root pointers are not root aliases. Only exact typed
                        // candidates retain all their slots, with the original 256-alias limit.
                        if (!typedRoots.Contains(value)) { ignoredNonRootAliases++; continue; }
                        AddGlobal(globals, (uint)at);
                    }
                return globals;
            }
            finally
            {
                Event("RELOCATION_WRITABLE_VTABLE_FILTER", "可寫模組槽先核對精確根型別，再保存根參照", new
                {
                    DiagnosticForceRootSearch = forceRootSearch, ThroughRva = Probe.Hex(through),
                    DistinctPointersExamined = examined.Count, TypedRootCandidates = typedRoots.Count,
                    Unreadable = unreadable, RejectedType = rejectedType, IgnoredNonRootAliases = ignoredNonRootAliases,
                    RetainedRootAliases = globals.Values.Sum(x => x.Count), MaximumDistinctObjects = MaximumGlobalObjects,
                    MaximumAliasesPerTypedRoot = 256, candidateObjectReads, HeapRegionsScanned = false
                });
            }
        }

        private bool GlobalObjectCandidate(ulong value) => Probe.Pointer(value) &&
            !(value >= memory.Image && value - memory.Image < (ulong)memory.ImageSize);

        private void AddGlobal(Dictionary<ulong, List<uint>> globals, uint slot)
        {
            ulong value = U64(image.Bytes, (int)slot);
            if (!GlobalObjectCandidate(value)) return;
            if (!globals.TryGetValue(value, out var slots))
            {
                Need(globals.Count < MaximumGlobalObjects, "RELOCATION_GLOBAL_LIMIT", "模組根候選超過 16384 個不同物件；停止定位，未取第一筆套用。");
                globals.Add(value, slots = []);
            }
            if (slots.Contains(slot)) return;
            Need(slots.Count < 256, "RELOCATION_GLOBAL_ALIAS_LIMIT", "同一根物件的模組參照超過 256 筆。");
            slots.Add(slot);
        }

        private void SearchGlobals(Dictionary<ulong, List<uint>> globals, string method)
        {
            bool findDirector = director is null, findService = service is null;
            var directors = new Dictionary<ulong, RootMatch>(); var services = new Dictionary<ulong, RootMatch>();
            int checkedCount = 0;
            foreach (var entry in globals)
            {
                budget.Check();
                if (++candidateObjectReads > MaximumGlobalObjects * 2) throw new ProbeFailure("RELOCATION_GLOBAL_LIMIT", "根物件核對達總讀取上限。");
                if (!memory.Pointed(entry.Key, 8, out var first)) continue;
                ulong vptr = U64(first);
                bool isDirector = findDirector && Matches(DirectorName, vptr).Count > 0;
                bool isService = findService && Matches(RootName, vptr).Count > 0;
                if (!isDirector && !isService) continue;
                foreach (uint slot in entry.Value)
                {
                    var result = TryRoot(slot, isDirector); if (result is null) continue;
                    var target = isDirector ? directors : services;
                    if (target.TryGetValue(result.Object, out var prior))
                    {
                        Need(prior.Related == result.Related, "RELOCATION_SOURCE_CHANGED", "同一根物件的所屬來源在搜尋中變更。");
                        // Aliases of the same proven object are not distinct root objects.
                        if (result.Slot < prior.Slot) target[result.Object] = result;
                    }
                    else target.Add(result.Object, result);
                }
                if ((++checkedCount & 31) == 0) Notify("Roots", "核對精確型別根候選與所屬集合", 65);
            }
            Event("RELOCATION_GLOBAL_SEARCH", "有界模組根候選核對完成", new
            {
                Method = method, DistinctObjectCandidates = globals.Count, GlobalSlots = globals.Values.Sum(x => x.Count),
                DiagnosticForceRootSearch = forceRootSearch,
                TypedDirectorObjects = directors.Count, TypedServiceObjects = services.Count, candidateObjectReads,
                CoverageLimited = image.Skipped != 0, WholeHeapScanned = false, GlobalSingletonClaimed = false
            });
            Need(directors.Count <= 1 && services.Count <= 1, "RELOCATION_ROOT_AMBIGUOUS", "找到多個不同的有效型別根，不能自動選取。");
            if (findDirector && directors.Count == 1) director = directors.Values.Single();
            if (findService && services.Count == 1) service = services.Values.Single();
        }

        private RootMatch? TryRoot(uint slot, bool isDirector)
        {
            if (!memory.InImage(slot, 8)) return null;
            var proofs = new List<Proof>(); var used = new List<Binding>();
            byte[] Track(ulong at, int count, string kind = "Source", ProofMode mode = ProofMode.Exact)
            {
                var bytes = ReadAt(at, count); proofs.Add(new(at, bytes, kind, mode)); return bytes;
            }
            Binding Exact(ulong obj, string name)
            {
                Need(Probe.Pointer(obj), "RELOCATION_ROOT_SHAPE", "根候選中的物件指標不符。");
                var matches = Matches(name, U64(Track(obj, 8, "ExactObjectVtable:" + name)));
                Need(matches.Count == 1, "RELOCATION_ROOT_SHAPE", "根候選缺少唯一精確型別關係：" + name);
                VerifyBinding(matches[0]); used.Add(matches[0]); return matches[0];
            }
            try
            {
                ulong obj = U64(Track(memory.Image + slot, 8, isDirector ? "DirectorGlobal" : "ServiceGlobal"));
                if (isDirector)
                {
                    Exact(obj, DirectorName);
                    ulong scene = U64(Track(obj + 0x150, 8, "CurrentScenePointer")); Exact(scene, SceneName);
                    var owner = Track(scene + 0x190, 16, "SceneParentAndDirector");
                    Need(U64(owner) == 0 && U64(owner, 8) == obj, "RELOCATION_ROOT_SHAPE", "Scene parent／Director 回指不符。");
                    Close(proofs); return new(slot, obj, scene, used, proofs);
                }
                Exact(obj, RootName);
                Need(Track(obj + 0xD8, 1, "ServiceReady")[0] != 0, "RELOCATION_NOT_READY", "角色服務尚未就緒。");
                ulong holder = U64(Track(obj + 0xD0, 8, "SubsystemHolderPointer")); Exact(holder, HolderName);
                var vector = Track(holder + 0x180, 24, "SubsystemVectorHeader");
                ulong begin = U64(vector), end = U64(vector, 8), capacity = U64(vector, 16);
                Need(Probe.Pointer(begin) && Probe.Pointer(end) && Probe.Pointer(capacity) && begin <= end && end <= capacity &&
                    (end - begin) % 16 == 0 && (capacity - begin) % 16 == 0 && (capacity - begin) / 16 <= 128,
                    "RELOCATION_ROOT_SHAPE", "角色服務集合的界限不符。");
                Need(end > begin, "RELOCATION_NOT_READY", "角色服務集合尚未就緒。");
                byte[] rows = Track(begin, checked((int)(end - begin)), "SubsystemSharedPointerEntries");
                ulong character = 0; var objects = new HashSet<ulong>();
                for (int at = 0; at < rows.Length; at += 16)
                {
                    ulong member = U64(rows, at), control = U64(rows, at + 8);
                    Need(Probe.Pointer(member) && Probe.Pointer(control) && objects.Add(member), "RELOCATION_ROOT_SHAPE", "服務集合含無效或重複的 shared_ptr。");
                    // Strong/weak counts are lifetime conditions, not stable source identity.
                    // The complete (object, control) vector and object vtables remain exact proofs.
                    var counts = Track(control + 8, 8, "SubsystemSharedOwnershipCounts", ProofMode.SharedOwnershipAlive);
                    Need(OwnershipAlive(counts), "RELOCATION_ROOT_SHAPE", "服務 shared_ptr 所屬生命週期不符。");
                    ulong memberVptr = U64(Track(member, 8, "SubsystemObjectVtable"));
                    if (Matches(CharacterName, memberVptr).Count == 0) continue;
                    Need(character == 0, "RELOCATION_ROOT_SHAPE", "CharacterDataSubsystem 在所屬集合內不唯一。");
                    Exact(member, CharacterName); character = member;
                }
                Need(character != 0, "RELOCATION_NOT_READY", "所屬集合缺少 CharacterDataSubsystem。");
                ulong ownId = U64(Track(character + 0x6D8, 8, "OwnCharacterId"));
                Need(ownId is not (0 or ulong.MaxValue), "RELOCATION_NOT_READY", "目前自身角色來源尚未就緒。");
                Close(proofs); return new(slot, obj, holder, used, proofs);
            }
            catch (ProbeFailure failure) when (failure.Code is "RELOCATION_ROOT_SHAPE" or "RELOCATION_NOT_READY" or "RELOCATION_READ_FAILED" or "REQUIRED_READ_FAILED" or "POINTED_RANGE")
            {
                return null;
            }
        }

        private List<Binding> Matches(string name, ulong vptr) => bindings.TryGetValue(name, out var rows)
            ? rows.Where(b => memory.Image + b.Vtable == vptr).ToList() : [];

        private void ChooseRoots(RootMatch root, uint oldSlot)
        {
            AddMapping(oldSlot, root.Slot); sourceProofs.AddRange(root.Proofs);
            foreach (var binding in root.Bindings)
            {
                if (chosen.TryGetValue(binding.Name, out var prior))
                    Need(prior.Descriptor == binding.Descriptor && prior.Col == binding.Col && prior.Vtable == binding.Vtable,
                        "RELOCATION_ROOT_AMBIGUOUS", "不同必要根關係使用了互相衝突的型別定位。");
                else chosen.Add(binding.Name, binding);
            }
        }

        private void AddMapping(uint prior, uint current)
        {
            if (prior == 0) return;
            Need(current > 0 && memory.InImage(current, 1), "RELOCATION_MAPPING_RANGE", "候選 RVA 不在目前載入主映像內。");
            if (mapped.TryGetValue(prior, out uint existing))
                Need(existing == current, "RELOCATION_MAPPING_CONFLICT", "同一版型符號出現不一致的定位結果。");
            else mapped.Add(prior, current);
        }

        private byte[] ReadImage(uint rva, int count)
        {
            Need(memory.InImage(rva, count), "RELOCATION_METADATA_RANGE", "定位中繼資料超出載入主映像。");
            return ReadAt(memory.Image + rva, count);
        }
        private byte[] ReadAt(ulong at, int count)
        {
            budget.Check();
            if (!memory.Pointed(at, count, out var bytes)) throw new ProbeFailure("RELOCATION_READ_FAILED", "定位中的必要有界資料未完整讀取。", memory.LastNativeError);
            return bytes;
        }
        private void VerifyBinding(Binding binding)
        {
            if(binding.Name is ReadOnlyUiSubsystemContract.Name or ReadOnlyUiSubsystemContract.PartyName)
            {
                Need(readonlyUiSubsystems.TryGetValue(binding.Vtable,out var ui),"RELOCATION_UI_DATA_MISSING","UI 子系統唯讀型別證據未完成。");
                ui!.CloseLocal(ReadAt,where=>ReadOnlyUiSubsystemContract.Query(memory,where),budget.Check);
            }
            if(binding.Name==ReadOnlyBuffCallbackContract.Name)
            {
                Need(readonlyCallbacks.TryGetValue(binding.Vtable,out var callback),"RELOCATION_BUFF_DATA_MISSING","BUFF 唯讀型別證據未完成。");
                callback!.CloseLocal(ReadAt,where=>ReadOnlyBuffCallbackContract.Query(memory,where),budget.Check);
            }
            Need(ReadImage(binding.Descriptor, binding.DescriptorBytes.Length).AsSpan().SequenceEqual(binding.DescriptorBytes) &&
                ReadImage(binding.Col, 24).AsSpan().SequenceEqual(binding.ColBytes) &&
                ReadImage(binding.Vtable - 8, binding.VtableBytes.Length).AsSpan().SequenceEqual(binding.VtableBytes) &&
                ReadImage(binding.Hierarchy, binding.HierarchyBytes.Length).AsSpan().SequenceEqual(binding.HierarchyBytes) &&
                ReadImage(binding.BaseArray, binding.BaseArrayBytes.Length).AsSpan().SequenceEqual(binding.BaseArrayBytes),
                "RELOCATION_METADATA_CHANGED", "精確型別關係在最終閉合時已改變。");
        }
        private void Close(IEnumerable<Proof> proofs)
        {
            foreach (var proof in proofs)
            {
                byte[] current;
                try { current = ReadAt(proof.At, proof.Bytes.Length); }
                catch (ProbeFailure failure) when (failure.Code == "RELOCATION_READ_FAILED")
                {
                    Event("RELOCATION_PROOF_MISMATCH", "來源閉合資料未完整讀取", new
                    {
                        Address = Probe.Hex(proof.At), Count = proof.Bytes.Length, proof.Kind, Mode = proof.Mode.ToString(),
                        BeforeHex = Convert.ToHexString(proof.Bytes.AsSpan(0, Math.Min(32, proof.Bytes.Length))), AfterHex = (string?)null,
                        PreviewOffset = 0, PreviewTruncated = proof.Bytes.Length > 32, failure.Code, failure.NativeError
                    });
                    throw;
                }
                bool identical = current.AsSpan().SequenceEqual(proof.Bytes);
                bool valid = proof.Mode == ProofMode.SharedOwnershipAlive ? OwnershipAlive(proof.Bytes) && OwnershipAlive(current) : identical;
                if (!valid || !identical)
                {
                    int difference = -1;
                    for (int i = 0; i < proof.Bytes.Length; i++)
                        if (proof.Bytes[i] != current[i]) { difference = i; break; }
                    int start = Math.Max(0, difference - 8), count = Math.Min(32, proof.Bytes.Length - start);
                    Event(valid ? "RELOCATION_PROOF_REVALIDATED" : "RELOCATION_PROOF_MISMATCH",
                        valid ? "動態參照計數已重新核對活性；來源與所屬指標仍需完整一致" : "來源閉合核對失配", new
                        {
                            Address = Probe.Hex(proof.At), Count = proof.Bytes.Length, proof.Kind, Mode = proof.Mode.ToString(),
                            FirstDifferenceOffset = difference, PreviewOffset = start,
                            BeforeHex = Convert.ToHexString(proof.Bytes.AsSpan(start, count)), AfterHex = Convert.ToHexString(current.AsSpan(start, count)),
                            PreviewTruncated = proof.Bytes.Length > count,
                            StrongAndWeakMustRemainBetween = proof.Mode == ProofMode.SharedOwnershipAlive ? "1..1000000" : null,
                            Accepted = valid
                        });
                }
                Need(valid, "RELOCATION_SOURCE_CHANGED", "場景或角色來源在定位期間變更，請重新定位。");
            }
        }
        private static bool OwnershipAlive(byte[] counts) => counts.Length == 8 &&
            U32(counts) is > 0 and <= 1000000 && U32(counts, 4) is > 0 and <= 1000000;
        private void Notify(string stage, string message, int percent)
        { budget.Check(); progress(new(stage, message, percent)); }
        private void Event(string phase, string message, object detail)
        { try { log(new(DateTimeOffset.UtcNow, phase, message, detail)); } catch { /* Logging failures cannot replace the bounded reader outcome. */ } }
    }

    private static uint U32(byte[] b, int at = 0) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    private static ulong U64(byte[] b, int at = 0) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
    private static void Need(bool condition, string code, string message)
    { if (!condition) throw new ProbeFailure(code, message); }
}
