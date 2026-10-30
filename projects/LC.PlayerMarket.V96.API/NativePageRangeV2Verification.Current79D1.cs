using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static partial class NativePageRangeV2Verification
{
    private static void Current79D1Checks(Action<string,Action> check)
    {
        var old=(SourceIdentity)Field("Source"); ulong image=(ulong)Field("Image"),target=(ulong)Field("Target"),hwnd=(ulong)Field("Hwnd"),shop=(ulong)Field("Shop"),owner=(ulong)Field("Owner");var epoch=(DateTimeOffset)Field("Epoch");
        var source=old with{DiskSha256=ShopNativeLegacy79D1Package.SupportedSha,PeTimestamp=ShopNativeLegacy79D1Package.SupportedTimestamp,ImageSize=ShopNativeLegacy79D1Package.SupportedImageSize,SessionKey="synthetic-current79D1-projection-not-live"};
        var map=new Dictionary<ulong,ulong>{{0x3FC90,0x3FD40},{0x287910,0x287BB0},{0x277CF0,0x277F90},{0x72D300,0x731AA0},{0x76BEB0,0x7710F0},{0x73B790,0x740090},{0x104870,0x104CA0},{0x3EC5C1C,0x3EDAF34},{0x15371DC,0x1544EC4}};
        void Translate(JsonNode? node)
        {
            if(node is JsonObject obj)foreach(var pair in obj.ToArray())
            {
                if(pair.Value is JsonValue value)
                {
                    if(value.TryGetValue<string>(out var text))
                    {
                        if(text==old.DiskSha256)obj[pair.Key]=source.DiskSha256;
                        else if(text==ShopNativeColdPolicy.Id)obj[pair.Key]=ShopNativeCurrent79D1Policy.Id;
                        else if(text==ShopNativeColdPolicy.CertificateSha256)obj[pair.Key]=ShopNativeCurrent79D1Policy.CertificateSha256;
                        else if(text==ShopNativeV17Contract.Policy)obj[pair.Key]=ShopNative79D1Contract.Policy;
                        else if(text==ShopNativeHistoricalPackage.NativePayloadSha256)obj[pair.Key]=ShopNativeLegacy79D1Package.NativePayloadSha256;
                        else if(text.StartsWith("0x")&&ulong.TryParse(text.AsSpan(2),System.Globalization.NumberStyles.HexNumber,null,out var rva)&&map.TryGetValue(rva,out var next))obj[pair.Key]=$"0x{next:X}";
                    }
                    else if(value.TryGetValue<ulong>(out var number)&&number>=image&&map.TryGetValue(number-image,out var next))obj[pair.Key]=image+next;
                }
                Translate(obj[pair.Key]);
            }
            else if(node is JsonArray array)foreach(var item in array)Translate(item);
        }
        List<JsonObject> Projection(bool native)
        {
            var rows=Modern(native?BaseFixture("Native",0u,0u,false):BaseFixture("Readiness",0u,false),image);
            foreach(var row in rows)Translate(row);
            var identity=Data(rows,"SOURCE");identity["Timestamp"]=$"0x{source.PeTimestamp:X}";identity["ImageSize"]=$"0x{source.ImageSize:X}";
            foreach(var row in rows)
            {
                var d=row["Data"]!.AsObject();
                if(d.ContainsKey("MaximumDeferredMask"))d["MaximumDeferredMask"]=0;
                if(Kind(row)=="PREFLIGHT_AB_CLOSED")
                {
                    var ranges=new JsonArray();string stage=d["PreflightStage"]!.GetValue<string>();
                    foreach(var spec in ShopNative79D1Contract.RequestedRanges)ranges.Add(Range(new ShopNativeV17Contract.RangeSpec(spec.Rva,spec.Length,spec.Sha256),image,0,stage));
                    d["RequestedCodeRanges"]=ranges;
                }
            }
            if(native)
            {
                // Filled from exact newly compiled current contract, never from live fixture history.
                var expected=new (string Rva,int Length,bool Metadata,string Sha256)[]{("0x3EDAF34",12,true,"F9B3A9814DD15C9D889B841C437EF93A9647608A7BF37BEB787768E5295D72A7"),("0x1544EC4",12,true,"39455C244CD3D4DE53471A0ADEC96067C74300DDC0BA6DB9733FE8F8E3A9FFC4"),("0x731AA0",91,false,"0B5262EFE80D968E1041568C6C0EC6CC641FCE8DAED2377192F7B9744CD80817"),("0x7710F0",67,false,"E8970D042A2CEE49CA238F302E102D5B00298E2CD31805D20EC4DB0BC41ECE4B")};
                var proofs=Data(rows,"EVENT_RESULT")["PostProof"]!.AsArray();Need(proofs.Count==expected.Length,"Immediate post-proof shape differs");
                for(int i=0;i<expected.Length;i++)
                {
                    var (rva,length,metadata,sha)=expected[i];var proof=proofs[i]!.AsObject();ulong relative=Convert.ToUInt64(rva[2..],16);
                    proof["Rva"]=rva;proof["Length"]=length;proof["Metadata"]=metadata;proof["CurrentSha256"]=sha;
                    var page=new JsonObject{["Base"]=(image+relative)&~4095UL,["AllocationBase"]=image,["AllocationProtect"]=128,["PartitionId"]=0,["Size"]=4096,["State"]=4096,["Protect"]=metadata?2:32,["Type"]=16777216};proof["Before"]=page;proof["After"]=page.DeepClone();
                }
            }
            return rows;
        }
        ShopNativeReadiness Ready(List<JsonObject> rows)=>ShopNativeReadinessProof.Parse(SyntheticText(rows),source,hwnd,shop,owner,0,epoch,epoch.AddMilliseconds(950),"synthetic-current79D1-not-live");
        ShopNativeOutcome Native(List<JsonObject> rows)=>ShopNativeOutcome.Parse(SyntheticText(rows),source,image,target,0);
        check("current79D1_readiness_strict_current_ranges",()=>{var p=Ready(Projection(false));Need(p.Ready&&p.Code=="NativeReady"&&p.PendingMask==0&&p.CurrentRuntimeCodeVerified&&p.ColdCodePolicyId==ShopNativeCurrent79D1Policy.Id,p.Detail);});
        check("current79D1_returned_exact_current_contract",()=>{var p=Native(Projection(true));Need(p.SafeToSettle&&p.Entered&&p.Returned&&p.SourceMatched&&p.Failure is null,p.Detail);});
        foreach(uint mask in new uint[]{1,3})
        {
            uint m=mask;
            check("current79D1_reject_host_mask_"+m,()=>{var rows=Projection(true);foreach(string kind in new[]{"EVENT_RESULT","NATIVE_FINAL_STATE","END"}){var d=Data(rows,kind);var n=kind=="END"?d["NativeResult"]!:d["State"]!;n["HostDeferredMask"]=m;}Need(!Native(rows).SafeToSettle,"Current host mask admitted");});
            check("current79D1_reject_native_mask_"+m,()=>{var rows=Projection(true);foreach(string kind in new[]{"EVENT_RESULT","NATIVE_FINAL_STATE","END"}){var d=Data(rows,kind);var n=kind=="END"?d["NativeResult"]!:d["State"]!;n["NativeDeferredMask"]=m;}Need(!Native(rows).SafeToSettle,"Current native mask admitted");});
            check("current79D1_reject_preflight_mask_"+m,()=>{var rows=Projection(false);Data(rows,"CURRENT_NATIVE_CODE_GUARDS")["DeferredMask"]=m;Need(!Ready(rows).Ready,"Current preflight mask admitted");});
        }
        foreach(string mutation in new[]{"old_policy","old_certificate","old_rva","missing_cloner_second_page","fabricated_pending"})
        {
            string key=mutation;check("current79D1_reject_"+key,()=>{var rows=Projection(false);
                switch(key){case "old_policy":Data(rows,"START")["DeferredCodePolicy"]=ShopNativeV17Contract.Policy;break;
                    case "old_certificate":Data(rows,"START")["ColdCodeCertificateSha256"]=ShopNativeColdPolicy.CertificateSha256;break;
                    case "old_rva":Data(rows,"PREFLIGHT_AB_CLOSED")["PacketCreate"]=image+0x72D300;break;
                    case "missing_cloner_second_page":Data(rows,"PREFLIGHT_AB_CLOSED")["RequestedCodeRanges"]!.AsArray().RemoveAt(4);break;
                    default:rows.Insert(2,new JsonObject{["Kind"]="PACKET_CODE_PENDING",["Data"]=new JsonObject{["Bit"]=1,["Rva"]="0x731AA0"}});break;}
                Need(!Ready(rows).Ready,"Current strict readiness admitted "+key);});
        }
        check("current79D1_reject_old_post_sha",()=>{var rows=Projection(true);Data(rows,"EVENT_RESULT")["PostProof"]![2]!["CurrentSha256"]=new string('0',64);Need(!Native(rows).SafeToSettle,"Old/unknown post body settled");});
        bool DirectCurrentEvidence(List<JsonObject> rows)
        {
            using var end=JsonDocument.Parse(Data(rows,"END").ToJsonString());
            try{return (bool)typeof(ShopNative79D1Outcome).GetMethod("ConfirmedV17CurrentEvidence",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,[SyntheticText(rows),source,image,target,end.RootElement.GetProperty("NativeResult"),false])!;}
            catch(System.Reflection.TargetInvocationException error)when(error.InnerException is InvalidDataException){return false;}
        }
        check("current79D1_direct_recovery_evidence_warm_baseline",()=>Need(DirectCurrentEvidence(Projection(true)),"Exact source-specific evidence failed at direct recovery entry"));
        foreach(uint mask in new uint[]{1,3})
        {
            uint m=mask;check("current79D1_direct_recovery_reject_native_mask_"+m,()=>{var rows=Projection(true);foreach(string kind in new[]{"EVENT_RESULT","NATIVE_FINAL_STATE","END"}){var d=Data(rows,kind);var n=kind=="END"?d["NativeResult"]!:d["State"]!;n["NativeDeferredMask"]=m;}Need(!DirectCurrentEvidence(rows),"Direct recovery evidence bypassed maximum mask zero");});
        }
        check("current79D1_recovery_cannot_early_return_untagged_legacy",()=>{
            using var receipt=JsonDocument.Parse("{}");
            try{ShopNativeOutcome.ValidateV17RecoveryOriginal("untagged legacy record\n",source,target,1,123,receipt.RootElement);throw new Exception("Current source skipped original native validation");}
            catch(InvalidDataException){}
        });
        check("nearby_single_target_never_select_unknown_or_nonshop_or_far",()=>{
            var now=DateTimeOffset.UtcNow;MarketOwner Owner(ulong id,int x,string state)=>new(){Id=id,LocalIdentity="i"+id,Position=new(x,10),ObservedAt=now,ShopState=state,ShopStateSampledAt=now,ShopStateEvidence=ShopEligibilityPolicy.ActionEvidence,ActionType=state==ShopEligibilityPolicy.ShopActionObserved?".?AVShopAction@lineage@@":".?AVBreathAction@lineage@@"};
            var snapshot=new ShopTargetSnapshot{CanDispatch=true,SelfId=1,SelfPosition=new(10,10),RefreshedAt=now,Owners=[Owner(2,10,ShopEligibilityPolicy.Unknown),Owner(3,11,ShopEligibilityPolicy.KnownOtherActionObserved),Owner(4,14,ShopEligibilityPolicy.ShopActionObserved),Owner(5,12,ShopEligibilityPolicy.ShopActionObserved)]};
            Need(OneNearbyShopVerification.SelectTarget(snapshot,now).Id==5,"Single target selection widened eligibility");
            try{OneNearbyShopVerification.SelectTarget(snapshot with{Owners=snapshot.Owners.Where(x=>x.Id!=5).ToArray()},now);throw new Exception("Unknown/far target accepted");}catch(InvalidOperationException){}
        });
    }
}
