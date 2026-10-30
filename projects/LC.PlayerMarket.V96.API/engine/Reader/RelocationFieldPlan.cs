using System.Text.Json;

namespace MemoryHub.Reader;

internal sealed record RelocationAnchorEvidence(string Key, bool Supported, bool CanSample,
    string[] RequiredTypes, string[] MissingTypes, uint[] MissingSymbols, string[] RuntimeTypes, string[] MissingRuntimeTypes, string Layout, string Detail);

/// <summary>Every public field has its own dependency report; no build-wide field exclusion.</summary>
internal static class RelocationFieldPlan
{
    private const string Root=".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@";
    private const string Holder=".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@";
    private static string C(string name)=>".?AV"+name+"@cocos2d@@";
    private static string L(string name)=>".?AV"+name+"@lineage@@";
    private static readonly string[] Common=[C("Director"),C("Scene"),Root,Holder,L("CharacterDataSubsystem")];
    private static readonly string[] Hud=[C("Node"),".?AVText@ui@cocos2d@@",C("Label")];
    private static readonly string[] Self=[L("ActorSubsystem"),L("MyPlayer")];
    private static readonly string[] Coordinates=[..Self,L("TransformComponent")];
    private static readonly string[] Names=[..Self,L("DescriptionComponent")];
    private static readonly string[] Inventory=[L("InventorySubsystem"),".?AV?$_Ref_count_obj2@VInventorySubsystem@lineage@@@std@@",".?AV?$_Ref_count_obj2@VItemEntity@lineage@@@std@@"];
    private static readonly string[] Party=[..Names,L("PartySubsystem"),".?AV?$_Ref_count_obj2@VParty@lineage@@@std@@",".?AV?$_Ref_count_obj2@VPartyPlayer@lineage@@@std@@"];

    internal static ReadSelection AllFields()=>new();
    internal static RelocationAnchorEvidence[] Evaluate(IReadOnlySet<string> locatedTypes,IReadOnlySet<uint> mappedSymbols)
        =>FieldCatalog.All.Select(field=>Evaluate(field.Key,locatedTypes,mappedSymbols)).ToArray();

    private static RelocationAnchorEvidence Evaluate(string key,IReadOnlySet<string> locatedTypes,IReadOnlySet<uint> mappedSymbols)
    {
        bool supported=ReaderProfile.SupportedKeys.Contains(key,StringComparer.Ordinal);
        if(!supported)return new(key,false,false,[],[],[],[],[],"Unsupported",
            "尚無可驗證讀取版型；不可人工放行。");
        string[] local=key switch
        {
            "hp" or "mp" or "level" or "experiencePercent" or "weightPercent" or "foodPercent" or "armorClass" or "magicResistancePercent" or "alignment" or "mapName"=>Hud,
            "selfId"=>Self,
            "position"=>Coordinates,
            "characterName"=>Names,
            "buffIds"=>ProfileSymbols.Types.Where(t=>t.Name.Contains("BuffSlot",StringComparison.Ordinal)||t.Name==".?AVLayout@ui@cocos2d@@"||t.Name==C("Sprite")||t.Name==".?AVImageView@ui@cocos2d@@"||t.Name==C("Node")).Select(t=>t.Name).ToArray(),
            "hotbarPage" or "hotbarFocus" or "hotbarSlots" or "atsEnabled"=>[L("HudSlotDataSubsystem")],
            "inventory" or "equippedItems" or "weapon"=>Inventory,
            "safeZone" or "mapTile" or "mapNeighborhood"=>[..Coordinates,L("MapSubsystem")],
            "party"=>Party,
            "actors" or "nearbyUnknown"=>Self,
            "actorNames"=>Names,
            "actorPositions"=>Coordinates,
            "nearbyMonsters"=>[..Names,L("TransformComponent"),L("Monster")],
            "nearbyNpcs"=>[..Names,L("TransformComponent"),L("InteractiveNPC")],
            "nearbyPlayers"=>[..Names,L("TransformComponent"),L("Player")],
            "playerShopStates"=>[..Self,L("Player"),L("ActionComponent"),L("ShopAction"),L("BreathAction"),L("MovingAction")],
            "actorViewport"=>[..Self,C("GLViewImpl")],
            "personalShopListing"=>[..Hud,".?AVLayout@ui@cocos2d@@",".?AVListView@ui@cocos2d@@",
                ".?AVUISubsystem@ui@lineage@@",L("PlayerShopSubsystem"),".?AVPlayerShopLayoutPCImpl@ui@lineage@@",
                ".?AV?$_Ref_count_obj2@VItemEntity@lineage@@@std@@"],
            "cursorPosition" or "cursorInputState"=>[C("GLViewImpl")],
            _=>throw new InvalidDataException("新增可讀欄位尚未宣告定位依賴："+key)
        };
        // Optional collection elements are checked by their actual live object
        // TD/COL, owner and membership. Absent members need no guessed vtable.
        string[] unconditional=key switch
        {
            "inventory" or "equippedItems" or "weapon"=>[],
            "party"=>[L("PartySubsystem")],
            "actors" or "actorNames" or "actorPositions" or "nearbyMonsters" or "nearbyNpcs" or "nearbyPlayers" or "playerShopStates" or "nearbyUnknown"=>[L("ActorSubsystem")],
            "actorViewport" or "cursorPosition" or "cursorInputState"=>[],
            "personalShopListing"=>[".?AVUISubsystem@ui@lineage@@",L("PlayerShopSubsystem")],
            "buffIds"=>[C("Node"),".?AVBuffSlotView@ui@lineage@@"],
            _=>local.Where(n=>ProfileSymbols.Types.Any(t=>t.Name==n&&(t.Descriptor!=0||t.Col!=0||t.Vtable!=0))).ToArray()
        };
        string[] required=Common.Concat(unconditional).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] runtime=local.Except(required,StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] missingRuntime=runtime.Where(t=>!locatedTypes.Contains(t)).ToArray();
        string[] missing=required.Where(t=>!locatedTypes.Contains(t)).ToArray();
        uint[] symbols=ProfileSymbols.Types.Where(t=>required.Contains(t.Name,StringComparer.Ordinal))
            .SelectMany(t=>new[]{t.Descriptor,t.Col,t.Vtable}).Where(r=>r!=0).Concat([0x17E00D8u,0x1642B60u])
            .Distinct().Where(r=>!mappedSymbols.Contains(r)).Order().ToArray();
        bool ready=missing.Length==0&&symbols.Length==0;
        return new(key,true,ready,required,missing,symbols,runtime,missingRuntime,"CurrentTypedLayoutAndOwnershipMustClose",
            ready?"必要精確型別與根映射已建立；尚須本欄來源、結構及值的多輪閉合樣本。":
                "部分定位：缺少唯一完整型別／根映射；不沿用舊位址。"+
                (missing.Length==0?"":" 型別："+string.Join("、",missing))+
                (symbols.Length==0?"":" 映射："+string.Join("、",symbols.Select(s=>$"0x{s:X}"))));
    }

    // Complete here means a source-closed field/collection. Optional unknown
    // leaves are preserved in the payload; they do not invalidate membership.
    internal static bool CompleteValue(FieldReading reading)
    {
        if(reading.State!="Observed"||reading.Value is null||reading.SampledAt is null)return false;
        var node=reading.Value.Value;
        if(node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)return false;
        return node.ValueKind!=JsonValueKind.Object||!node.EnumerateObject().Any(p=>
            (p.Name.Equals("complete",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("completeCollection",StringComparison.OrdinalIgnoreCase))&&
            p.Value.ValueKind!=JsonValueKind.True);
    }

    // An empty or all-unknown player collection proves membership only. It
    // cannot confirm the newly added action layout for a game build.
    internal static bool ConfirmationValue(string key,FieldReading reading)
    {
        if(!CompleteValue(reading))return false;
        if(key!="playerShopStates")return true;
        var node=reading.Value!.Value;
        if(node.ValueKind!=JsonValueKind.Object||!node.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array)return false;
        foreach(var item in items.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("actionType",out var type)||type.ValueKind!=JsonValueKind.String||
                !item.TryGetProperty("shopState",out var state)||state.ValueKind!=JsonValueKind.String||
                !item.TryGetProperty("shopStateEvidence",out var evidence)||evidence.ValueKind!=JsonValueKind.String||
                !item.TryGetProperty("shopStateSampledAt",out var sampledAt)||sampledAt.ValueKind!=JsonValueKind.String||
                !sampledAt.TryGetDateTimeOffset(out _))continue;
            string classified=PlayerShopStatePolicy.ClassifyExactAction(type.GetString());
            if(classified!="Unknown"&&state.GetString()==classified&&
                evidence.GetString()=="ExactPlayerId_ComponentOwner_DirectAction_PrimaryRtti_ActionBaseHierarchy_ABClosed")return true;
        }
        return false;
    }

    internal static int PartialLeaves(JsonElement node,int depth=0)
    {
        if(depth>12)return 0;
        if(node.ValueKind==JsonValueKind.Array)return node.EnumerateArray().Sum(n=>PartialLeaves(n,depth+1));
        if(node.ValueKind!=JsonValueKind.Object)return 0;
        int count=0;
        foreach(var property in node.EnumerateObject())
        {
            if(property.Name.EndsWith("state",StringComparison.OrdinalIgnoreCase)&&property.Value.ValueKind==JsonValueKind.String&&
                property.Value.GetString() is "Unknown" or "UnknownItemValues" or "Partial" or "Absent")count++;
            count+=PartialLeaves(property.Value,depth+1);
        }
        return count;
    }

    internal static RelocationField Report(string key,FieldReading last,int successfulSamples,RelocationAnchorEvidence anchors)
    {
        bool complete=ConfirmationValue(key,last),confirm=anchors.CanSample&&successfulSamples>=2&&complete;
        int partialLeaves=last.Value is {} value?PartialLeaves(value):0;
        string detail=!anchors.Supported?"尚未支援："+anchors.Detail:
            !anchors.CanSample?anchors.Detail:
            key=="playerShopStates"&&!complete&&last.State=="Observed"?"玩家集合已閉合，但目前沒有已驗 Action 分類樣本；空集合／全部未知不能確認擺攤狀態版型。":
            confirm?$"完整（集合／型別來源）：{successfulSamples} 輪通過本欄結構與來源核對；仍須人工比對語義與畫面。"+(partialLeaves>0?$" 欄內 {partialLeaves} 個缺值／部分狀態保留原樣，確認不會補成假值。":""):
            last.State=="Observed"?$"部分：{successfulSamples} 輪取得讀值，但含未完成項目或尚不足 2 輪；不可確認。 "+last.Detail:
            "不可用：必要型別已定位，但本欄尚未取得有效閉合樣本。 "+last.Detail;
        return new(key,last,confirm,detail);
    }
}
