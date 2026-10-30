namespace MemoryHub.Reader;

// These are semantic anchor keys from the retained V2 baseline, never current
// process RVAs. A zero member has no fixed baseline anchor and is not mapped.
internal sealed record TypeAnchorSpec(string Name,uint Descriptor,uint Col,uint Vtable);

internal static class ProfileSymbols
{
    internal static readonly TypeAnchorSpec[] Types=
    [
        new(".?AVDirector@cocos2d@@",0x176A2A0,0x13456E8,0x121AC40),
        new(".?AVScene@cocos2d@@",0x1660718,0x1348E48,0x121D980),
        new(".?AVNode@cocos2d@@",0x1649050,0x1345870,0x121B1D0),
        new(".?AVText@ui@cocos2d@@",0x1660DC8,0x134C1D8,0x1223A30),
        new(".?AVLabel@cocos2d@@",0x1660F88,0x134ABE8,0x1220C90),
        new(".?AVLayout@ui@cocos2d@@",0x1660EB0,0x134CBC8,0x1227950),
        new(".?AVSprite@cocos2d@@",0x1660F60,0x134C250,0x12241E0),
        new(".?AVImageView@ui@cocos2d@@",0x1660DF0,0x134E198,0x1229A80),
        new(".?AVBuffSlotView@ui@lineage@@",0x173E3A8,0x1321460,0x11FE948),
        new(".?AV?$_Func_impl_no_alloc@V<lambda_1>@?1??Init@BuffSlot@ui@lineage@@AEAAXXZ@XPEAVRef@cocos2d@@_NPEAVEventMouse@7@@std@@",0x1732020,0x1316F08,0x11F62C0),
        new(".?AV?$_Ref_count_obj2@VBuffSlot@ui@lineage@@@std@@",0x173E240,0x13213C0,0x11FE838),
        new(".?AVOnAppBackgroundStateChangedDelegate@GameInstance@lineage@@",0x16AEE30,0x1263C10,0x11672A0),
        new(".?AVOnSubsystemAddedDelegate@SubsystemHolder@lineage@@",0x16AEF50,0x1263CF0,0),
        new(".?AVHudSlotDataSubsystem@lineage@@",0x16AE830,0x1263760,0x11674B0),
        new(".?AVCharacterDataSubsystem@lineage@@",0x1643CF0,0x12663F8,0x1169730),
        new(".?AVActorSubsystem@lineage@@",0x1643C28,0,0x1162EB8),
        new(".?AVMapSubsystem@lineage@@",0x1643E28,0,0x11ABCB8),
        new(".?AVPartySubsystem@lineage@@",0x16447A0,0,0x11AC548),
        new(".?AVMyPlayer@lineage@@",0x1644900,0,0),
        new(".?AVDescriptionComponent@lineage@@",0x1643F70,0,0x11657B0),
        new(".?AV?$_Ref_count_obj2@VParty@lineage@@@std@@",0x1704768,0x12BA4F8,0x11AC4E0),
        new(".?AV?$_Ref_count_obj2@VPartyPlayer@lineage@@@std@@",0x17045F0,0x12B9EA8,0),
        new(".?AV?$_Ref_count_obj2@VItemEntity@lineage@@@std@@",0x17007B0,0x12B6B28,0x11AA3D0),
        new(".?AVInventorySubsystem@lineage@@",0,0,0),
        new(".?AV?$_Ref_count_obj2@VInventorySubsystem@lineage@@@std@@",0,0,0),
        new(".?AVTransformComponent@lineage@@",0,0,0),
        new(".?AVGLViewImpl@cocos2d@@",0,0,0),
        new(".?AVPlayer@lineage@@",0,0,0),
        new(".?AVActionComponent@lineage@@",0,0,0),
        new(".?AVShopAction@lineage@@",0,0,0),
        new(".?AVBreathAction@lineage@@",0,0,0),
        new(".?AVMovingAction@lineage@@",0,0,0),
        new(".?AVMonster@lineage@@",0,0,0),
        new(".?AVInteractiveNPC@lineage@@",0,0,0),
        new(".?AVUISubsystem@ui@lineage@@",0,0,0),
        new(".?AVPlayerShopSubsystem@lineage@@",0,0,0),
        new(".?AVPlayerShopLayoutPCImpl@ui@lineage@@",0,0,0),
        new(".?AVListView@ui@cocos2d@@",0,0,0)
    ];
}
