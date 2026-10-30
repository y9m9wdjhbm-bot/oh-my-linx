namespace MemoryHub.Service;

internal static class ShopEmptyPagePolicy
{
    // Called only after this request's durable native settlement. Never grants dispatch or confirmation.
    internal static ShopEmptyPageObservation? TryObserve(ShopCommandRequest request, SourceIdentity expected,
        PersonalShopListing baseline, PersonalShopListing? after, ShopNativeOutcome native,
        DateTimeOffset requestedAt, DateTimeOffset now)
    {
        if(!native.Entered || !native.Returned || !native.SafeToSettle || !native.SourceMatched || native.NoEntryConfirmed || native.Failure is not null
            || after is null || expected.SessionKey!=request.ExpectedSourceSessionKey || baseline.SourceSessionKey!=expected.SessionKey
            || after.SourceSessionKey!=expected.SessionKey || after.OwnerIdRaw!=request.OwnerId || request.OwnerId is null
            || !ShopRuntimeContext.Fresh(after.SampledAt,now) || after.SampledAt<=baseline.SampledAt || after.SampledAt<requestedAt
            || after.LayoutState!="Present" || !after.Complete || !after.PageEmpty || after.BankState!="Closed"
            || after.PageMode is not null || after.PageModeState!="Unknown" || after.WholeStoreEmptyVerified
            || after.IncompleteReasons is not {Length:0} || after.ContainerRowCount!=8 || after.InspectedRows!=8
            || after.ExcludedEmptyOrTemplateRows!=8 || after.Rows is not {Length:8}
            || after.Rows.Any(r=>r is null || r.IncludedInListing || r.State!="EmptyOrTemplateCandidate")
            || after.RawBankSummary is null || after.SlotRows is null || after.SlotRows.Any(s=>s is null
                || s.ControlVerified || s.PayloadVerified || s.QuantityMatchesPayload
                || s.PriceCandidate is not null || s.QuantityCandidate is not null || s.PayloadQuantityCandidate is not null
                || s.RawDisplayTextCandidate is not null || s.EntityIdCandidate is not null || s.DefinitionIdCandidate is not null || s.UidCandidate is not null
                || s.ItemKindRaw is not null || s.ItemKindDetailFlag is not null || s.ItemKind is not null
                || s.ItemKindState!="Unknown" || s.ItemStateEvidence is not null))return null;
        var evidence=new ShopEmptyPageObservation {RequestId=request.RequestId,SourceSessionKey=expected.SessionKey,OwnerId=request.OwnerId.Value,
            RequestedDirection=request.ViewerDirection??"",RequestedAt=requestedAt,BaselineSampledAt=baseline.SampledAt,
            SampledAt=after.SampledAt,ValidatedAt=now,LayoutState=after.LayoutState,BankState=after.BankState,
            BankFingerprint=after.BankFingerprint??"",Complete=true,BankSummaries=after.RawBankSummary.ToArray(),
            Slots=after.SlotRows.Select(s=>new ShopEmptySlotObservation(s.BankIndex,s.RowIndex,s.State,s.PairPresent)).ToArray(),
            ExcludedRowIndices=after.Rows.Select(r=>r.RowIndex).ToArray(),NativeLifecycleSettled=true};
        return ShopEmptyPageRules.IsEvidenceValid(evidence)?evidence:null;
    }

    internal static MarketScanState CountPage(MarketScanState scan,ShopCommandResponse response)
    {
        bool noEntryClaim=ShopNotEnteredPageRules.HasNotEnteredClaim(response);
        bool notEntered=ShopNotEnteredPageRules.IsNotEnteredResponse(response);
        // A contradictory no-entry claim cannot fall through to success or empty.
        bool verified=!noEntryClaim && response.ResponseVerified;
        bool empty=!noEntryClaim && ShopEmptyPageRules.IsNeutralResponse(response);
        return scan with {Completed=scan.Completed+1,Succeeded=scan.Succeeded+(verified?1:0),
            EmptyObservedPages=scan.EmptyObservedPages+(empty?1:0),NotEnteredPages=scan.NotEnteredPages+(notEntered?1:0),
            Failed=scan.Failed+(verified || empty || notEntered?0:1)};
    }
}
