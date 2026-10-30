using System.Security.Cryptography;
using System.Text;

namespace MemoryHub.Reader;

internal sealed partial class PersonalShopSampler
{
    private const string ShopItemControlName = ".?AV?$_Ref_count_obj2@VItemEntity@lineage@@@std@@";
    private readonly HashSet<ulong> shopItemControls = [];
    private readonly HashSet<ulong> shopItemHierarchies = [];
    private sealed record ShopBanks(string Fingerprint, byte ActiveFlagRaw,
        PersonalShopBankSummary[] Summary, PersonalShopSlotRow[] Rows);

    private ShopBanks ReadShopBanks(ulong shop, ulong ownerId)
    {
        byte[] context = Read(shop + 0x2C0, 9, "shop owner and ACK active byte");
        Need(U64(context) == ownerId, "GROUP_SOURCE_CHANGED", "商店 owner 在讀 bank 前改變。");
        byte[] buy = Read(shop + 0x2D0, 8 * 0x38, "ViewerBuy/native2 complete eight slots A");
        byte[] sell = Read(shop + 0x490, 8 * 0x38, "ViewerSell/native1 complete eight slots A");
        var rows = new List<PersonalShopSlotRow>(); var summaries = new List<PersonalShopBankSummary>();
        foreach (var bank in new[] { (Index: 0, Bytes: buy), (Index: 1, Bytes: sell) })
        {
            int occupied = 0, payloads = 0;
            for (int index = 0; index < 8; index++)
            {
                byte[] raw = bank.Bytes.AsSpan(index * 0x38, 0x38).ToArray();
                ulong item = U64(raw), control = U64(raw, 8), qty = U64(raw, 0x28);
                long price = unchecked((long)U64(raw, 0x20)); bool anyPair = item != 0 || control != 0;
                var row = new PersonalShopSlotRow { BankIndex = bank.Index, RowIndex = index,
                    State = anyPair ? "UnverifiedPair" : "EmptyPair", PairPresent = anyPair,
                    RawField10 = U64(raw, 0x10), RawField18 = raw[0x18], RawPriceField20 = price, RawQuantityField28 = qty };
                if (anyPair)
                {
                    occupied++;
                    try
                    {
                        VerifyShopItemControl(item, control);
                        row = row with { ControlVerified = true, State = "TypedItem_PayloadPending" };
                        row = ReadShopItemPayload(item, control, row);
                        payloads++;
                    }
                    catch (ProbeFailure error) when (ShopSlotRecoverable(error.Code))
                    {
                        row = row with { State = row.ControlVerified ? "TypedItem_PartialPayload" : "PartialTypeUnavailable",
                            Detail = error.Code + ": " + error.Message };
                    }
                }
                rows.Add(row);
            }
            summaries.Add(new(bank.Index, bank.Index == 0 ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell,
                bank.Index == 0 ? 2 : 1, occupied, payloads, Convert.ToHexString(SHA256.HashData(bank.Bytes))));
        }
        VerifyShopItemControlsAlive();
        // Read() tracks every byte. Run and the outer SourceClosure close both
        // complete banks, all typed ownership and every selected value together.
        byte[] material = new byte[context.Length + buy.Length + sell.Length];
        context.CopyTo(material, 0); buy.CopyTo(material, context.Length); sell.CopyTo(material, context.Length + buy.Length);
        return new(Convert.ToHexString(SHA256.HashData(material)), context[8], summaries.ToArray(), rows.ToArray());
    }

    private void VerifyShopItemControl(ulong item, ulong control)
    {
        Need(Pointer(item) && Pointer(control) && control <= 0x7FFFFFFFFFEFUL && item == control + 16,
            "SHOP_ITEM_OWNER", "商品 shared payload 必須精確等於 control+16。");
        TypeInfo type = Type(control);
        Need(memory.Profile.TypeBindings.TryGetValue(ShopItemControlName,out var binding)&&
            type.Name == ShopItemControlName && type.ObjectOffset == 0 && type.ConstructionOffset == 0 &&
            type.Vtable == memory.Image + binding.Vtable && type.Col == memory.Image + binding.Col &&
            type.Descriptor == binding.Descriptor,
            "SHOP_ITEM_TYPE", "商品 control 完整 RTTI／primary COL／ACK vtable 不符。");
        if (!shopItemHierarchies.Contains(type.Vtable))
        {
            byte[] chd = Meta(memory.Image + type.Hierarchy, 16, "ItemEntity control full hierarchy");
            uint count = U32(chd, 8), array = U32(chd, 12);
            Need(U32(chd) == 0 && count is > 0 and <= 8 && (array & 3) == 0,
                "SHOP_ITEM_HIERARCHY", "商品 control CHD 數量或對齊不符。");
            byte[] entries = Meta(memory.Image + array, checked((int)count * 4), "ItemEntity full base array");
            for (int i = 0; i < count; i++)
            {
                uint rva = U32(entries, i * 4); Need((rva & 3) == 0, "SHOP_ITEM_HIERARCHY", "商品 BCD 未對齊。");
                byte[] bcd = Meta(memory.Image + rva, 28, "ItemEntity full base descriptor");
                uint td = U32(bcd), hierarchy = U32(bcd, 24); Meta(memory.Image + td, 16, "ItemEntity base TD");
                string name = Name(td);
                Need(U32(bcd, 4) < count && I32(bcd, 8) == 0 && I32(bcd, 12) == -1 && I32(bcd, 16) == 0 &&
                    (hierarchy == 0 || ((hierarchy & 3) == 0 && InImage(memory.Image + hierarchy, 16))) &&
                    (i != 0 || (td == type.Descriptor && name == ShopItemControlName && U32(bcd, 4) == count - 1)),
                    "SHOP_ITEM_HIERARCHY", "商品 control 完整 self/base PMD 不符。");
            }
            shopItemHierarchies.Add(type.Vtable);
        }
        CheckShopItemRefs(control); shopItemControls.Add(control);
    }
    private void CheckShopItemRefs(ulong control)
    {
        // Positive in each pass, not byte-stable: ordinary native use can retain/release.
        byte[] refs = Raw(control + 8, 8, "ItemEntity current strong/weak refs");
        Need(I32(refs) > 0 && I32(refs, 4) > 0, "SHOP_ITEM_REFS", "商品 shared 參照已失效。");
    }
    private void VerifyShopItemControlsAlive()
    { foreach (ulong control in shopItemControls) CheckShopItemRefs(control); }

    private PersonalShopSlotRow ReadShopItemPayload(ulong item, ulong control, PersonalShopSlotRow row)
    {
        // Existing typed InventoryFieldReader layout; no resource table inference.
        byte[] id = Read(item + 8, 0x20, "typed shop ItemEntity IDs");
        byte[] quantity = Read(item + 0x28, 8, "typed shop ItemEntity quantity");
        // The same Read journal closes these bytes with ownership, payload and source.
        // Sell ACK forwards entry+54 through5057A0; Buy's constructor destination
        // remains unverified, so its raw value is never promoted to a kind here.
        byte[] kindFields = Read(item + 0x4C, 12, "typed shop ItemEntity detail flag and field54");
        byte[] descriptor = Read(item + 0x58, 32, "typed shop ItemEntity raw display descriptor");
        ulong length = U64(descriptor, 16), cap = U64(descriptor, 24);
        Need(length is > 0 and <= 512 && cap >= length && cap is >= 15 and <= 4096 && (cap != 15 || length <= 15),
            "SHOP_ITEM_TEXT", "商品 display 非有界完整 SSO／heap 字串。");
        string display;
        try { display = String(descriptor, 0, 512, "typed shop ItemEntity raw display"); }
        catch (DecoderFallbackException) { throw new ProbeFailure("SHOP_ITEM_TEXT", "商品 display 不是完整 UTF-8。"); }
        Need(!string.IsNullOrWhiteSpace(display) && !display.Any(char.IsControl), "SHOP_ITEM_TEXT", "商品 display 空白或含控制字元。");
        Need(row.RawPriceField20 is >= 0 and <= int.MaxValue, "SHOP_ITEM_PRICE", "ACK 價格不是非負 sign-extended int32。");
        CheckShopItemRefs(control);
        ulong value = U64(quantity);
        int kindRaw = I32(kindFields, 8); byte detailFlag = kindFields[0];
        var kind = PersonalShopItemKindRules.Decode(row.BankIndex, kindRaw, detailFlag, memory.Profile.Sha256, currentItemKindCodeVerified);
        return row with { State = "TypedPayloadCandidate", PayloadVerified = true,
            EntityIdCandidate = U32(id), DefinitionIdCandidate = U32(id, 8), UidCandidate = U64(id, 16),
            PriceCandidate = (ulong)row.RawPriceField20, QuantityCandidate = row.RawQuantityField28,
            PayloadQuantityCandidate = value, QuantityMatchesPayload = value == row.RawQuantityField28,
            RawDisplayTextCandidate = display,
            ItemKindRaw = kindRaw, ItemKindDetailFlag = detailFlag, ItemKind = kind.Kind, ItemKindState = kind.State, ItemStateEvidence = currentItemKindCodeVerified ? itemStateEvidence : null,
            Detail = "ACK +20 價／+28 量；typed ItemEntity payload；等待同列 UI 價格與獨占方向 bank 核對" };
    }

    private static bool ShopSlotRecoverable(string code) => code is "RTTI_VTABLE" or "RTTI_COL" or "RTTI_NAME" or
        "METADATA_RANGE" or "POINTER_RANGE" or "SHOP_READ_FAILED" or "SHOP_ITEM_OWNER" or "SHOP_ITEM_TYPE" or
        "SHOP_ITEM_HIERARCHY" or "SHOP_ITEM_REFS" or "SHOP_ITEM_TEXT" or "SHOP_ITEM_PRICE" or "STRING_LIMIT" or "STRING_NUL";
}
