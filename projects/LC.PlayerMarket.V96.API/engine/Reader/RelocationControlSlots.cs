using System.Buffers.Binary;

namespace MemoryHub.Reader;

/// <summary>Only called after an exact primary TD/COL/vtable relationship is established.</summary>
internal static class RelocationControlSlots
{
    internal const string Party=".?AV?$_Ref_count_obj2@VParty@lineage@@@std@@";
    internal const string Item=".?AV?$_Ref_count_obj2@VItemEntity@lineage@@@std@@";
    internal const string Buff=".?AV?$_Ref_count_obj2@VBuffSlot@ui@lineage@@@std@@";
    internal const string Inventory=".?AV?$_Ref_count_obj2@VInventorySubsystem@lineage@@@std@@";
    internal const string GlView=".?AVGLViewImpl@cocos2d@@";
    internal const string PlayerShopLayout=".?AVPlayerShopLayoutPCImpl@ui@lineage@@";
    internal const string Character=".?AVCharacterDataSubsystem@lineage@@";
    internal static bool IsLateSlotType(string name)=>name is Party or Item or Buff or Inventory or GlView or PlayerShopLayout or Character;
    internal static bool Valid(string name,ReadOnlySpan<byte> slots,ulong image,uint imageSize,Func<ulong,bool> executable)
    {
        if(!IsLateSlotType(name)||slots.Length!=32||imageSize<4096||image>ulong.MaxValue-imageSize)return false;
        for(int index=0;index<4;index++)
        {
            ulong pointer=BinaryPrimitives.ReadUInt64LittleEndian(slots[(index*8)..]);
            if(pointer<image||pointer-image>=imageSize)return false;
        }
        bool third=executable(BinaryPrimitives.ReadUInt64LittleEndian(slots[16..]));
        bool fourth=executable(BinaryPrimitives.ReadUInt64LittleEndian(slots[24..]));
        return name==PlayerShopLayout?third:fourth&&(name!=Party||third);
    }
}
