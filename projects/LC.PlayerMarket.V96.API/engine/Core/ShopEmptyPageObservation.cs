namespace MemoryHub;

/// <summary>A closed empty observation at the current selected owner. It proves no page direction, ACK or whole-store emptiness.</summary>
public sealed record ShopEmptyPageObservation
{
    public int Schema { get; init; } = 1;
    public string ObservationMode { get; init; } = "ClosedOwnerBanks";
    public bool ReadSucceeded { get; init; }
    public int IncludedItems { get; init; } = -1;
    public ulong? ObservedOwnerIdRaw { get; init; }
    public string? ObservedPageDirection { get; init; }
    public string RequestId { get; init; } = "";
    public string SourceSessionKey { get; init; } = "";
    public ulong OwnerId { get; init; }
    public string RequestedDirection { get; init; } = "";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset BaselineSampledAt { get; init; }
    public DateTimeOffset SampledAt { get; init; }
    public DateTimeOffset ValidatedAt { get; init; }
    public string LayoutState { get; init; } = "Unknown";
    public string BankState { get; init; } = "Unknown";
    public string BankFingerprint { get; init; } = "";
    public bool Complete { get; init; }
    public PersonalShopBankSummary[] BankSummaries { get; init; } = [];
    public ShopEmptySlotObservation[] Slots { get; init; } = [];
    public int[] ExcludedRowIndices { get; init; } = [];
    public bool NativeLifecycleSettled { get; init; }
    public bool DirectionVerified { get; init; }
    public bool ResponseMatched { get; init; }
    public bool ServerAckVerified { get; init; }
}

public sealed record ShopEmptySlotObservation(int BankIndex, int RowIndex, string State, bool PairPresent);

public static class ShopEmptyPageRules
{
    public const string State = "CurrentOwnerEmptyPageObserved";
    public const string SingleReadState = "SingleReadEmptyPageObserved";
    public const string SingleReadDetail = "開頁指令已返回並完成清理；依設定間隔只讀取一次，頁面沒有商品。本頁標記為無商品後接續下一頁，不重開、不輪詢；實際原始店主及方向另存，未宣稱網路 ACK。";
    public const string Detail = "原指令已返回並完成清理；同來源目前店主的完整頁面與兩個商品 bank 均為空。買／賣方向及新回覆未確認；不代表指定方向或整店無商品，保留已送紀錄且不重送。";

    public static bool IsEvidenceValid(ShopEmptyPageObservation? e)
    {
        if (e?.Schema == 2) return IsSingleReadEvidenceValid(e);
        if(e is null || e.Schema!=1 || string.IsNullOrWhiteSpace(e.RequestId) || e.RequestId.Length>256
            || string.IsNullOrWhiteSpace(e.SourceSessionKey) || e.SourceSessionKey.Length>2048
            || e.OwnerId is 0 or >int.MaxValue || e.RequestedDirection is not (ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell)
            || e.RequestedAt==default || e.BaselineSampledAt==default || e.SampledAt<=e.BaselineSampledAt || e.SampledAt<e.RequestedAt
            || e.ValidatedAt<e.SampledAt || e.ValidatedAt-e.SampledAt>TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds)
            || !e.NativeLifecycleSettled || e.DirectionVerified || e.ResponseMatched || e.ServerAckVerified
            || e.LayoutState!="Present" || e.BankState!="Closed" || !e.Complete
            || !Hash(e.BankFingerprint) || e.BankSummaries is not {Length:2} || e.Slots is not {Length:16}
            || e.ExcludedRowIndices is not {Length:8})return false;
        for(int bank=0;bank<2;bank++)
        {
            var found=e.BankSummaries.Where(b=>b is not null && b.BankIndex==bank).ToArray();
            if(found.Length!=1 || found[0].PageRole!=(bank==0?ShopPageModes.ViewerBuy:ShopPageModes.ViewerSell)
                || found[0].NativeMode!=(bank==0?2:1) || found[0].OccupiedSlots!=0 || found[0].TypedPayloadSlots!=0 || !Hash(found[0].RawSha256))return false;
            for(int row=0;row<8;row++)
            {
                var slots=e.Slots.Where(s=>s is not null && s.BankIndex==bank && s.RowIndex==row).ToArray();
                if(slots.Length!=1 || slots[0].State!="EmptyPair" || slots[0].PairPresent)return false;
            }
        }
        return e.ExcludedRowIndices.Order().SequenceEqual(Enumerable.Range(0,8));
    }

    public static bool IsNeutralResponse(ShopCommandResponse response) => response.EmptyPageObserved
        && response.State==(response.EmptyPageObservation?.Schema==2?SingleReadState:State) && !response.ResponseVerified && response.NativeEntered && response.NativeReturned
        && !response.SessionBlocked && response.FailureCode is null && response.FailureScope is null
        && response.FailurePhase is null && response.FailureRva is null && IsEvidenceValid(response.EmptyPageObservation)
        && response.EmptyPageObservation!.RequestId==response.RequestId
        && response.EmptyPageObservation.SourceSessionKey==response.CurrentSourceSessionKey
        && response.At>=response.EmptyPageObservation.ValidatedAt;

    public static bool IsNeutralListing(MarketListing listing) => listing.EmptyPageObserved && listing.State==(listing.EmptyPageObservation?.Schema==2?SingleReadState:State)
        && !listing.OwnerAndResponseMatched && !listing.PageEmpty && !listing.Complete && listing.Rows is {Length:0}
        && IsEvidenceValid(listing.EmptyPageObservation) && listing.EmptyPageObservation!.RequestId==listing.RequestId
        && listing.EmptyPageObservation.SourceSessionKey==listing.SourceSessionKey && listing.EmptyPageObservation.OwnerId==listing.Owner.Id
        && listing.EmptyPageObservation.RequestedDirection==listing.ViewerDirection && listing.EmptyPageObservation.SampledAt==listing.CapturedAt
        && listing.EmptyPageObservation.RequestedAt==listing.RequestedAt;

    private static bool IsSingleReadEvidenceValid(ShopEmptyPageObservation e) =>
        e.ObservationMode == "SingleReadNoItems" && e.ReadSucceeded && e.IncludedItems == 0
        && !string.IsNullOrWhiteSpace(e.RequestId) && e.RequestId.Length <= 256
        && !string.IsNullOrWhiteSpace(e.SourceSessionKey) && e.SourceSessionKey.Length <= 2048
        && e.OwnerId is > 0 and <= int.MaxValue
        && e.RequestedDirection is (ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell)
        && e.RequestedAt != default && e.BaselineSampledAt != default
        && e.SampledAt > e.BaselineSampledAt && e.SampledAt >= e.RequestedAt
        && e.ValidatedAt >= e.SampledAt && e.ValidatedAt-e.SampledAt <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds)
        && e.NativeLifecycleSettled && !e.DirectionVerified && !e.ResponseMatched && !e.ServerAckVerified
        && e.LayoutState is ("Present" or "Absent") && (e.LayoutState != "Present" || e.Complete);

    private static bool Hash(string? value)=>value is {Length:64} && value.All(Uri.IsHexDigit);
}
