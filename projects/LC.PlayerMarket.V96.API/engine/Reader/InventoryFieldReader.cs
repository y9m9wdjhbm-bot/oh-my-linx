using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemoryHub.Reader;

/// <summary>Bounded current inventory. Display-derived candidates are never relabelled as raw equipment flags.</summary>
internal static class InventoryFieldReader
{
    private const string InventoryType=".?AVInventorySubsystem@lineage@@";
    private const string InventoryControl=".?AV?$_Ref_count_obj2@VInventorySubsystem@lineage@@@std@@";
    private const string ItemControl=".?AV?$_Ref_count_obj2@VItemEntity@lineage@@@std@@";
    private static readonly UTF8Encoding Utf8=new(false,true);
    private static readonly Regex Token=new(@"\$([0-9]+)",RegexOptions.CultureInvariant);
    private static readonly Regex QuantitySuffix=new(@"\(([0-9]+(?:,[0-9]{3})*)\)$",RegexOptions.CultureInvariant);
    private static readonly Regex Enhancement=new(@"^([+-][0-9]+)(?=\s)",RegexOptions.CultureInvariant);
    private static readonly Regex FormatParameter=new(@"%%|%(?:[0-9]+\$)?[-+ #0']*(?:\*|[0-9]+)?(?:\.(?:\*|[0-9]+))?(?:hh|ll|I32|I64|[hljztL])?[diuoxXfFeEgGaAcspn]",RegexOptions.CultureInvariant);
    private static readonly Lazy<DescriptionTable> Descriptions=new(LoadDescriptions);

    internal sealed record ItemRow(int Index,string Uid,uint EntityIdCandidate,uint DefinitionIdCandidate,
        string? QuantityCandidateExact,string? RawDisplayText,string? LocalizedDisplayCandidate,
        bool DisplayLocalizationComplete,bool? QuantityMatchesDisplayed,string[] DisplayStateMarkers,
        int? EnhancementFromDisplay,int? EquipmentSlot,string? AuxiliaryTagExact,bool AuxiliaryDefinitionMatches,
        DateTimeOffset SampledAt,string State,string[] UnknownReasons)
    {
        public bool EquipmentRawFlagDecoded=>false;
        public bool QuantityFieldWidthVerified=>false;
        public bool DynamicQuantityChangeVerified=>false;
        // Compatibility property refers to the live archive, which is not rehashed here.
        public bool CurrentResourceTableVerified=>false;
        public bool DescriptionTableAuthenticated=>Descriptions.Value.Tokens.Count==18052;
        public string DescriptionSourceSha256=>"9885B00E204B822B0AD818D5D3FCAF8ECE59B4C736213CC3FDEEF0EF263175B8";
        public string DescriptionSourceLcSha256=>"79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9";
        public string LocalizationEvidence=>Descriptions.Value.Evidence;
    }
    private sealed record DescriptionTable(IReadOnlyDictionary<string,string> Tokens,string Evidence);
    private sealed record ItemValue(DateTimeOffset At,ulong Quantity,string Raw);
    private sealed class ItemValueFailure(string code,string message):Exception(message)
    {internal string Code=>code;}

    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        if(!new[]{"inventory","equippedItems","weapon","equipmentSlots"}.Any(selection.Enabled))return;
        // ReadProfile binds and closes this character fence before invoking any field
        // reader, then closes it again before publication. Rewalking every nearby
        // actor here would make unrelated actor arrivals invalidate the inventory.
        if(journal.SelfId is null or 0 or ulong.MaxValue||journal.SourceClosure is null)
            throw new ProbeFailure("GROUP_SOURCE_CHANGED","背包讀取前缺少已綁定的自身 ID 或來源閉合程序，不能單獨沿 Inventory 鏈發布。");
        var at=DateTimeOffset.UtcNow;var g=new CurrentTypedGraph(memory,journal,budget,"INVENTORY_FIELD");
        ulong subsystem=g.Subsystem(InventoryType);
        g.Need(subsystem>=0x10010,"OWNER_RANGE","Inventory 所屬控制物件範圍不符。");
        ulong subsystemControl=subsystem-16;g.Control(subsystemControl);g.Exact(subsystemControl,InventoryControl);
        var header=g.Track(subsystem+0x3D8,24);
        int objectCount=Count(g,header,16,256);
        var pairs=g.Vector(subsystem+0x3D8,256);
        g.Need(pairs.Count==objectCount&&pairs.Select(p=>p.Object).Distinct().Count()==objectCount,
            "PRIMARY_COVERAGE","背包主集合含空項目或重複物件，不能輸出截斷／合併清單。");
        ulong[] auxiliaryUids=ScalarVector(g,subsystem+0x4C8),tags=ScalarVector(g,subsystem+0x4E0);
        g.Need(auxiliaryUids.Length==tags.Length&&auxiliaryUids.Distinct().Count()==auxiliaryUids.Length,
            "AUXILIARY_RELATION","輔助 UID／tag 集合異長或 UID 重複，不能配對。");
        var tagsByUid=auxiliaryUids.Select((uid,index)=>(uid,tag:tags[index])).ToDictionary(p=>p.uid,p=>p.tag);
        var rows=new List<ItemRow>();var seen=new HashSet<ulong>();int leafReads=0,leafBytes=0;
        foreach(var pair in pairs)
        {
            budget.Check();g.Control(pair.Control);var type=g.Exact(pair.Control,ItemControl);
            g.Need(pair.Object==pair.Control+16,"ITEM_OWNER","ItemEntity payload 與精確 control 所屬關係不符。");
            uint entityId=CurrentTypedGraph.U32(g.Track(pair.Object+8,4));
            uint definitionId=CurrentTypedGraph.U32(g.Track(pair.Object+0x10,4));
            ulong uid=CurrentTypedGraph.U64(g.Track(pair.Object+0x18,8));
            g.Need(uid is not(0 or ulong.MaxValue)&&seen.Add(uid),"UID","背包本體 UID 無效或重複。");
            bool hasTag=tagsByUid.TryGetValue(uid,out ulong tag);
            g.Need(!hasTag||(uint)tag==definitionId,"DEFINITION_RELATION","本體 definition 候選與按 UID 查得 tag 低位不符。");
            ItemValue? value=null;var problems=new List<string>();
            for(int attempt=0;attempt<2;attempt++)
            {
                try {value=ReadValue(pair.Object);break;}
                catch(ItemValueFailure error)
                {
                    if(attempt==0&&error.Code=="ITEM_VALUE_CHANGED")continue;
                    problems.Add(error.Code+": "+error.Message);break;
                }
            }
            string? localized=null;bool complete=false;bool? quantityMatches=null;string[] markers=[];int? enhancement=null;
            if(value is not null)
            {
                (localized,complete)=LocalizeForBuild(memory.Profile.Sha256,value.Raw);
                if(!complete)problems.Add("DISPLAY_LOCALIZATION_PARTIAL: 未解析 token／參數保留原文。");
                var suffix=QuantitySuffix.Match(value.Raw);
                if(suffix.Success)
                {
                    quantityMatches=ulong.TryParse(suffix.Groups[1].Value.Replace(",",""),NumberStyles.None,CultureInfo.InvariantCulture,out ulong shown)&&shown==value.Quantity;
                    if(quantityMatches==false)problems.Add("QUANTITY_DISPLAY_MISMATCH: 數量候選與顯示括號不符。");
                }
                markers=new[]{("9","揮舞"),("117","穿戴")}.Where(m=>value.Raw.Contains("($"+m.Item1+")",StringComparison.Ordinal)).Select(m=>m.Item2).ToArray();
                var prefix=Enhancement.Match(value.Raw);
                if(prefix.Success&&int.TryParse(prefix.Groups[1].Value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int n))enhancement=n;
            }
            if(!hasTag)problems.Add("AUXILIARY_UID_MISSING: 本體 UID 未出現在輔助表，保留本體資料但不推測 tag。");
            rows.Add(new(rows.Count,uid.ToString(CultureInfo.InvariantCulture),entityId,definitionId,
                value?.Quantity.ToString(CultureInfo.InvariantCulture),value?.Raw,localized,complete,quantityMatches,markers,enhancement,null,
                hasTag?tag.ToString(CultureInfo.InvariantCulture):null,hasTag,value?.At??DateTimeOffset.UtcNow,
                value is null?"UnknownItemValues":"ObservedCandidate",problems.ToArray()));
            journal.Write("INVENTORY_ITEM_SAMPLE",new{Preliminary=true,CollectionClosed=false,Index=rows.Count-1,Object=Probe.Hex(pair.Object),Control=Probe.Hex(pair.Control),
                VtableRva=Probe.Hex(type.Vtable-memory.Image),DescriptorRva=Probe.Hex(type.Descriptor),ColRva=Probe.Hex(type.Col),Row=rows[^1]});
        }
        g.Close();g.AttachSourceClosure();
        var items=rows.ToArray();var equipped=items.Where(r=>r.DisplayStateMarkers.Length>0).ToArray();
        string[] missingAuxiliary=auxiliaryUids.Where(uid=>!seen.Contains(uid)).Select(v=>v.ToString(CultureInfo.InvariantCulture)).ToArray();
        bool allTextRead=items.All(r=>r.RawDisplayText is not null);
        journal.Write("INVENTORY_CURRENT_SAMPLE",new{StartedAt=at,Subsystem=Probe.Hex(subsystem),PrimaryCount=objectCount,
            AuxiliaryCount=auxiliaryUids.Length,AuxiliaryUidsWithoutPrimary=missingAuxiliary,
            DynamicValuesObserved=items.Count(r=>r.RawDisplayText is not null),DisplayLocalizationComplete=items.Count(r=>r.DisplayLocalizationComplete),
            DisplayEquipmentCandidates=equipped.Length,LeafReads=leafReads,LeafBytes=leafBytes,
            CompleteCollection=true,SourceClosed=true,AtomicSnapshot=false,UserInventoryVerified=false,CurrentResourceTableVerified=false,
            MetadataEvidence=memory.MetadataEvidence});
        journal.Set("inventory",items,at,"CurrentTypedItemCollectionWithPerItemUnknown_FieldsRemainCandidates");
        if(allTextRead)
        {
            journal.Set("equippedItems",equipped,at,"InventoryDisplayMarkersOnly_RawEquipmentFlagUnverified");
            var weapons=equipped.Where(r=>r.DisplayStateMarkers.Contains("揮舞",StringComparer.Ordinal)).ToArray();
            if(weapons.Length==1)journal.Set("weapon",weapons[0],weapons[0].SampledAt,"InventoryDisplayWieldingMarkerOnly_WeaponSlotUnverified");
            else journal.Unknown("weapon",weapons.Length==0?"當次完整背包文字沒有『揮舞』標記，可能未裝備；不代表讀取失敗，原始武器欄語義仍未確認":"當次背包有多個『揮舞』標記，不能選定唯一武器");
        }
        else
        {
            journal.Unknown("equippedItems","部分道具文字未穩定取得，不能把缺少標記當成未穿戴；詳見 inventory 逐筆原因");
            journal.Unknown("weapon","部分道具文字未穩定取得，不能判定唯一武器；詳見 inventory 逐筆原因");
        }
        journal.Unsupported("equipmentSlots","已讀背包的穿戴／揮舞文字，尚未證明独立裝備欄位或部位槽编号");

        byte[] Leaf(ulong address,int count)
        {
            budget.Check();g.Need(count is >0 and <=513&&leafReads++<4096&&(leafBytes+=count)<=256*1024,"LEAF_BUDGET","背包逐項資料讀取超過有界預算。");
            if(!memory.Pointed(address,count,out var bytes))throw new ItemValueFailure("ITEM_LEAF_READ","道具即時數量／文字未完整讀取，Win32="+memory.LastNativeError);
            return bytes;
        }
        ItemValue ReadValue(ulong obj)
        {
            var sampleAt=DateTimeOffset.UtcNow;
            byte[] quantityA=Leaf(obj+0x28,8),descriptorA=Leaf(obj+0x58,32);
            ulong length=CurrentTypedGraph.U64(descriptorA,16),capacity=CurrentTypedGraph.U64(descriptorA,24);
            if(length is 0 or >512||capacity<length||capacity>4096||capacity<15||capacity==15&&length>15)
                throw new ItemValueFailure("ITEM_TEXT_LAYOUT","當次 display 不是已知有界 SSO／heap 文字布局。");
            bool sso=capacity==15;ulong textAt=sso?obj+0x58:CurrentTypedGraph.U64(descriptorA);
            if(textAt<0x10000||textAt>0x7FFFFFFFFFFFUL-length-1)throw new ItemValueFailure("ITEM_TEXT_POINTER","display 文字指標超界。");
            byte[] textA=sso?descriptorA.AsSpan(0,checked((int)length+1)).ToArray():Leaf(textAt,checked((int)length+1));
            if(textA[^1]!=0||textA.AsSpan(0,textA.Length-1).Contains((byte)0))throw new ItemValueFailure("ITEM_TEXT_TERMINATOR","display 文字終止符或內部 NUL 不符。");
            string decoded;
            try {decoded=Utf8.GetString(textA,0,textA.Length-1);}
            catch(DecoderFallbackException){throw new ItemValueFailure("ITEM_TEXT_UTF8","display 不是完整 UTF-8。");}
            if(string.IsNullOrWhiteSpace(decoded)||decoded.Any(char.IsControl))throw new ItemValueFailure("ITEM_TEXT_FORMAT","display 文字為空白或含控制字元。");
            byte[] quantityB=Leaf(obj+0x28,8),descriptorB=Leaf(obj+0x58,32);
            byte[] textB=sso?descriptorB.AsSpan(0,textA.Length).ToArray():Leaf(textAt,textA.Length);
            byte[] descriptorC=Leaf(obj+0x58,32);
            if(!quantityA.AsSpan().SequenceEqual(quantityB)||!descriptorA.AsSpan().SequenceEqual(descriptorB)||
                !descriptorA.AsSpan().SequenceEqual(descriptorC)||!textA.AsSpan().SequenceEqual(textB))
                throw new ItemValueFailure("ITEM_VALUE_CHANGED","單筆數量／文字在短讀期間改變；僅丟棄該筆即時欄位。");
            return new(sampleAt,CurrentTypedGraph.U64(quantityB),decoded);
        }
    }
    private static int Count(CurrentTypedGraph g,byte[] h,int stride,int maximum)
    {
        ulong begin=CurrentTypedGraph.U64(h),end=CurrentTypedGraph.U64(h,8),capacity=CurrentTypedGraph.U64(h,16);
        if(begin==0&&end==0&&capacity==0)return 0;
        g.Pointer(begin);g.Pointer(end);g.Pointer(capacity);
        g.Need(begin<=end&&end<=capacity&&(end-begin)%(uint)stride==0&&(capacity-begin)%(uint)stride==0&&(capacity-begin)/(uint)stride<=(uint)maximum,
            "VECTOR_BOUNDS","背包集合界線／stride／容量不符合有界布局。");
        return checked((int)((end-begin)/(uint)stride));
    }
    private static ulong[] ScalarVector(CurrentTypedGraph g,ulong at)
    {
        byte[] header=g.Track(at,24);int count=Count(g,header,8,512);if(count==0)return [];
        byte[] entries=g.Track(CurrentTypedGraph.U64(header),count*8);
        return Enumerable.Range(0,count).Select(i=>CurrentTypedGraph.U64(entries,i*8)).ToArray();
    }
    internal static (string Text,bool Complete) LocalizeForBuild(string sourceSha256,string raw)
        => sourceSha256 == "79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9" ? Localize(raw) : (raw,false);
    internal static (string Text,bool Complete) Localize(string raw)
    {
        bool complete=true;var table=Descriptions.Value;
        string value=Token.Replace(raw,match=>
        {
            if(!table.Tokens.TryGetValue(match.Groups[1].Value,out var text)||string.IsNullOrEmpty(text)||Regex.IsMatch(text,@"^desc[0-9]+$",RegexOptions.CultureInvariant))
            {complete=false;return match.Value;}
            return text;
        });
        if(Token.IsMatch(value)||FormatParameter.Matches(value).Any(m=>m.Value!="%%"))complete=false;
        return(value,complete);
    }
    private static DescriptionTable LoadDescriptions()
    {
        // Authenticated current template TW description-text-id table. Runtime archive is not rehashed per sample; provenance is explicit. Missing/tampered resources preserve raw text.
        try
        {
            var assembly=typeof(InventoryFieldReader).Assembly;
            string? name=assembly.GetManifestResourceNames().SingleOrDefault(n=>n.EndsWith("inventory-desc-tw-79D1-v2.json",StringComparison.Ordinal));
            if(name is null)return new(new Dictionary<string,string>(),"HistoricalDescriptionResourceNotEmbedded");
            using var input=assembly.GetManifestResourceStream(name)!;using var stream=new MemoryStream();input.CopyTo(stream);
            byte[] data=stream.ToArray();if(data.Length>2*1024*1024)return new(new Dictionary<string,string>(),"DescriptionResourceOverBound");
            if(Convert.ToHexString(SHA256.HashData(data))!="E583F8E0E98F8B586B182A1397B93FE0EB43C7257191B08FDCEB21888E8AF327")
                return new(new Dictionary<string,string>(),"DescriptionResourceFingerprintMismatch");
            using var document=JsonDocument.Parse(data);var root=document.RootElement;
            if(root.GetProperty("SourceSha256").GetString()!="9885B00E204B822B0AD818D5D3FCAF8ECE59B4C736213CC3FDEEF0EF263175B8")
                return new(new Dictionary<string,string>(),"DescriptionSourceFingerprintMismatch");
            if(root.GetProperty("Schema").GetInt32()!=2 || root.GetProperty("SourceLcSha256").GetString()!="79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9"
                || root.GetProperty("IdDomain").GetString()!="description-text-id" || root.GetProperty("Locale").GetString()!="TW"
                || !root.GetProperty("CurrentResourceVerified").GetBoolean() || root.GetProperty("RuntimeResourceRehashed").GetBoolean())
                return new(new Dictionary<string,string>(),"DescriptionResourceDomainOrSourceMismatch");
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var item in root.GetProperty("Tokens").EnumerateObject())
            {
                if(!uint.TryParse(item.Name,NumberStyles.None,CultureInfo.InvariantCulture,out uint id)||id.ToString(CultureInfo.InvariantCulture)!=item.Name||item.Value.ValueKind!=JsonValueKind.String||!result.TryAdd(item.Name,item.Value.GetString()!))
                    return new(new Dictionary<string,string>(),"DescriptionResourceInvalidIds");
            }
            if(result.Count!=root.GetProperty("Count").GetInt32()||result.Count>100000)return new(new Dictionary<string,string>(),"DescriptionResourceCountMismatch");
            return new(result,"AuthenticatedDescTw_20261021_79D1_DescriptionTextId_TW_RuntimeArchiveNotRehashed");
        }
        catch(Exception error)when(error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {return new(new Dictionary<string,string>(),"DescriptionResourceInvalid_"+error.GetType().Name);}
    }
}
