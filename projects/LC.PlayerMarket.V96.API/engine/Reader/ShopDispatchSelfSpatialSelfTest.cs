using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace MemoryHub.Reader;

/// <summary>Production token/recovery/baseline validation with fake complete reads only; no process is opened.</summary>
public static class ShopDispatchSelfSpatialSelfTest
{
    private sealed record Pass(ReadResult Result,ShopActionSourceBinding? Binding);

    public static IReadOnlyList<ShopDispatchTimeoutCheck> Run()
    {
        var checks=new List<ShopDispatchTimeoutCheck>();
        var source=new SourceIdentity(4242,987654321,"offline-LC","0x100000",1,4096,new string('A',64),"offline-self-spatial");
        var now=new DateTimeOffset(2026,10,29,0,0,0,TimeSpan.Zero);
        string[] all=["selfId","position","nearbyPlayers","playerShopStates","personalShopListing"];
        void Check(string name,Action action)
        {
            try{action();checks.Add(new(name,true,"Actual token/recovery/AttachBaseline validation; fake reads; native/input/process=0"));}
            catch(Exception error){checks.Add(new(name,false,error.ToString()));}
        }
        static void Need(bool value,string detail="Assertion failed"){if(!value)throw new InvalidOperationException(detail);}
        Pass Good(int pass=1,DateTimeOffset? at=null)
        {
            var sampled=at??now;
            var baseline=new PersonalShopListing{SourceSessionKey=source.SessionKey,SampledAt=sampled,OwnerIdRaw=88,LayoutState="Absent",Complete=false};
            var binding=new ShopActionSourceBinding(source,sampled,0x50000UL+(ulong)pass*0x1000,88,0x60000,0x70000,0x80000,0x90000);
            var fields=new Dictionary<string,FieldReading>
            {
                ["selfId"]=FieldReading.Observed(11,sampled),
                ["position"]=FieldReading.Observed(new{x=100+pass,y=100},sampled),
                ["nearbyPlayers"]=FieldReading.Observed(new{Complete=false,Scope="SelectedDispatchTarget",SelectedOwnerId=22,FullIdentityCollectionVerified=true,
                    Items=new[]{new{id=22,localIdentity="owned-player",kind="Player",name="shop",nameState="Observed",position=new{x=101,y=100},positionState="Observed",sampledAt=sampled}}},sampled),
                ["playerShopStates"]=FieldReading.Observed(new{Complete=false,Scope="SelectedDispatchTarget",SelectedOwnerId=22,FullIdentityCollectionVerified=true,
                    Items=new[]{new{id=22,localIdentity="owned-player",actionType=".?AVShopAction@lineage@@",shopState=ShopEligibilityPolicy.ShopActionObserved,
                        shopStateSampledAt=sampled,shopStateEvidence=ShopEligibilityPolicy.ActionEvidence,shopStateDetail="fake exact current owner"}}},sampled),
                ["personalShopListing"]=FieldReading.Observed(baseline,sampled)
            };
            return new(new(source,fields,"Reading","closed fake read"),binding);
        }
        Pass Spatial(string code="SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED",int nativeError=0,int pass=1)
        {
            var sample=Good(pass);sample.Result.Fields["position"]=FieldReading.Unknown(code+"：original fake failure");
            // This is the exact production predicate called by CaptureGroup on
            // the original ProbeFailure, not a token fabricated from Unknown.
            return sample with{Result=sample.Result with{RetryableFields=SelfSpatialDiscoveryRetry.RetryableField("自身座標","position",code,nativeError)?["position"]:[]}};
        }
        Pass Actor(int pass=1)
        {
            var sample=Good(pass);
            foreach(string key in new[]{"nearbyPlayers","playerShopStates"})sample.Result.Fields[key]=FieldReading.Unknown("ACTOR_COLLECTION_CHANGED：fake exact collection");
            return sample with{Result=sample.Result with{RetryableFields=["nearbyPlayers","playerShopStates"]}};
        }
        ShopActionSourceBinding Attach(ShopActionSourceBinding? binding,ReadResult result)
        {
            // Invoke the actual pure method used by ResolveShopDispatchSource;
            // no CurrentLcSource instance or process reader is constructed.
            var method=typeof(CurrentLcSource).GetMethod("AttachBaselineListing",BindingFlags.Static|BindingFlags.NonPublic)!;
            try{return (ShopActionSourceBinding)method.Invoke(null,[binding,result,now])!;}
            catch(TargetInvocationException error)when(error.InnerException is not null)
            {ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
        }
        ReadResult Recover(Func<Pass> read,out Func<ShopActionSourceBinding?> lastBinding,Action? validate=null,
            CancellationToken token=default,Action<int,CancellationToken>? delay=null,Func<double>? elapsed=null)
        {
            ShopActionSourceBinding? captured=null;lastBinding=()=>captured;
            return ShopDispatchReadRecovery.Read(()=>
            {
                captured=null;var pass=read();
                ShopDispatchSourceValidation.RequireCurrent(pass.Result,source);
                captured=Attach(pass.Binding,pass.Result);
                return pass.Result;
            },source,validate??(()=>{}),token,_=>{},()=>now,elapsed??(()=>0),delay??((_,_)=>{}));
        }
        foreach(string code in new[]{"SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED","SELF_SPATIAL_ACTOR_DISCOVERY_CONTROL"})
            Check("dispatch_spatial_actual_token_accepts_"+code,()=>Need(SelfSpatialDiscoveryRetry.RetryableField("自身座標","position",code,0)
                && ShopDispatchReadRecovery.Eligible(Spatial(code).Result,source,now)));
        foreach(var (group,field,code,error) in new[]{("自身座標","position","SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED",5),
            ("自身座標","position","SELF_SPATIAL_SELF_UNIQUE",0),("自身座標","position","SELF_SPATIAL_SELF_OWNERSHIP",0),
            ("自身座標","position","SELF_SPATIAL_TYPE",0),("自身座標","position","GROUP_SOURCE_CHANGED",0),
            ("自身座標","position","PROCESS_CHANGED",0),("附近角色物件","position","SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED",0),
            ("自身座標","nearbyPlayers","SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED",0)})
            Check("dispatch_spatial_token_rejects_"+group+"_"+field+"_"+code+"_"+error,
                ()=>Need(!SelfSpatialDiscoveryRetry.RetryableField(group,field,code,error)));
        Check("dispatch_spatial_no_token_is_not_retryable_unknown",()=>
        {var sample=Spatial();Need(!ShopDispatchReadRecovery.Eligible(sample.Result with{RetryableFields=[]},source,now));});
        foreach(string mutation in new[]{"unknown-source","different-source","no-source-read","global-code","global-native-zero","global-native-error",
            "wrong-state","ambiguous-code","owner-code","position-has-value","position-has-timestamp","mixed-actor-fields","stale-self","stale-listing","null-self","undefined-listing"})
            Check("dispatch_spatial_closed_envelope_rejects_"+mutation,()=>
            {
                var sample=Spatial();var result=sample.Result;
                switch(mutation)
                {
                    case "unknown-source":result=result with{Source=null};break;
                    case "different-source":result=result with{Source=source with{CreationFileTime=99}};break;
                    case "no-source-read":result=result with{NoSourceRead=true};break;
                    case "global-code":result=result with{ReadFailureCode="GROUP_SOURCE_CHANGED"};break;
                    case "global-native-zero":result=result with{ReadFailureNativeError=0};break;
                    case "global-native-error":result=result with{ReadFailureNativeError=5};break;
                    case "wrong-state":result=result with{State="Unsupported"};break;
                    case "ambiguous-code":result.Fields["position"]=FieldReading.Unknown("SELF_SPATIAL_SELF_UNIQUE：bad");break;
                    case "owner-code":result.Fields["position"]=FieldReading.Unknown("SELF_SPATIAL_COMPONENT_OWNER：bad");break;
                    case "position-has-value":result.Fields["position"]=FieldReading.Observed(1,now) with{State="Unknown",SampledAt=null};break;
                    case "position-has-timestamp":result.Fields["position"]=result.Fields["position"] with{SampledAt=now};break;
                    case "mixed-actor-fields":result.Fields["nearbyPlayers"]=FieldReading.Unknown("ACTOR_COLLECTION_CHANGED：bad");result=result with{RetryableFields=["position","nearbyPlayers"]};break;
                    case "stale-self":result.Fields["selfId"]=FieldReading.Observed(11,now.AddSeconds(-3));break;
                    case "stale-listing":result.Fields["personalShopListing"]=result.Fields["personalShopListing"] with{SampledAt=now.AddSeconds(-3)};break;
                    case "null-self":result.Fields["selfId"]=FieldReading.Observed<object?>(null,now);break;
                    case "undefined-listing":result.Fields["personalShopListing"]=new("Observed",now,default(JsonElement),"","fake");break;
                }
                Need(!ShopDispatchReadRecovery.Eligible(result,source,now));
            });
        Check("dispatch_spatial_all_fresh_has_no_reason_to_retry",()=>Need(!ShopDispatchReadRecovery.Eligible(Good().Result,source,now)));
        Check("dispatch_spatial_and_actor_transients_can_share_original_source",()=>
        {
            var sample=Actor();sample.Result.Fields["position"]=Spatial().Result.Fields["position"];
            Need(ShopDispatchReadRecovery.Eligible(sample.Result with{RetryableFields=["position","nearbyPlayers","playerShopStates"]},source,now));
        });
        Check("dispatch_spatial_three_complete_passes_keep_only_final_binding_values_and_times",()=>
        {
            int reads=0;var start=now;
            try
            {
                Pass? final=null;
                var result=Recover(()=>
                {
                    reads++;var at=start.AddMilliseconds(reads*25);now=at;
                    return reads switch{1=>Spatial(pass:1),2=>Actor(pass:2),_=>final=Good(3,at)};
                },out var binding);
                Need(reads==3&&final is not null&&ReferenceEquals(result,final.Result));
                Need(all.All(key=>result.Fields[key].SampledAt==now)&&result.Fields["position"].Value!.Value.GetProperty("x").GetInt32()==103);
                var selected=binding()!;Need(selected.PlayerShopSubsystem==0x53000&&selected.SampledAt==now
                    &&selected.BaselineListing.SampledAt==now&&selected.Source==source&&selected.BaselineListing.SourceSessionKey==source.SessionKey);
            }
            finally{now=start;}
        });
        Check("dispatch_spatial_third_pass_missing_binding_cannot_reuse_an_older_root",()=>
        {
            int reads=0;Exception? error=null;
            try{Recover(()=>++reads<3?Spatial(pass:reads):Good(3) with{Binding=null},out _);}
            catch(Exception failure){error=failure;}
            Need(reads==3&&error is InvalidOperationException&&error.Message.Contains("SHOP_SOURCE_UNAVAILABLE"));
        });
        Check("dispatch_spatial_final_binding_and_baseline_timestamp_mismatch_rejected",()=>
        {
            int reads=0;Exception? error=null;var start=now;
            try
            {
                var old=Good().Binding;
                try{Recover(()=>{reads++;if(reads==1)return Spatial();now=start.AddMilliseconds(100);return Good(2) with{Binding=old};},out _);}
                catch(Exception failure){error=failure;}
                Need(reads==2&&error is InvalidOperationException&&error.Message.Contains("SHOP_SOURCE_UNAVAILABLE"));
            }
            finally{now=start;}
        });
        Check("dispatch_spatial_exhaustion_is_typed_original_code_and_three_passes",()=>
        {
            int reads=0,delays=0;Exception? error=null;
            try{Recover(()=>{reads++;return Spatial();},out _,delay:(_,_)=>delays++);}
            catch(Exception failure){error=failure;}
            Need(reads==3&&delays==2&&error is ShopDispatchCollectionChangedException changed&&changed.Source==source
                &&error.Message.StartsWith("ACTOR_COLLECTION_CHANGED：")&&error.Message.Contains("SELF_SPATIAL_ACTOR_DISCOVERY_CHANGED")&&error.Message.Contains("position="));
        });
        Check("dispatch_spatial_four_second_admission_prevents_extra_complete_pass",()=>
        {
            int reads=0,delays=0;double elapsed=0;Exception? error=null;
            try{Recover(()=>{reads++;return Spatial();},out _,elapsed:()=>elapsed,delay:(_,_)=>{delays++;elapsed=4000;});}
            catch(Exception failure){error=failure;}
            Need(reads==1&&delays==1&&error is ShopDispatchCollectionChangedException);
        });
        Check("dispatch_spatial_expired_other_fields_do_not_gain_freshness_during_delay",()=>
        {
            int reads=0;var start=now;
            try
            {
                var result=Recover(()=>{reads++;return Spatial();},out _,delay:(_,_)=>now=now.AddSeconds(3));
                Need(reads==1&&!result.Fields["selfId"].Fresh(now)&&result.Fields["position"] is{State:"Unknown",Value:null,SampledAt:null});
            }
            finally{now=start;}
        });
        foreach(string change in new[]{"source","configuration"})
            Check("dispatch_spatial_"+change+"_change_before_next_pass_stops",()=>
            {
                int reads=0;bool invalid=false;Exception? error=null;
                try{Recover(()=>{reads++;return Spatial();},out _,validate:()=>{if(invalid)throw new InvalidOperationException(change=="source"?"SHOP_SOURCE_CHANGED":"SHOP_CONFIGURATION_CHANGED");},delay:(_,_)=>invalid=true);}
                catch(Exception failure){error=failure;}
                Need(reads==1&&error is InvalidOperationException&&error is not ShopDispatchCollectionChangedException);
            });
        Check("dispatch_spatial_different_complete_source_on_second_pass_stops",()=>
        {
            int reads=0;Exception? error=null;
            try{Recover(()=>++reads==1?Spatial():Good(2) with{Result=Good(2).Result with{Source=source with{SessionKey="other"}}},out _);}
            catch(Exception failure){error=failure;}
            Need(reads==2&&error is InvalidOperationException&&error.Message.StartsWith("SHOP_SOURCE_CHANGED"));
        });
        Check("dispatch_spatial_final_source_closure_failure_never_retries",()=>
        {
            int reads=0;Exception? error=null;
            try{Recover(()=>{reads++;var sample=Spatial();return sample with{Result=sample.Result with{Source=null,ReadFailureCode="GROUP_SOURCE_CHANGED",ReadFailureNativeError=0}};},out _);}
            catch(Exception failure){error=failure;}
            Need(reads==1&&error is InvalidOperationException&&error is not ShopDispatchCollectionChangedException);
        });
        Check("dispatch_spatial_external_cancel_during_delay_prevents_next_read",()=>
        {
            using var stop=new CancellationTokenSource();int reads=0;bool cancelled=false;
            try{Recover(()=>{reads++;return Spatial();},out _,token:stop.Token,delay:(_,_)=>stop.Cancel());}
            catch(OperationCanceledException){cancelled=true;}
            Need(reads==1&&cancelled);
        });
        Check("dispatch_spatial_external_cancel_wins_over_typed_read_timeout",()=>
        {
            using var stop=new CancellationTokenSource();int reads=0;bool cancelled=false;
            try{Recover(()=>{reads++;stop.Cancel();throw new ShopDispatchReadTimeoutException(source,0,0,"fake finished deadline");},out _,token:stop.Token);}
            catch(OperationCanceledException){cancelled=true;}
            Need(reads==1&&cancelled);
        });
        return checks;
    }
}
