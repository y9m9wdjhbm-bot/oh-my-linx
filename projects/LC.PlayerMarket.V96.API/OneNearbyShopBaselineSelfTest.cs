using MemoryHub;
using System.Security.Cryptography;
using System.Text.Json;

namespace LcPlayerMarket;

internal static class OneNearbyShopBaselineSelfTest
{
    internal static OfflineCheck[] Run()
    {
        var hash = new string('A', 64);
        var baseline = new PersonalShopListing
        {
            OwnerIdRaw = ulong.MaxValue, LayoutState = "Absent", Complete = false,
            BankState = "Closed", BankFingerprint = hash, PageModeState = "Unknown",
            RawBankSummary = [new(0, ShopPageModes.ViewerBuy, 2, 0, 0, hash), new(1, ShopPageModes.ViewerSell, 1, 0, 0, hash)],
            SlotRows = Enumerable.Range(0, 16).Select(i => new PersonalShopSlotRow
            { BankIndex = i / 8, RowIndex = i % 8, State = "EmptyPair" }).ToArray()
        };
        var checks = new List<OfflineCheck>();
        void Check(string name, PersonalShopListing value, bool expected)
        {
            bool actual = OneNearbyShopVerification.IsClosedEmptyViewerBaseline(value);
            checks.Add(new("single_shop_empty_baseline_" + name, actual == expected,
                "Pure diagnostic admission; no LC, broker, helper or game input."));
        }
        Check("closed_absent_empty_allowed_without_promoting_complete", baseline, true);
        Check("zero_owner_sentinel", baseline with { OwnerIdRaw = 0 }, true);
        Check("unknown_owner", baseline with { OwnerIdRaw = null }, false);
        Check("existing_owner_preserved", baseline with { OwnerIdRaw = 12345 }, false);
        Check("present_incomplete_storage_rejected", baseline with { LayoutState = "Present" }, false);
        Check("present_complete_closed_empty_storage", baseline with { LayoutState = "Present", Complete = true }, true);
        Check("unknown_layout_rejected", baseline with { LayoutState = "Unknown" }, false);
        Check("unclosed_bank", baseline with { BankState = "Unknown" }, false);
        Check("incomplete_sample", baseline with { IncompleteReasons = ["READ_PARTIAL"] }, false);
        Check("complete_page_is_not_empty_absent_evidence", baseline with { Complete = true }, false);
        Check("missing_slot", baseline with { SlotRows = baseline.SlotRows[..15] }, false);
        Check("duplicate_slot", baseline with { SlotRows = Enumerable.Repeat(baseline.SlotRows[0], 16).ToArray() }, false);
        Check("occupied_slot", baseline with { SlotRows = baseline.SlotRows.Select((r, i) => i == 0 ? r with { PairPresent = true } : r).ToArray() }, false);
        Check("wrong_native_bank", baseline with { RawBankSummary = [baseline.RawBankSummary[0] with { NativeMode = 1 }, baseline.RawBankSummary[1]] }, false);
        Check("unverified_fingerprint", baseline with { BankFingerprint = null }, false);
        PersonalShopListing? actualBaseline=null;
        try
        {
            const string expectedSha="8D9298A9C9C025D6EC2D1E8A4F30AB5223C0F7060E7FFFD74D1917A3ABC9B8B7";
            var assembly=typeof(OneNearbyShopBaselineSelfTest).Assembly;
            var name=assembly.GetManifestResourceNames().Single(n=>n.EndsWith(".single-baseline-actual-v1.json",StringComparison.Ordinal));
            using var stream=assembly.GetManifestResourceStream(name)!;using var copy=new MemoryStream();stream.CopyTo(copy);var bytes=copy.ToArray();
            if(Convert.ToHexString(SHA256.HashData(bytes))!=expectedSha)throw new InvalidDataException("ACTUAL_BASELINE_FIXTURE_SHA_CHANGED");
            actualBaseline=JsonSerializer.Deserialize<PersonalShopListing>(bytes,HubProtocol.Json)??throw new InvalidDataException("ACTUAL_BASELINE_MISSING");
            bool matched=actualBaseline.OwnerIdRaw==ulong.MaxValue&&actualBaseline.LayoutState=="Present"&&actualBaseline.Complete
                &&actualBaseline.BankState=="Closed"&&actualBaseline.RawBankSummary.Length==2&&actualBaseline.SlotRows.Length==16;
            checks.Add(new("single_shop_actual_baseline_fixture_exact_pinned_bytes",matched,"Published V95 single-v1 result Before; exact full JSON; no LC or program start."));
        }
        catch(Exception error){checks.Add(new("single_shop_actual_baseline_fixture_exact_pinned_bytes",false,error.ToString()));}
        if(actualBaseline is {} actual)
        {
            string unchanged=JsonSerializer.Serialize(actual,HubProtocol.Json);
            Check("actual_present_complete_closed_banks_allowed",actual,true);
            Check("actual_absent_incomplete_equivalent_preserved",actual with{LayoutState="Absent",Complete=false},true);
            Check("actual_zero_owner_sentinel",actual with{OwnerIdRaw=0},true);
            Check("actual_unknown_owner_rejected",actual with{OwnerIdRaw=null},false);
            Check("actual_active_owner_rejected",actual with{OwnerIdRaw=22},false);
            Check("actual_unknown_layout_rejected",actual with{LayoutState="Unknown"},false);
            Check("actual_present_incomplete_rejected",actual with{Complete=false},false);
            Check("actual_absent_complete_rejected",actual with{LayoutState="Absent"},false);
            Check("actual_unknown_bank_state_rejected",actual with{BankState="Unknown"},false);
            Check("actual_open_bank_state_rejected",actual with{BankState="Open"},false);
            Check("actual_nonempty_rows_rejected",actual with{Rows=[new(0,"Occupied",true,"item","1",1,"unit",1,"Observed","")]},false);
            Check("actual_incomplete_reason_rejected",actual with{IncompleteReasons=["READ_PARTIAL"]},false);
            Check("actual_known_page_mode_rejected",actual with{PageMode=ShopPageModes.ViewerBuy},false);
            Check("actual_known_mode_state_rejected",actual with{PageModeState="Observed"},false);
            Check("actual_invalid_bank_fingerprint_rejected",actual with{BankFingerprint=new string('G',64)},false);
            Check("actual_missing_bank_rejected",actual with{RawBankSummary=actual.RawBankSummary[..1]},false);
            Check("actual_duplicate_bank_rejected",actual with{RawBankSummary=[actual.RawBankSummary[0],actual.RawBankSummary[0]]},false);
            Check("actual_occupied_bank_rejected",actual with{RawBankSummary=[actual.RawBankSummary[0] with{OccupiedSlots=1},actual.RawBankSummary[1]]},false);
            Check("actual_typed_payload_bank_rejected",actual with{RawBankSummary=[actual.RawBankSummary[0] with{TypedPayloadSlots=1},actual.RawBankSummary[1]]},false);
            Check("actual_invalid_bank_hash_rejected",actual with{RawBankSummary=[actual.RawBankSummary[0] with{RawSha256="unknown"},actual.RawBankSummary[1]]},false);
            Check("actual_wrong_bank_native_mode_rejected",actual with{RawBankSummary=[actual.RawBankSummary[0] with{NativeMode=1},actual.RawBankSummary[1]]},false);
            Check("actual_wrong_bank_role_rejected",actual with{RawBankSummary=[actual.RawBankSummary[0] with{PageRole=ShopPageModes.ViewerSell},actual.RawBankSummary[1]]},false);
            Check("actual_missing_slot_rejected",actual with{SlotRows=actual.SlotRows[..15]},false);
            Check("actual_duplicate_slot_rejected",actual with{SlotRows=Enumerable.Repeat(actual.SlotRows[0],16).ToArray()},false);
            Check("actual_outside_bank_slot_rejected",actual with{SlotRows=actual.SlotRows.Select((r,i)=>i==0?r with{BankIndex=2}:r).ToArray()},false);
            Check("actual_outside_row_slot_rejected",actual with{SlotRows=actual.SlotRows.Select((r,i)=>i==0?r with{RowIndex=8}:r).ToArray()},false);
            Check("actual_present_pair_rejected",actual with{SlotRows=actual.SlotRows.Select((r,i)=>i==0?r with{PairPresent=true}:r).ToArray()},false);
            Check("actual_unknown_slot_state_rejected",actual with{SlotRows=actual.SlotRows.Select((r,i)=>i==0?r with{State="Unknown"}:r).ToArray()},false);
            checks.Add(new("single_shop_empty_baseline_predicate_does_not_mutate_actual_observation",
                JsonSerializer.Serialize(actual,HubProtocol.Json)==unchanged,
                "Layout/Complete/visibility/item fields and original timestamps are retained; admission does not establish UI closure or a response."));
        }
        return checks.ToArray();
    }
}
