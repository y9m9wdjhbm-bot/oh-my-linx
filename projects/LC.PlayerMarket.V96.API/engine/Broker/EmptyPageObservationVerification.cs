using System.Reflection;
using System.Security.Cryptography;
using MemoryHub.Reader;
using System.Security.Principal;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class EmptyPageObservationVerification
{
    private static readonly DateTimeOffset Epoch=new(2026,10,17,0,0,0,TimeSpan.Zero);
    private static readonly SourceIdentity Source=new(4242,987654321,"offline-empty-only","0x100000",1,4096,new string('A',64),"offline-empty-source");
    private static readonly ShopNativeOutcome Native=new(true,true,true,"offline complete return and cleanup",null){SourceMatched=true};
    private static readonly ShopCommandRequest Request=new(){RequestId="offline-empty-1",ExpectedSourceSessionKey=Source.SessionKey,OwnerId=22,ViewerDirection=ShopPageModes.ViewerBuy};
    private static readonly PersonalShopListing Baseline=new(){SourceSessionKey=Source.SessionKey,OwnerIdRaw=17,SampledAt=Epoch.AddSeconds(-1)};
    private static PersonalShopListing Empty()=>new(){SourceSessionKey=Source.SessionKey,OwnerIdRaw=22,SampledAt=Epoch.AddMilliseconds(500),
        LayoutState="Present",Complete=true,PageEmpty=true,BankState="Closed",BankFingerprint=new string('B',64),
        RawBankSummary=[new(0,ShopPageModes.ViewerBuy,2,0,0,new string('C',64)),new(1,ShopPageModes.ViewerSell,1,0,0,new string('D',64))],
        SlotRows=Enumerable.Range(0,16).Select(i=>new PersonalShopSlotRow{BankIndex=i/8,RowIndex=i%8,State="EmptyPair",RawField10=7,RawField18=1,RawPriceField20=123,RawQuantityField28=456}).ToArray(),
        ContainerRowCount=8,InspectedRows=8,ExcludedEmptyOrTemplateRows=8,
        Rows=Enumerable.Range(0,8).Select(i=>PersonalShopTextRules.Decode(i,"아이템 이름","2555","個")).ToArray()};
    private static ShopEmptyPageObservation? Observe(PersonalShopListing? sample=null,ShopNativeOutcome? native=null,ShopCommandRequest? request=null,PersonalShopListing? before=null,DateTimeOffset? now=null)
        =>ShopEmptyPagePolicy.TryObserve(request??Request,Source,before??Baseline,sample??Empty(),native??Native,Epoch,now??Epoch.AddSeconds(1));
    private static ShopCommandResponse Response(ShopEmptyPageObservation e)=>new(){RequestId=e.RequestId,CurrentSourceSessionKey=e.SourceSessionKey,
        At=e.ValidatedAt,State=ShopEmptyPageRules.State,Detail=ShopEmptyPageRules.Detail,NativeEntered=true,NativeReturned=true,EmptyPageObserved=true,EmptyPageObservation=e};
    private static MarketListing Listing(ShopEmptyPageObservation e)=>new(){RequestId=e.RequestId,SourceSessionKey=e.SourceSessionKey,
        Owner=new(){Id=e.OwnerId},ViewerDirection=e.RequestedDirection,CapturedAt=e.SampledAt,RequestedAt=e.RequestedAt,
        State=ShopEmptyPageRules.State,Detail=ShopEmptyPageRules.Detail,EmptyPageObserved=true,EmptyPageObservation=e};
    internal static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action body){try{body();checks.Add(new("empty_observation_"+name,true,"Offline actual policy/DTO/progress; no LC/helper/input."));}catch(Exception e){checks.Add(new("empty_observation_"+name,false,e.ToString()));}}
        void Reject(string name,Func<PersonalShopListing,PersonalShopListing> change)=>Check(name,()=>Need(Observe(change(Empty())) is null,"Malformed/unknown listing became neutral"));
        foreach(string direction in new[]{ShopPageModes.ViewerBuy,ShopPageModes.ViewerSell})
            Check("closed_empty_neutral_"+direction,()=>{var e=Observe(request:Request with{ViewerDirection=direction})!;Need(e is not null&&ShopEmptyPageRules.IsNeutralResponse(Response(e))&&ShopEmptyPageRules.IsNeutralListing(Listing(e)),"Complete closed empty was rejected");Need(!e.DirectionVerified&&!e.ResponseMatched&&!e.ServerAckVerified&&!Listing(e).PageEmpty,"Request direction became observed direction/ACK");});
        Reject("wrong_source",p=>p with{SourceSessionKey="other"});Reject("wrong_owner",p=>p with{OwnerIdRaw=23});
        Reject("not_present",p=>p with{LayoutState="Unknown"});Reject("incomplete",p=>p with{Complete=false});
        Reject("not_empty",p=>p with{PageEmpty=false});Reject("bank_not_closed",p=>p with{BankState="Unknown"});
        Reject("mode_claim",p=>p with{PageMode=ShopPageModes.ViewerBuy});Reject("mode_state_claim",p=>p with{PageModeState="MatchedExclusiveBankAndRows"});
        Reject("whole_store_claim",p=>p with{WholeStoreEmptyVerified=true});Reject("partial_reason",p=>p with{IncompleteReasons=["partial"]});
        Reject("container_count",p=>p with{ContainerRowCount=7});Reject("inspected_count",p=>p with{InspectedRows=7});
        Reject("excluded_count",p=>p with{ExcludedEmptyOrTemplateRows=7});Reject("row_count",p=>p with{Rows=p.Rows[..7]});
        Reject("row_included",p=>p with{Rows=p.Rows.Select((r,i)=>i==0?r with{IncludedInListing=true}:r).ToArray()});
        Reject("row_partial",p=>p with{Rows=p.Rows.Select((r,i)=>i==0?r with{State="Partial"}:r).ToArray()});
        Reject("duplicate_row",p=>p with{Rows=p.Rows.Select((r,i)=>i==0?r with{RowIndex=1}:r).ToArray()});
        Reject("missing_bank",p=>p with{RawBankSummary=p.RawBankSummary[..1]});
        Reject("duplicate_bank",p=>p with{RawBankSummary=[p.RawBankSummary[0],p.RawBankSummary[0]]});
        Reject("occupied_bank",p=>p with{RawBankSummary=p.RawBankSummary.Select((b,i)=>i==0?b with{OccupiedSlots=1}:b).ToArray()});
        Reject("typed_bank",p=>p with{RawBankSummary=p.RawBankSummary.Select((b,i)=>i==1?b with{TypedPayloadSlots=1}:b).ToArray()});
        Reject("bank_role",p=>p with{RawBankSummary=p.RawBankSummary.Select((b,i)=>i==0?b with{PageRole=ShopPageModes.ViewerSell}:b).ToArray()});
        Reject("bank_mode",p=>p with{RawBankSummary=p.RawBankSummary.Select((b,i)=>i==0?b with{NativeMode=1}:b).ToArray()});
        Reject("bank_hash",p=>p with{BankFingerprint="partial"});Reject("slot_count",p=>p with{SlotRows=p.SlotRows[..15]});
        Reject("duplicate_slot",p=>p with{SlotRows=p.SlotRows.Select((s,i)=>i==0?s with{RowIndex=1}:s).ToArray()});
        Reject("nonempty_pair",p=>p with{SlotRows=p.SlotRows.Select((s,i)=>i==0?s with{PairPresent=true}:s).ToArray()});
        Reject("unknown_slot",p=>p with{SlotRows=p.SlotRows.Select((s,i)=>i==0?s with{State="Unknown"}:s).ToArray()});
        Check("empty_pair_typed_flags_rejected",()=>{
            var p=Empty();var s=p.SlotRows[0];
            foreach(var residue in new[]{s with{ControlVerified=true},s with{PayloadVerified=true},s with{QuantityMatchesPayload=true}})
                Need(Observe(p with{SlotRows=p.SlotRows.Select((row,i)=>i==0?residue:row).ToArray()}) is null,"Typed flag survived empty-slot projection");});
        Check("empty_pair_typed_candidates_rejected",()=>{
            var p=Empty();var s=p.SlotRows[0];
            foreach(var residue in new[]{s with{UidCandidate=1},s with{EntityIdCandidate=1},s with{DefinitionIdCandidate=1},
                s with{PriceCandidate=0},s with{QuantityCandidate=0},s with{PayloadQuantityCandidate=0},s with{RawDisplayTextCandidate=""}})
                Need(Observe(p with{SlotRows=p.SlotRows.Select((row,i)=>i==0?residue:row).ToArray()}) is null,"Typed candidate survived empty-slot projection");});
        Check("empty_pair_item_semantics_rejected",()=>{
            var p=Empty();var s=p.SlotRows[0];
            foreach(var residue in new[]{s with{ItemKindRaw=0},s with{ItemKindDetailFlag=0},s with{ItemKind="Normal"},
                s with{ItemKindState="Verified"},s with{ItemStateEvidence=new PersonalShopItemStateEvidence()}})
                Need(Observe(p with{SlotRows=p.SlotRows.Select((row,i)=>i==0?residue:row).ToArray()}) is null,"Item semantics survived empty-slot projection");});
        Check("stale_sample",()=>Need(Observe(now:Epoch.AddSeconds(4)) is null,"Stale sample neutral"));
        Check("future_sample",()=>Need(Observe(now:Epoch) is null,"Future sample neutral"));
        Check("same_baseline_time",()=>Need(Observe(before:Baseline with{SampledAt=Epoch.AddMilliseconds(500)}) is null,"Old sample neutral"));
        Check("wrong_baseline_source",()=>Need(Observe(before:Baseline with{SourceSessionKey="other"}) is null,"Cross-source sample neutral"));
        foreach(var (name,native) in new[]{("not_entered",Native with{Entered=false}),("not_returned",Native with{Returned=false}),
            ("not_settled",Native with{SafeToSettle=false}),("source_not_matched",Native with{SourceMatched=false}),
            ("positive_noentry",Native with{NoEntryConfirmed=true}),("native_failure",Native with{Failure=ShopFailure.Unknown("pending")})})
            Check(name,()=>Need(Observe(native:native) is null,"Unsettled native became neutral"));
        Check("wrong_request_source",()=>Need(Observe(request:Request with{ExpectedSourceSessionKey="other"}) is null,"Wrong request source neutral"));
        foreach(var (name,mutate) in new (string,Func<ShopCommandResponse,ShopCommandResponse>)[]{
            ("missing_evidence",r=>r with{EmptyPageObservation=null}),("failure",r=>r with{FailureCode="Unknown"}),
            ("blocked",r=>r with{SessionBlocked=true}),("verified",r=>r with{ResponseVerified=true}),
            ("other_request",r=>r with{RequestId="other"}),("other_source",r=>r with{CurrentSourceSessionKey="other"}),
            ("direction_claim",r=>r with{EmptyPageObservation=r.EmptyPageObservation! with{DirectionVerified=true}}),
            ("ack_claim",r=>r with{EmptyPageObservation=r.EmptyPageObservation! with{ServerAckVerified=true}})})
            Check("response_"+name,()=>Need(!ShopEmptyPageRules.IsNeutralResponse(mutate(Response(Observe()!))),"Invalid response neutral"));
        Check("json_roundtrip_stays_neutral_unconfirmed",()=>{var response=JsonSerializer.Deserialize<ShopCommandResponse>(JsonSerializer.Serialize(Response(Observe()!),HubProtocol.Json),HubProtocol.Json)!;Need(ShopEmptyPageRules.IsNeutralResponse(response)&&!response.ResponseVerified,"JSON lost neutral boundary");});
        Check("three_counts_keep_failures",()=>{var scan=new MarketScanState();scan=ShopEmptyPagePolicy.CountPage(scan,new(){ResponseVerified=true});scan=ShopEmptyPagePolicy.CountPage(scan,Response(Observe()!));scan=ShopEmptyPagePolicy.CountPage(scan,new(){FailureCode="ReadFailed"});Need(scan.Completed==3&&scan.Succeeded==1&&scan.EmptyObservedPages==1&&scan.Failed==1,"Empty observation hid real failures");});
        Check("neutral_continues_both_dispatch_policies",()=>{var r=Response(Observe()!);Need(ShopScanPolicy.Decide(r,true)==ShopScanDisposition.Continue&&ShopScanPolicy.Decide(r,true,true)==ShopScanDisposition.Continue&&ShopScanPolicy.Decide(r,false)==ShopScanDisposition.StopBatch,"Neutral changed source/continuation boundary");});
        Check("stored_sent_never_confirmed_or_replayed",()=>
        {
            string directory=Path.Combine(Path.GetTempPath(),"LC-V82-empty-progress",Guid.NewGuid().ToString("N"));
            var owner=new MarketOwner{Id=22,LocalIdentity="offline-owner",Name="離線店主",Position=new(100,101),ObservedAt=Epoch,
                ShopState=ShopEligibilityPolicy.ShopActionObserved,ActionType=".?AVShopAction@lineage@@",ShopStateSampledAt=Epoch,ShopStateEvidence=ShopEligibilityPolicy.ActionEvidence};
            var targets=new ShopTargetSnapshot{SnapshotId="offline-empty-targets",SourceSessionKey=Source.SessionKey,SelfId=11,SelfPosition=new(100,100),RefreshedAt=Epoch,Owners=[owner]};
            var store=new ShopScanProgressStore(directory);var plan=store.StartNew(Source,targets,forceDispatch:true);var page=new SavedScanPage(22,owner.LocalIdentity,ShopPageModes.ViewerBuy);
            store.BeginAttempt(page,Request.RequestId);store.NativeIntent(Request.RequestId);store.NativeSettled(Request.RequestId,true,true,"closed native lifecycle");store.Result(Request.RequestId,Response(Observe()!));
            var reloaded=new ShopScanProgressStore(directory);var view=reloaded.View(Source,targets,false,false,true)!;
            Need(view.SearchedPages==1&&view.ConfirmedPages==0&&view.UncertainPages==0&&reloaded.Continue(plan.PlanId,Source,targets,allowEmpty:true,forceDispatch:true).All(p=>p!=page),"Neutral was confirmed or replayable after reload");
        });
        Check("actual_broker_over_96_verified_history_keeps_exact_request_receipt",()=>VerifyBoundedBrokerReceipt().GetAwaiter().GetResult());
        Check("required_listing_survives_optional_byte_trimming",()=>
        {
            var required=Listing(Observe()!);var optional=required with{RequestId="optional",Detail=new string('x',HubProtocol.MaxMessageBytes)};
            var response=Response(Observe()!) with{Listings=[required,optional]};
            var fitted=ShopResponseBudget.Fit(response,requiredListing:required);
            Need(fitted.Listings.Length==1&&ReferenceEquals(fitted.Listings[0],required)&&fitted.ListingsTruncated
                &&ShopResponseBudget.MeasuredBytes(fitted)<=HubProtocol.MaxMessageBytes-ShopResponseBudget.RuntimeReservedBytes,
                "Optional summaries displaced the required request receipt");
        });
        Check("required_listing_not_dropped_even_at_tail",()=>
        {
            var required=Listing(Observe()!);var optional=required with{RequestId="optional",Detail=new string('x',HubProtocol.MaxMessageBytes)};
            var fitted=ShopResponseBudget.Fit(Response(Observe()!) with{Listings=[optional,required]},requiredListing:required);
            Need(fitted.Listings.Length==1&&ReferenceEquals(fitted.Listings[0],required),"Tail receipt was dropped");
        });
        foreach(string defect in new[]{"missing","duplicate","wrong_request","substituted"})
            Check("required_listing_rejects_"+defect,()=>
            {
                var required=Listing(Observe()!);var response=Response(Observe()!) with{Listings=[required]};
                response=defect switch{"missing"=>response with{Listings=[]},"duplicate"=>response with{Listings=[required,required]},
                    "wrong_request"=>response with{RequestId="other"},_=>response with{Listings=[required with{}]}};
                bool rejected=false;try{ShopResponseBudget.Fit(response,requiredListing:required);}catch(InvalidDataException){rejected=true;}
                Need(rejected,"Invalid required receipt binding passed");
            });
        Check("oversize_required_listing_fails_without_erasing_receipt",()=>
        {
            var required=Listing(Observe()!) with{Detail=new string('x',HubProtocol.MaxMessageBytes)};
            var response=Response(Observe()!) with{Listings=[required]};bool rejected=false;
            try{ShopResponseBudget.Fit(response,requiredListing:required);}catch(ShopRequestFailure){rejected=true;}
            Need(rejected&&response.Listings.Length==1&&ReferenceEquals(response.Listings[0],required),"Oversize receipt silently disappeared or byte ceiling changed");
        });
        return checks.ToArray();
    }
    private static async Task VerifyBoundedBrokerReceipt()
    {
        string root=Path.Combine(Path.GetTempPath(),"LC-V82-page-receipt",Guid.NewGuid().ToString("N"));
        var fake=new NoReadSource();
        await using var runtime=new HubRuntime(fake,Path.Combine(root,"runtime"),enableTransport:false);runtime.SetLogging(false);
        using var reader=new CurrentLcSource(profileDirectory:Path.Combine(root,"absent-profile"));
        await using var broker=new ShopBroker(runtime,reader,Path.Combine(root,"shops"));
        const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        var remember=typeof(ShopBroker).GetMethod("RememberListing",hidden)!;
        var state=typeof(ShopBroker).GetMethod("State",hidden)!;
        var seed=Listing(Observe()!);
        for(int i=0;i<100;i++)remember.Invoke(broker,[seed with{RequestId="old-"+i,Revision="revision-"+i,
            Owner=new(){Id=(ulong)(100+i),LocalIdentity="old-owner-"+i},CapturedAt=Epoch.AddSeconds(-100+i),
            State="OwnerAndResponseMatched",OwnerAndResponseMatched=true,Complete=true,EmptyPageObserved=false,EmptyPageObservation=null}]);
        var retained=(List<MarketListing>)typeof(ShopBroker).GetField("listings",hidden)!.GetValue(broker)!;
        Need(retained.Count==96&&retained.All(value=>value.OwnerAndResponseMatched),"Existing history ceiling/policy changed");
        var before=retained.ToArray();remember.Invoke(broker,[seed]);
        Need(retained.SequenceEqual(before)&&!retained.Contains(seed),"Neutral modified verified history retention");
        var response=(ShopCommandResponse)state.Invoke(broker,[seed.RequestId,seed.State,seed.Detail,true,null,seed])!;
        Need(response.Listings.Length==32&&ReferenceEquals(response.Listings[0],seed)
            &&response.Listings.Count(value=>value.RequestId==seed.RequestId)==1&&response.ListingsTruncated,
            "Current immutable receipt was lost after bounded history eviction");
        var neutral=Response(seed.EmptyPageObservation!) with{Listings=response.Listings,ListingsTruncated=response.ListingsTruncated};
        using var wire=new MemoryStream();await ShopCommandWire.WriteAsync(wire,neutral,default);wire.Position=0;
        var roundtrip=await ShopCommandWire.ReadAsync<ShopCommandResponse>(wire,default);
        Need(ShopEmptyPageRules.IsNeutralResponse(roundtrip)&&ShopEmptyPageRules.IsNeutralListing(roundtrip.Listings[0])
            &&JsonSerializer.Serialize(seed,HubProtocol.Json)==JsonSerializer.Serialize(roundtrip.Listings[0],HubProtocol.Json)
            &&!roundtrip.ResponseVerified&&!roundtrip.Listings[0].OwnerAndResponseMatched&&!roundtrip.Listings[0].PageEmpty,
            "Wire/JSON promoted or altered the neutral receipt");
        Need(fake.Reads==0&&!runtime.IsRunning,"Offline receipt test read LC");
    }
    private sealed class NoReadSource:IMemorySource
    {internal int Reads;public ReadResult Read(CancellationToken token){Reads++;throw new InvalidOperationException("Receipt test must not read LC");}public void Dispose(){}}
    internal static int RunReport(string output)
    {
        bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if(!admin)throw new InvalidOperationException("ADMIN_REQUIRED");
        if(File.Exists(output))throw new IOException("New report output required");
        var checks=Run();string assembly=typeof(EmptyPageObservationVerification).Assembly.Location;
        var report=new{Success=checks.All(c=>c.Passed),IsAdministrator=admin,Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),Checks=checks,
            ExecutedAssembly=assembly,ExecutedAssemblySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),LCRead=0,GameInput=0,NativeCalls=0};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return report.Success?0:1;
    }
    private static void Need(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
}
