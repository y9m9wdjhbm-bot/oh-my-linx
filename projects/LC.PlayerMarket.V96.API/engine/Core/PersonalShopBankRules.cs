namespace MemoryHub;

/// <summary>Correlates a closed page with one exclusive typed bank, never with a presumed new ACK.</summary>
public static class PersonalShopBankRules
{
    public static PersonalShopListing Match(PersonalShopListing listing, string? sourceBuildSha256 = null, bool currentBuildCodeVerified = false, bool currentBuildIdentificationVerified = false, PersonalShopItemStateEvidence? evidence = null)
    {
        // Never carry forward a previous page's inferred direction, quantity or item kind.
        var unknown = listing with { PageMode = null, PageModeState = "Unknown",
            SlotRows = listing.SlotRows.Select(slot => slot.ItemStateEvidence?.Mode == "BuildCertified" || slot.ItemKindState == "BuildCertifiedItemKind"
                ? slot with { ItemKind = null, ItemKindState = "Unknown", ItemStateEvidence = null } : slot).ToArray(),
            Rows = listing.Rows.Select(row => row with { Quantity = null, QuantityState = "Unverified",
                ItemKindRaw = null, ItemKindDetailFlag = null, ItemKind = null, ItemKindState = "Unknown", ItemIdentificationState = "Unknown", ItemIdentificationEvidence = "Unverified", ItemStateEvidence = null }).ToArray() };
        if (!listing.Complete || listing.LayoutState != "Present" || listing.BankState != "Closed" ||
            listing.BankFingerprint is null || listing.RawBankSummary.Length != 2 || listing.SlotRows.Length != 16) return unknown;
        if (listing.RawBankSummary.Select(bank => bank.BankIndex).Distinct().Count() != 2 ||
            listing.RawBankSummary.Any(bank => bank.BankIndex is < 0 or > 1)) return unknown;
        if (listing.SlotRows.Any(slot => slot.BankIndex is < 0 or > 1 || slot.RowIndex is < 0 or > 7) ||
            listing.SlotRows.Select(slot => (slot.BankIndex, slot.RowIndex)).Distinct().Count() != 16) return unknown;
        var occupied = listing.SlotRows.Where(slot => slot.PairPresent).ToArray();
        if (occupied.Length == 0 || occupied.Select(slot => slot.BankIndex).Distinct().Count() != 1) return unknown;
        if (listing.SlotRows.Any(slot => !slot.PairPresent && slot.State != "EmptyPair")) return unknown;
        if (occupied.Any(slot => !slot.ControlVerified || !slot.PayloadVerified || !slot.QuantityMatchesPayload ||
            slot.PriceCandidate is null || slot.QuantityCandidate is null || slot.RawPriceField20 is < 0 or > int.MaxValue ||
            slot.PriceCandidate != (ulong)slot.RawPriceField20 || slot.QuantityCandidate != slot.RawQuantityField28 ||
            slot.PayloadQuantityCandidate != slot.QuantityCandidate || string.IsNullOrWhiteSpace(slot.RawDisplayTextCandidate))) return unknown;
        int bankIndex = occupied[0].BankIndex;
        var summary = listing.RawBankSummary.Single(bank => bank.BankIndex == bankIndex);
        string mode = bankIndex == 0 ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell;
        if (summary.PageRole != mode || summary.NativeMode != (bankIndex == 0 ? 2 : 1) ||
            summary.OccupiedSlots != occupied.Length || summary.TypedPayloadSlots != occupied.Length ||
            listing.RawBankSummary.Any(bank => bank.BankIndex != bankIndex && bank.OccupiedSlots != 0)) return unknown;
        var included = listing.Rows.Where(row => row.IncludedInListing).ToArray();
        if (included.Length != occupied.Length || included.Select(row => row.RowIndex).Distinct().Count() != included.Length) return unknown;
        var byIndex = occupied.ToDictionary(slot => slot.RowIndex);
        foreach (var row in included)
            if (row.Price is null || !byIndex.TryGetValue(row.RowIndex, out var slot) || row.Price != slot.PriceCandidate) return unknown;
        return listing with
        {
            PageMode = mode, PageModeState = "MatchedExclusiveBankAndRows",
            SlotRows = PersonalShopItemEvidenceRules.IsBuildCertified(evidence) ? listing.SlotRows.Select(slot =>
            {
                if (!slot.PairPresent) return slot;
                var decoded = PersonalShopItemKindRules.Decode(slot.BankIndex, slot.ItemKindRaw, slot.ItemKindDetailFlag, sourceBuildSha256, false, evidence);
                return slot with { ItemKind = decoded.Kind, ItemKindState = decoded.State, ItemStateEvidence = evidence };
            }).ToArray() : listing.SlotRows,
            Rows = unknown.Rows.Select(row =>
            {
                if (!row.IncludedInListing) return row;
                var slot = byIndex[row.RowIndex];
                var kind = PersonalShopItemKindRules.Decode(bankIndex, slot.ItemKindRaw, slot.ItemKindDetailFlag, sourceBuildSha256, currentBuildCodeVerified, evidence);
                var identification = PersonalShopItemIdentificationRules.Decode(slot.ItemKindRaw, slot.ItemKindDetailFlag, sourceBuildSha256,
                    currentBuildCodeVerified && currentBuildIdentificationVerified, evidence);
                return row with
                {
                    Quantity = slot.QuantityCandidate,
                    QuantityState = "TypedSlotPayloadAndUiPriceMatched",
                    ItemKindRaw = slot.ItemKindRaw, ItemKindDetailFlag = slot.ItemKindDetailFlag,
                    ItemKind = kind.Kind, ItemKindState = kind.State,
                    ItemIdentificationState = identification.State, ItemIdentificationEvidence = identification.Evidence, ItemStateEvidence = evidence,
                    Detail = row.Detail + "；同列價格、唯一方向 bank 與 typed payload 數量一致；不代表新回覆或整店完整清單；道具類別狀態=" + kind.State
                };
            }).ToArray()
        };
    }
}
