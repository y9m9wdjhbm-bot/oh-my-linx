using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Explicit synthetic V17 projections of a retained research82 receipt. Never a V17 live attestation.</summary>
internal static class NativeCertifiedColdV17Verification
{
    private const string PolicyId="38A7-CreateCopy-NOACCESS-ColdCertified-v17";
    private const string Certificate="6FBE2B8DEB6AB0082BCB5A5D3162E22DC0E2B141088D2C5957A6637965458FCE";
    private const string Policy="Certified 38A7 Create/Copy NOACCESS only; policy 38A7-CreateCopy-NOACCESS-ColdCertified-v17; maximum mask3; current metadata/other full bodies/source AB; immediate strict full postbody; unknown retains pending";
    private const string LegacyPolicy="Current 38A7 exact seven A/B bodies; every invoked body must be currently readable/executable; no deferred NOACCESS call; immediate strict full postbody; maxmask0";
    private const string Build="38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1";
    private static readonly DateTimeOffset Epoch=new(2026,10,16,20,0,0,TimeSpan.Zero);
    private static readonly SourceIdentity Source=new(5408,134366508555397744,@"C:\Program Files (x86)\NCSOFT\Lineage Classic\LC.exe","0x7FF7DB2E0000",0x6ACEDB9A,0x3F5F000,Build,"synthetic-v17-not-current-live");
    private const ulong Image=0x7FF7DB2E0000, Hwnd=263744, Shop=2687142035360, Owner=ulong.MaxValue, Target=89985545;

    internal static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action body)
        {try{body();checks.Add(new("native_certified_cold_v17_"+name,true,"Synthetic parser receipt only; research82-derived fixture is not V17 live evidence; no LC/helper/native execution."));}
         catch(Exception error){checks.Add(new("native_certified_cold_v17_"+name,false,error.ToString()));}}
        ShopNativeOutcome Parse(List<JsonObject> rows,SourceIdentity? source=null,int exit=0,ShopNativeLogPurpose purpose=ShopNativeLogPurpose.NativeRequest)
            =>ShopNativeOutcome.Parse(Text(rows),source??Source,Image,Target,exit,purpose);
        ShopNativeReadiness Ready(List<JsonObject> rows,SourceIdentity? source=null,DateTimeOffset? now=null)
            =>ShopNativeReadinessProof.Parse(Text(rows),source??Source,Hwnd,Shop,Owner,0,Epoch,now??Epoch.AddMilliseconds(500),"synthetic-v17-probe");
        void NativeReject(string name,Action<List<JsonObject>> mutate)
            =>Check("native_reject_"+name,()=>{var rows=Native();mutate(rows);Need(!Parse(rows).SafeToSettle,"Invalid V17 native receipt settled: "+name);});
        void ReadyReject(string name,Action<List<JsonObject>> mutate)
            =>Check("readiness_reject_"+name,()=>{var rows=Readiness(3);mutate(rows);Need(!Ready(rows).Ready,"Invalid V17 readiness granted: "+name);});

        for(uint mask=0;mask<=3;mask++)
        {
            uint value=mask;
            Check("native_returned_exact_mask_"+mask,()=>{var rows=Native(value,value);var parsed=Parse(rows);Need(parsed.SafeToSettle&&parsed.Entered&&parsed.Returned&&parsed.SourceMatched&&parsed.Failure is null&&!parsed.NoEntryConfirmed,parsed.Detail);});
            Check("readiness_exact_mask_"+mask,()=>{var parsed=Ready(Readiness(value));Need(parsed.Ready&&parsed.ReadOnly&&parsed.NativeRequests==0&&parsed.Source==Source&&parsed.Fresh(Epoch.AddMilliseconds(500)),parsed.Detail);});
        }
        Check("native_host_cold_native_warm_with_strict_post",()=>{var result=Parse(Native(3,0));Need(result.SafeToSettle&&result.Returned,result.Detail);});
        Check("legacy16_warm_baseline_still_settles",()=>{var result=Parse(Native(0,0,true));Need(result.SafeToSettle&&result.Returned,result.Detail);});
        Check("legacy16_readiness_warm_stays_ready",()=>Need(Ready(Readiness(0,true)).Ready,"Legacy warm receipt rejected"));
        for(uint mask=1;mask<=3;mask++)
        {
            uint value=mask;
            Check("legacy16_nonzero_native_mask_rejected_"+mask,()=>Need(!Parse(Native(value,value,true)).SafeToSettle,"Legacy16 accepted cold mask"));
            Check("legacy16_nonzero_readiness_mask_rejected_"+mask,()=>Need(!Ready(Readiness(value,true)).Ready,"Legacy16 readonly accepted cold mask"));
        }
        foreach(var (key,value) in new[]{("Version","17.0.0"),("ManagedProgramVersion","16.0.2"),("NativePayloadVersion","15.0.0"),("ColdCodePolicyId","wrong-policy"),("ColdCodeCertificateSha256",new string('0',64)),("DeferredCodePolicy",LegacyPolicy)})
        {
            string k=key,v=value;
            NativeReject("start_"+k,r=>Data(r,"START")[k]=v);
            ReadyReject("start_"+k,r=>Data(r,"START")[k]=v);
        }
        foreach(var (key,value) in new[]{("NativeProtocolVersion",82),("NativeGuardContract",15),("MaximumDeferredMask",7)})
        {
            string k=key;int v=value;
            NativeReject("tuple_"+k,r=>Data(r,"START")[k]=v);
            ReadyReject("tuple_"+k,r=>Data(r,"START")[k]=v);
        }
        NativeReject("missing_start_certificate",r=>Data(r,"START").Remove("ColdCodeCertificateSha256"));
        ReadyReject("missing_start_certificate",r=>Data(r,"START").Remove("ColdCodeCertificateSha256"));
        NativeReject("end_certificate",r=>Data(r,"END")["ColdCodeCertificateSha256"]=new string('A',64));
        ReadyReject("end_certificate",r=>Data(r,"END")["ColdCodeCertificateSha256"]=new string('A',64));
        NativeReject("wrong_source_build",r=>Data(r,"SOURCE")["Sha256"]=new string('A',64));
        ReadyReject("wrong_source_build",r=>Data(r,"SOURCE")["Sha256"]=new string('A',64));
        Check("unsupported_bound_build_cannot_settle",()=>Need(!Parse(Native(),Source with{DiskSha256=new string('A',64)}).SafeToSettle,"Wrong expected build accepted"));
        Check("unsupported_bound_build_not_ready",()=>Need(!Ready(Readiness(3),Source with{DiskSha256=new string('A',64)}).Ready,"Wrong bound build ready"));
        NativeReject("wrong_source_creation",r=>Data(r,"SOURCE")["Creation"]=Source.CreationFileTime+1);
        NativeReject("wrong_target",r=>Data(r,"SOURCE")["TargetOwner"]=Target+1);
        ReadyReject("wrong_window",r=>Data(r,"SOURCE")["Hwnd"]=Hwnd+1);
        foreach(uint mask in new uint[]{4,7,uint.MaxValue})
        {
            uint value=mask;
            NativeReject("host_mask_"+mask,r=>SetState(r,"HostDeferredMask",value));
            NativeReject("native_mask_"+mask,r=>SetState(r,"NativeDeferredMask",value));
            ReadyReject("mask_"+mask,r=>Data(r,"PREFLIGHT_AB_CLOSED")["DeferredMask"]=value);
        }
        NativeReject("missing_preflight_round",r=>r.Remove(r.Last(x=>Kind(x)=="PREFLIGHT_AB_CLOSED")));
        NativeReject("second_round_certificate",r=>AllData(r,"PREFLIGHT_AB_CLOSED")[1]["ColdCodeCertificateSha256"]=new string('B',64));
        NativeReject("final_host_mask_disagrees",r=>AllData(r,"PREFLIGHT_AB_CLOSED")[^1]["DeferredMask"]=1);
        NativeReject("missing_pending_page",r=>r.Remove(r.First(x=>Kind(x)=="PACKET_CODE_PENDING")));
        NativeReject("pending_readable_page_cannot_defer",r=>Data(r,"PACKET_CODE_PENDING")["Page"]!["Protect"]=32);
        NativeReject("pending_foreign_allocation",r=>Data(r,"PACKET_CODE_PENDING")["Page"]!["AllocationBase"]=Image+4096);
        NativeReject("pending_body_short",r=>Data(r,"PACKET_CODE_PENDING")["Length"]=90);
        NativeReject("pending_current_bytes_claim",r=>Data(r,"PACKET_CODE_PENDING")["CurrentBytesMatched"]=true);
        NativeReject("pending_certificate",r=>Data(r,"PACKET_CODE_PENDING")["ColdCodeCertificateSha256"]=new string('C',64));
        foreach(string flag in new[]{"PostCodeConfirmed","EventResultDurable","BorrowedSourceContractVerified","NativeAlreadyReturned","CleanupConfirmed"})
        {string f=flag;NativeReject("end_"+flag,r=>Data(r,"END")[f]=false);}
        NativeReject("pending_remains",r=>Data(r,"END")["SessionBlocked"]=true);
        NativeReject("native_lease_release_missing",r=>SetState(r,"LeaseReleaseCompleted",0));
        NativeReject("native_exception",r=>SetState(r,"Exception",0xC0000005u));
        NativeReject("still_active",r=>SetState(r,"Active",1));
        NativeReject("post_state_inconsistent",r=>Data(r,"EVENT_RESULT")["State"]!["NativeDeferredMask"]=0);
        NativeReject("event_result_missing",r=>r.Remove(r.Single(x=>Kind(x)=="EVENT_RESULT")));
        NativeReject("post_missing_body",r=>Data(r,"EVENT_RESULT")["PostProof"]!.AsArray().RemoveAt(3));
        NativeReject("post_duplicate_body",r=>Data(r,"EVENT_RESULT")["PostProof"]![3]=Data(r,"EVENT_RESULT")["PostProof"]![2]!.DeepClone());
        NativeReject("post_wrong_body_hash",r=>Data(r,"EVENT_RESULT")["PostProof"]![2]!["CurrentSha256"]=new string('0',64));
        NativeReject("post_wrong_metadata_hash",r=>Data(r,"EVENT_RESULT")["PostProof"]![0]!["CurrentSha256"]=new string('0',64));
        NativeReject("post_short_body",r=>Data(r,"EVENT_RESULT")["PostProof"]![2]!["Length"]=90);
        NativeReject("post_ab_changed",r=>Data(r,"EVENT_RESULT")["PostProof"]![2]!["After"]!["Protect"]=64);
        NativeReject("post_noaccess",r=>{Data(r,"EVENT_RESULT")["PostProof"]![2]!["Before"]!["Protect"]=1;Data(r,"EVENT_RESULT")["PostProof"]![2]!["After"]!["Protect"]=1;});
        NativeReject("post_not_matched",r=>Data(r,"EVENT_RESULT")["PostProof"]![2]!["Matched"]=false);
        NativeReject("post_read_after_durable_result",r=>Data(r,"EVENT_RESULT")["PostReadBeforeDurableResult"]=false);
        Check("native_action_receipt_cannot_be_readonly",()=>Need(!Parse(Native(),purpose:ShopNativeLogPurpose.ReadOnlyPreflight).SafeToSettle,"Action promoted to readonly"));
        foreach(var mutation in new (string Name,Action<List<JsonObject>> Change)[]{
            ("missing_pending",r=>r.Remove(r.First(x=>Kind(x)=="PACKET_CODE_PENDING"))),
            ("wrong_pending_bit",r=>Data(r,"PACKET_CODE_PENDING")["Bit"]=4),
            ("wrong_pending_rva",r=>Data(r,"PACKET_CODE_PENDING")["Rva"]="0x287910"),
            ("short_pending_span",r=>Data(r,"PACKET_CODE_PENDING")["Length"]=90),
            ("pending_private",r=>Data(r,"PACKET_CODE_PENDING")["Page"]!["Type"]=0x20000),
            ("pending_guard",r=>Data(r,"PACKET_CODE_PENDING")["Page"]!["Protect"]=0x101),
            ("pending_readable",r=>Data(r,"PACKET_CODE_PENDING")["Page"]!["Protect"]=32),
            ("pending_foreign_allocation",r=>Data(r,"PACKET_CODE_PENDING")["Page"]!["AllocationBase"]=Image+4096),
            ("pending_missing_page",r=>Data(r,"PACKET_CODE_PENDING").Remove("Page")),
            ("pending_current_match_claim",r=>Data(r,"PACKET_CODE_PENDING")["CurrentBytesMatched"]=true),
            ("pending_protection_write",r=>Data(r,"PACKET_CODE_PENDING")["ProtectionChanged"]=true),
            ("guard_byte_count",r=>Data(r,"CURRENT_NATIVE_CODE_GUARDS")["DeferredCodeBytes"]=157),
            ("closed_bytes_claimed",r=>Data(r,"PREFLIGHT_AB_CLOSED")["EverySynchronousGuardBodyCurrentlyMatched"]=true),
            ("passed_bytes_claimed",r=>Data(r,"READ_ONLY_PREFLIGHT_PASSED")["CurrentBytesMatched"]=true),
            ("passed_native_callable",r=>Data(r,"READ_ONLY_PREFLIGHT_PASSED")["NativeCallableVerified"]=true),
            ("passed_query",r=>Data(r,"READ_ONLY_PREFLIGHT_PASSED")["NativeRequests"]=1),
            ("passed_hook",r=>Data(r,"READ_ONLY_PREFLIGHT_PASSED")["HooksInstalled"]=1),
            ("missing_network_sample",r=>r.Remove(r.First(x=>Kind(x)=="NETWORK_SHARED_COUNTS_SAMPLE"))),
            ("cleanup_unknown",r=>Data(r,"END")["CleanupConfirmed"]=false),
            ("native_result",r=>Data(r,"END")["NativeResult"]=new JsonObject()),
            ("pending_block",r=>Data(r,"END")["SessionBlocked"]=true)
        })ReadyReject(mutation.Name,mutation.Change);
        Check("readonly_proof_expires",()=>Need(!Ready(Readiness(3),now:Epoch.AddSeconds(5)).Ready,"Old readiness authorized later work"));
        Check("readonly_not_reused_by_new_probe",()=>Need(!ShopNativeReadinessProof.Parse(Text(Readiness(3)),Source,Hwnd,Shop,Owner,0,Epoch.AddSeconds(1),Epoch.AddSeconds(2),"different-probe").Ready,"Previous launch accepted"));
        Check("readonly_truncated_end_rejected",()=>Need(!ShopNativeReadinessProof.Parse(Text(Readiness(3)).TrimEnd('\n'),Source,Hwnd,Shop,Owner,0,Epoch,Epoch.AddMilliseconds(500),"p").Ready,"Missing newline accepted"));
        for(uint mask=0;mask<=3;mask++)
        {
            uint value=mask;
            Check("recovery_exact_certified_mask_"+mask,()=>{var f=new RecoveryFixture(value);var proof=f.Read();Need(!proof.LifecycleOnly&&proof.TargetOwner==Target&&proof.Source==Source&&proof.Direction==ShopPageModes.ViewerBuy,"Recovery identity differed");});
        }
        Check("recovery_lifecycle_does_not_claim_completion",()=>{var f=new RecoveryFixture();f.Lifecycle();var proof=f.Read();Need(proof.LifecycleOnly&&proof.CompletionDetail.Contains("未確認",StringComparison.Ordinal),"Lifecycle promoted to query success");});
        void RecoveryReject(string name,Action<RecoveryFixture> mutate)
            =>Check("recovery_reject_"+name,()=>{var f=new RecoveryFixture();mutate(f);bool refused=false;try{_=f.Read();}catch(Exception e) when(e is InvalidDataException or JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException){refused=true;}Need(refused,"Invalid recovery receipt accepted: "+name);});
        foreach(string key in new[]{"ManagedProgramVersion","ColdCodePolicyId","ColdCodeCertificateSha256","MaximumDeferredMask"})
        {string k=key;RecoveryReject("missing_receipt_"+key,f=>{f.ReceiptNode.Remove(k);f.WriteReceipt();});}
        RecoveryReject("wrong_receipt_cert",f=>{f.ReceiptNode["ColdCodeCertificateSha256"]=new string('F',64);f.WriteReceipt();});
        RecoveryReject("wrong_receipt_policy",f=>{f.ReceiptNode["ColdCodePolicyId"]="wrong";f.WriteReceipt();});
        RecoveryReject("wrong_receipt_producer",f=>{f.ReceiptNode["ManagedProgramVersion"]="16.0.2";f.WriteReceipt();});
        RecoveryReject("receipt_mask4",f=>{f.ReceiptNode["MaximumDeferredMask"]=4;f.WriteReceipt();});
        RecoveryReject("original_wrong_producer",f=>f.ChangeLog(r=>Data(r,"START")["ManagedProgramVersion"]="16.0.2"));
        RecoveryReject("original_wrong_guard",f=>f.ChangeLog(r=>Data(r,"START")["NativeGuardContract"]=15));
        RecoveryReject("original_wrong_cert",f=>f.ChangeLog(r=>Data(r,"START")["ColdCodeCertificateSha256"]=new string('E',64)));
        RecoveryReject("original_wrong_terminal_cert",f=>f.ChangeLog(r=>Data(r,"END")["ColdCodeCertificateSha256"]=new string('D',64)));
        RecoveryReject("original_wrong_build",f=>f.ChangeLog(r=>Data(r,"SOURCE")["Sha256"]=new string('B',64)));
        RecoveryReject("original_mask4",f=>f.ChangeLog(r=>SetState(r,"NativeDeferredMask",4)));
        RecoveryReject("original_wrong_nonce",f=>{f.ReceiptNode["NativeNonce"]=1;f.WriteReceipt();});
        RecoveryReject("original_wrong_owner",f=>{f.ReceiptNode["TargetOwner"]=Target+1;f.WriteReceipt();});
        RecoveryReject("original_missing_preflight",f=>f.ChangeLog(r=>r.Remove(r.Last(x=>Kind(x)=="PREFLIGHT_AB_CLOSED"))));
        RecoveryReject("original_missing_pending_body",f=>f.ChangeLog(r=>r.Remove(r.First(x=>Kind(x)=="PACKET_CODE_PENDING"))));
        RecoveryReject("original_pending_readable",f=>f.ChangeLog(r=>Data(r,"PACKET_CODE_PENDING")["Page"]!["Protect"]=32));
        RecoveryReject("original_native_not_returned",f=>f.ChangeLog(r=>SetState(r,"Returned",0)));
        RecoveryReject("original_lease_not_released",f=>f.ChangeLog(r=>SetState(r,"LeaseReleaseCompleted",0)));
        RecoveryReject("original_still_active",f=>f.ChangeLog(r=>SetState(r,"Active",1)));
        RecoveryReject("original_cleanup_false",f=>f.ChangeLog(r=>Data(r,"END")["CleanupConfirmed"]=false));
        RecoveryReject("original_unhook_failed",f=>f.ChangeLog(r=>Data(r,"UNHOOK")["Success"]=false));
        RecoveryReject("original_unknown_error",f=>f.ChangeLog(r=>Data(r,"ERROR")["Code"]="Unknown"));
        RecoveryReject("original_no_durable_result",f=>f.ChangeLog(r=>Data(r,"END")["EventResultDurable"]=false));
        RecoveryReject("original_post_claim_substitution",f=>f.ChangeLog(r=>Data(r,"END")["PostCodeConfirmed"]=true));
        RecoveryReject("original_log_hash_changed",f=>File.AppendAllText(f.NativePath," "));
        RecoveryReject("archive_hash_changed",f=>File.AppendAllText(f.ForceArchive," "));
        RecoveryReject("shared_pending_still_exists",f=>File.WriteAllText(f.SinglePending,"synthetic unresolved fence"));
        RecoveryReject("receipt_claims_native_retry",f=>{f.ReceiptNode["NativeRequests"]=1;f.WriteReceipt();});
        RecoveryReject("receipt_claims_completion",f=>{f.ReceiptNode["OriginalRequestCompletionVerified"]=true;f.WriteReceipt();});
        RecoveryReject("fresh_proof_before_original_end",f=>{f.ReceiptNode["FreshProof"]!["StartedAt"]=Epoch.AddSeconds(-1).ToString("O");f.WriteReceipt();});
        RecoveryReject("fresh_proof_not_full",f=>{f.ReceiptNode["FreshProof"]!["AllCurrentBytesMatched"]=false;f.WriteReceipt();});
        RecoveryReject("lifecycle_mask4",f=>{f.Lifecycle();f.ReceiptNode["FreshProof"]!["UnavailableProtectedMask"]=4;f.WriteReceipt();});

        Check("installed_refusal_preserves_first_host_mask",()=>{var result=Parse(InstalledRefusal(),exit:1);Need(result.SafeToSettle&&result.SourceMatched&&result.NoEntryConfirmed&&!result.Entered&&!result.Returned,result.Detail);});
        Check("installed_refusal_wrong_host_mask_rejected",()=>{var rows=InstalledRefusal();Data(rows,"NATIVE_FINAL_STATE")["State"]!["HostDeferredMask"]=0;Need(!Parse(rows,exit:1).SafeToSettle,"Wrong installed host mask settled");});
        Check("installed_refusal_other_idle_field_nonzero_rejected",()=>{var rows=InstalledRefusal();Data(rows,"NATIVE_FINAL_STATE")["State"]!["Entered"]=1;Need(!Parse(rows,exit:1).SafeToSettle,"Entered state promoted to unpublished cleanup");});
        RecoveryReject("all_receipt_policy_fields_removed",f=>{foreach(string key in new[]{"ManagedProgramVersion","ColdCodePolicyId","ColdCodeCertificateSha256","MaximumDeferredMask"})f.ReceiptNode.Remove(key);f.WriteReceipt();});
        RecoveryReject("receipt_original_end_not_actual_end",f=>{f.ReceiptNode["OriginalEndAt"]=Epoch.AddSeconds(-1).ToString("O");f.WriteReceipt();});
        RecoveryReject("original_force_journal_not_receipt",f=>f.ChangeLog(r=>{string path=f.ForcePending.Replace("0123456789abcdef0123456789abcdef","fedcba9876543210fedcba9876543210");Data(r,"END")["PendingJournal"]=path;Data(r,"EVENT_INTENT_DURABLE")["Path"]=path;Data(r,"FORCE_DISPATCH")["CurrentPending"]=path;}));
        RecoveryReject("original_single_journal_not_receipt",f=>f.ChangeLog(r=>{string path=f.SinglePending+".different";Data(r,"END")["SingleTrialPendingJournal"]=path;Data(r,"EVENT_INTENT_DURABLE")["SingleTrialPending"]=path;}));
        Check("submit_two_pages_then_second_unavailable_settles",()=>{var result=Parse(InstalledRefusalWithSubmitRechecks(),exit:1);Need(result.SafeToSettle&&result.NoEntryConfirmed&&!result.Entered&&!result.Returned,result.Detail);});
        Check("submit_two_pages_restored_before_install_settles",()=>{var result=Parse(InstalledRefusalWithSubmitRechecks(true),exit:1);Need(result.SafeToSettle&&result.NoEntryConfirmed&&!result.Entered&&!result.Returned,result.Detail);});
        void SubmitReject(string name,Action<List<JsonObject>> mutation)
            =>Check("submit_two_pages_reject_"+name,()=>{var rows=InstalledRefusalWithSubmitRechecks();mutation(rows);Need(!Parse(rows,exit:1).SafeToSettle,"Invalid Submit slices settled: "+name);});
        SubmitReject("function_rva",r=>AllData(r,"SUBMIT_CODE_PREFLIGHT_RECHECK")[0]["FunctionRva"]="0x72D300");
        SubmitReject("slice_length",r=>AllData(r,"SUBMIT_CODE_PREFLIGHT_RECHECK")[1]["Length"]=1569);
        SubmitReject("before_span",r=>AllData(r,"SUBMIT_CODE_PREFLIGHT_RECHECK")[0]["Before"]!["Size"]=1776);
        SubmitReject("after_span",r=>AllData(r,"SUBMIT_CODE_PREFLIGHT_RECHECK")[1]["After"]!["Base"]=Image+0x289000);
        SubmitReject("reverse_order",r=>{int at=r.FindIndex(x=>Kind(x)=="SUBMIT_CODE_PREFLIGHT_RECHECK");(r[at],r[at+1])=(r[at+1],r[at]);});
        SubmitReject("duplicate_slice",r=>{var first=AllData(r,"SUBMIT_CODE_PREFLIGHT_RECHECK")[0];var second=AllData(r,"SUBMIT_CODE_PREFLIGHT_RECHECK")[1];foreach(string key in new[]{"Rva","Length","Before","After"})second[key]=first[key]!.DeepClone();});
        SubmitReject("third_record",r=>{int at=r.FindIndex(x=>Kind(x)=="SUBMIT_CODE_PREFLIGHT_RECHECK");r.Insert(at+1,r[at].DeepClone().AsObject());});
        SubmitReject("protection_changed",r=>AllData(r,"SUBMIT_CODE_PREFLIGHT_RECHECK")[0]["ProtectionChanged"]=true);
        return checks.ToArray();
    }

    private static List<JsonObject> Native(uint hostMask=3,uint nativeMask=3,bool legacy=false)
    {
        var rows=Research82.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonNode.Parse(s)!.AsObject()).ToList();
        rows.RemoveAll(r=>Kind(r)=="RESEARCH_TOKEN_CONSUMED");
        foreach(var row in rows)
        {
            var data=row["Data"]!.AsObject();
            if(Kind(row) is "START" or "PACKET_CODE_PENDING" or "CURRENT_NATIVE_CODE_GUARDS" or "PREFLIGHT_AB_CLOSED" or "END")Stamp(data);
            data.Remove("ResearchOnly");data.Remove("ExplicitColdTrial");
            if(Kind(row)=="PACKET_CODE_PENDING")
            {data.Remove("MaximumDeferredMask");data["Meaning"]="Synthetic V17 projection: current bytes unavailable; certified metadata/source contract and strict post required";}
            if(Kind(row)=="START")
            {data["Version"]="15.0.0";data["ManagedProgramVersion"]=legacy?"16.0.2":"17.0.0";data["NativePayloadVersion"]=legacy?"15.0.0":"17.0.0";data["NativeProtocolVersion"]=15;data["NativeGuardContract"]=legacy?15:17;data["DeferredCodePolicy"]=legacy?LegacyPolicy:Policy;data["ResearchOnly"]=true;data["Binding"]="Caller-supplied runtime identity, independently verified";data["ReturnedLifecycleRecoveryContract"]=1;data.Remove("ReturnedLifecycleRecoveryEnabled");data.Remove("ForceEnabled");data.Remove("ExplicitColdTrial");}
            if(Kind(row)=="NATIVE_PAYLOAD")
            {data["Path"]=legacy?@"D:\synthetic\LC_PlayerShopListActionHookV15.dll":@"D:\synthetic\LC_PlayerShopListActionHookV17.dll";data["Sha256"]=legacy?"124E5B0AD3D3D109C52BBC297670C9879A4655D4C67C2B9DFCEAD3BB9A7E836E":ShopNativeHistoricalPackage.NativePayloadSha256;data["Export"]=legacy?"PlayerShopListHook":"PlayerShopListHookV17";if(!legacy){Stamp(data);data.Remove("MaximumDeferredMask");}}
            if(Kind(row) is "CURRENT_NATIVE_CODE_GUARDS" or "PREFLIGHT_AB_CLOSED")
            {data["DeferredMask"]=hostMask;data["DeferredCodeBytes"]=DeferredBytes(hostMask);}
            if(Kind(row)=="PREFLIGHT_AB_CLOSED")
            {data["EverySynchronousGuardBodyCurrentlyMatched"]=hostMask==0;data["ColdCodeBytesPending"]=hostMask!=0;}
            if(Kind(row)=="END")foreach(string key in new[]{"ResearchOnly","ResearchTokenConsumed","ResearchTokenPath","ResearchTokenSha256"})data.Remove(key);
            if(legacy)foreach(string key in new[]{"ColdCodePolicyId","ColdCodeCertificateSha256","MaximumDeferredMask"})data.Remove(key);
        }
        rows.RemoveAll(r=>Kind(r)=="PACKET_CODE_PENDING"&&(r["Data"]!["Bit"]!.GetValue<uint>()&hostMask)==0);
        SetState(rows,"HostDeferredMask",hostMask);SetState(rows,"NativeDeferredMask",nativeMask);
        return rows;
    }
    private static List<JsonObject> InstalledRefusal()
    {
        var all=Native(3,3);int closure=all.FindIndex(r=>Kind(r)=="PREFLIGHT_AB_CLOSED");
        var rows=all.Take(closure+4).ToList(); // Complete first closure followed by install intent/payload/result.
        var page=new JsonObject{["Base"]=Image+0x287000,["AllocationBase"]=Image,["AllocationProtect"]=32,["PartitionId"]=0,["Size"]=4096,["State"]=0x1000,["Protect"]=1,["Type"]=0x1000000};
        rows.Add(new(){["Kind"]="NATIVE_CODE_PAGE_UNAVAILABLE",["Data"]=new JsonObject{["Rva"]="0x287910",["Page"]=page,["Phase"]="Preflight",["Code"]="NativeCodeUnavailable"}});
        rows.Add(new(){["Kind"]="ERROR",["Data"]=new JsonObject{["Type"]="NativePreflightException",["Code"]="NativeCodeUnavailable",["Phase"]="BeforePublish",["Rva"]="0x287910",["Message"]="Native code is not currently readable/executable MEM_IMAGE: 0x287910"}});
        foreach(string kind in new[]{"NATIVE_FINAL_STATE","UNHOOK","LOCAL_VIEW_RELEASE","LOCAL_MAPPING_CLOSE","LOCAL_LIBRARY_FREE","CLEANUP"})
            rows.Add(all.Single(r=>Kind(r)==kind).DeepClone().AsObject());
        var final=Data(rows,"NATIVE_FINAL_STATE");var state=final["State"]!.AsObject();
        foreach(string key in state.Select(p=>p.Key).ToArray())state[key]=0;
        state["HostDeferredMask"]=3;final["DisarmedBeforeClaim"]=false;
        var end=Data(all,"END").DeepClone().AsObject();end["ExitCode"]=1;end["NativeResult"]=null;
        foreach(string key in new[]{"PostCodeConfirmed","EventResultDurable","BorrowedSourceContractVerified","NativeAlreadyReturned","AckObserved","ListReceivedVerified","RemoteModuleUnloadProven","EntireHookTailExitProven","SessionBlocked"})end[key]=false;
        rows.Add(new(){["Kind"]="END",["Data"]=end});return rows;
    }

    private static JsonObject SubmitRecheck(uint rva,bool restored,int queries,int total)
    {
        var before=new JsonObject{["Base"]=Image+(rva&~4095u),["AllocationBase"]=Image,["AllocationProtect"]=32,["PartitionId"]=0,["Size"]=4096,["State"]=0x1000,["Protect"]=1,["Type"]=0x1000000};
        var after=before.DeepClone().AsObject();after["Protect"]=restored?32:1;
        return new(){["Kind"]="SUBMIT_CODE_PREFLIGHT_RECHECK",["Data"]=new JsonObject{
            ["FunctionRva"]="0x287910",["Rva"]=$"0x{rva:X}",["Length"]=rva==0x287910?1776:1570,["Before"]=before,["After"]=after,
            ["Attempted"]=true,["Queries"]=queries,["TotalQueries"]=total,["ElapsedMilliseconds"]=queries*25.0,["TotalWaitMilliseconds"]=total*25.0,
            ["Outcome"]=restored?"PageChanged":"BudgetExhausted",["MaximumWaitMilliseconds"]=250,["ReadOnly"]=true,["RequestNotPublished"]=true,
            ["ProtectionChanged"]=false,["Deferred"]=false,["CurrentBytesMatched"]=false,["FullByteAndSourceProofStillRequired"]=true}};
    }
    private static List<JsonObject> InstalledRefusalWithSubmitRechecks(bool beforeInstall=false)
    {
        var rows=InstalledRefusal();
        int at=rows.FindIndex(r=>Kind(r)==(beforeInstall?"PACKET_CODE_PENDING":"NATIVE_CODE_PAGE_UNAVAILABLE"));
        rows.Insert(at,SubmitRecheck(0x287910,true,1,1));rows.Insert(at+1,SubmitRecheck(0x288000,beforeInstall,beforeInstall?1:9,beforeInstall?2:10));
        if(!beforeInstall)
        {
            var unavailable=Data(rows,"NATIVE_CODE_PAGE_UNAVAILABLE");unavailable["Rva"]="0x288000";unavailable["Page"]!["Base"]=Image+0x288000;
            var error=Data(rows,"ERROR");error["Rva"]="0x288000";error["Message"]="Native code is not currently readable/executable MEM_IMAGE: 0x288000";
        }
        return rows;
    }

    private static List<JsonObject> Readiness(uint mask,bool legacy=false)
    {
        var source=Native(mask,mask,legacy);int closed=source.FindIndex(r=>Kind(r)=="PREFLIGHT_AB_CLOSED");
        var rows=source.Take(closed+1).ToList();
        var start=Data(rows,"START");start["Mode"]="--preflight";start["MaximumNativeRequests"]=0;
        Data(rows,"SOURCE")["TargetOwner"]=0;Data(rows,"PREFLIGHT_AB_CLOSED")["TargetOwner"]=0;
        var passed=new JsonObject{["NativeRequests"]=0,["HooksInstalled"]=0,["DeferredMask"]=mask,["NativeCallableVerified"]=false,["LiveShopQueryVerified"]=false,["CurrentBytesMatched"]=mask==0,["CurrentPendingBytesClaimedMatched"]=false};
        if(!legacy)Stamp(passed);
        rows.Add(new(){["Kind"]="READ_ONLY_PREFLIGHT_PASSED",["Data"]=passed});
        var end=new JsonObject{["ExitCode"]=0,["CleanupConfirmed"]=true,["SessionBlocked"]=false,["PostCodeConfirmed"]=false,["EventResultDurable"]=false,["BorrowedSourceContractVerified"]=false,["NativeAlreadyReturned"]=false,["AckObserved"]=false,["ListReceivedVerified"]=false,["NativeResult"]=null};
        if(!legacy)Stamp(end);
        rows.Add(new(){["Kind"]="END",["Data"]=end});return rows;
    }
    // Isolated file receipts only: no process/helper/shared product journal is touched.
    private sealed class RecoveryFixture
    {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"LC-V82-certified-cold-synthetic-"+Guid.NewGuid().ToString("N"));
        internal readonly string Journal,NativePath,Receipt,ForcePending,SinglePending,ForceArchive,SingleArchive;
        internal readonly JsonObject ReceiptNode;
        internal readonly List<JsonObject> Log;
        internal RecoveryFixture(uint mask=3)
        {
            Journal=Path.Combine(Root,"journal");Directory.CreateDirectory(Journal);Directory.CreateDirectory(Path.Combine(Root,"native"));
            string stem=Path.Combine(Journal,Source.ProcessId+"-"+Source.CreationFileTime);
            ForcePending=stem+".force-0123456789abcdef0123456789abcdef.pending.json";SinglePending=stem+".v15-singletrial.pending.json";
            ForceArchive=ForcePending[..^".pending.json".Length]+".resolved-test.json";SingleArchive=SinglePending[..^".pending.json".Length]+".resolved-test.json";
            NativePath=Path.Combine(Root,"native","0123456789abcdef0123456789abcdef.jsonl");Receipt=Path.Combine(Root,"committed-recovery.jsonl");
            Log=Native(mask,mask);
            Log.Insert(1,new(){["Kind"]="FORCE_DISPATCH",["Data"]=new JsonObject{["CurrentPending"]=ForcePending,["PreviousPending"]=stem+".pending.json",["WaitForPreviousGameState"]=false}});
            Log.RemoveAll(r=>Kind(r) is "PACKET_CODE_CONFIRMED" or "BORROWED_SOURCE_VERIFIED" or "JOURNAL_SETTLED" or "SINGLE_TRIAL_JOURNAL_SETTLED");
            SetState(Log,"PostCodeConfirmed",0);
            var result=Data(Log,"EVENT_RESULT");result["PostCodeConfirmed"]=false;result["PostProof"]=new JsonArray();result["PostCodeErrorCode"]="PostCodeUnconfirmed";result["PostCodeErrorRva"]="0x72D300";
            result["PostCodeRecheck"]=new JsonObject{["Contract"]=1,["ReadOnly"]=true,["NativeRequestsRetried"]=0,["Outcome"]="BudgetExhausted",["Attempts"]=2,["TemporaryUnavailableObservations"]=2,["ElapsedMilliseconds"]=1000.0};
            int at=Log.FindIndex(r=>Kind(r)=="EVENT_RESULT");
            Log.Insert(at+1,new(){["Kind"]="ERROR",["Data"]=new JsonObject{["Type"]="NativePreflightException",["Code"]="PostCodeUnconfirmed",["Phase"]="PostCode",["Rva"]="0x72D300",["Message"]="Synthetic bounded post-code failure; no real V17 invocation"}});
            var end=Data(Log,"END");end["ExitCode"]=1;end["PostCodeConfirmed"]=false;end["BorrowedSourceContractVerified"]=false;end["SessionBlocked"]=true;end["PendingJournal"]=ForcePending;end["SingleTrialPendingJournal"]=SinglePending;
            Data(Log,"EVENT_INTENT_DURABLE")["Path"]=ForcePending;Data(Log,"EVENT_INTENT_DURABLE")["SingleTrialPending"]=SinglePending;
            File.WriteAllText(NativePath,Text(Log));
            var source=Data(Log,"SOURCE");
            var pending=new JsonObject{["State"]="UNRESOLVED_INTENT",["Mode"]=1,["Log"]=NativePath,["At"]=Epoch.ToString("O"),["ManagedProgramVersion"]="17.0.0"};
            foreach(string key in new[]{"Pid","Creation","Image","Hwnd","Shop","Owner","TargetOwner"})pending[key]=source[key]!.DeepClone();
            Stamp(pending);pending.Remove("MaximumDeferredMask");File.WriteAllText(ForceArchive,pending.ToJsonString());
            pending["Schema"]="V15SingleTrialPendingV1";pending["Version"]="15.0.0";pending["NoAutomaticRetransmission"]=true;pending["PendingJournal"]=ForcePending;
            File.WriteAllText(SingleArchive,pending.ToJsonString());
            var identity=new {Pid=Source.ProcessId,Creation=Source.CreationFileTime,Image,Path=Source.ImagePath,Sha256=Source.DiskSha256,Source.PeTimestamp,Source.ImageSize};
            ReceiptNode=JsonSerializer.SerializeToNode(new{Schema="DelayedPostCodeRecoveryV1",Phase="Committed",TransactionId=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Root))),
                ManagedProgramVersion="17.0.0",ColdCodePolicyId=PolicyId,ColdCodeCertificateSha256=Certificate,MaximumDeferredMask=3,
                OriginalNativeLogPath=NativePath,OriginalNativeLogSha256=ShopPostCodeRecoveryProof.Hash(NativePath),Source=identity,TargetOwner=Target,Mode=1,NativeNonce=Data(Log,"END")["NativeResult"]!["Nonce"]!.GetValue<ulong>(),
                OriginalForcePendingPath=ForcePending,OriginalForcePendingSha256=ShopPostCodeRecoveryProof.Hash(ForceArchive),ForceResolvedPath=ForceArchive,
                OriginalSingleTrialPendingPath=SinglePending,OriginalSingleTrialPendingSha256=ShopPostCodeRecoveryProof.Hash(SingleArchive),SingleTrialResolvedPath=SingleArchive,
                OriginalEndAt=Epoch.AddMilliseconds((Log.Count-1)*10),OriginalPostCodeConfirmed=false,OriginalNativeReturned=true,OriginalCleanupConfirmed=true,
                OriginalRequestCompletionVerified=false,ServerAcknowledgmentVerified=false,RemoteModuleUnloadProven=false,EntireHookTailExitProven=false,
                OnlyFutureDistinctRequestsMayProceed=true,NoAutomaticRetransmission=true,NativeRequests=0,GameInput=0,
                FreshProof=new{Identity=identity,StartedAt=Epoch.AddSeconds(2),FinishedAt=Epoch.AddSeconds(2.01),AllCurrentBytesMatched=true,NoDeferredBodies=true,ReadCalls=7,ReadBytes=700,
                    MetadataAndDispatchProofs=Enumerable.Range(0,7).Select(i=>new{Synthetic=true,Index=i}).ToArray()}})!.AsObject();
            WriteReceipt();
        }
        internal void WriteReceipt()=>File.WriteAllText(Receipt,ReceiptNode.ToJsonString());
        internal void ChangeLog(Action<List<JsonObject>> change)
        {change(Log);File.WriteAllText(NativePath,Text(Log));ReceiptNode["OriginalNativeLogSha256"]=ShopPostCodeRecoveryProof.Hash(NativePath);WriteReceipt();}
        internal ShopPostCodeRecoveredProof Read()=>ShopPostCodeRecoveryProof.Read(Receipt,Source,Journal);
        internal void Lifecycle()
        {
            ReceiptNode["Schema"]="ReturnedLifecycleRecoveryV1";ReceiptNode["LifecycleSettled"]=true;ReceiptNode["Disposition"]="ReturnedAndCleaned_PostCodeUnavailable";ReceiptNode["CurrentCreateCopyBytesVerified"]=false;
            var fresh=ReceiptNode["FreshProof"]!.AsObject();fresh["ReturnedLifecycleContract"]=1;fresh["UnavailableProtectedMask"]=3;fresh["CurrentCreateCopyBytesVerified"]=false;fresh["AllCurrentBytesMatched"]=false;fresh["NoDeferredBodies"]=false;WriteReceipt();
        }
    }

    private static void Stamp(JsonObject value){value["ColdCodePolicyId"]=PolicyId;value["ColdCodeCertificateSha256"]=Certificate;value["MaximumDeferredMask"]=3;}
    private static int DeferredBytes(uint mask)=>((mask&1)!=0?91:0)+((mask&2)!=0?67:0);
    private static string Kind(JsonObject row)=>row["Kind"]!.GetValue<string>();
    private static JsonObject Data(List<JsonObject> rows,string kind)=>rows.First(r=>Kind(r)==kind)["Data"]!.AsObject();
    private static JsonObject[] AllData(List<JsonObject> rows,string kind)=>rows.Where(r=>Kind(r)==kind).Select(r=>r["Data"]!.AsObject()).ToArray();
    private static void SetState<T>(List<JsonObject> rows,string key,T value)
    {
        foreach(var row in rows)
        {
            string field=Kind(row) switch{"EVENT_RESULT" or "NATIVE_FINAL_STATE"=>"State","END"=>"NativeResult",_=>""};
            if(field.Length!=0&&row["Data"]?[field] is JsonObject state)state[key]=JsonSerializer.SerializeToNode(value);
        }
    }
    private static string Text(List<JsonObject> rows)
    {
        for(int i=0;i<rows.Count;i++)
        {rows[i]["Sequence"]=i+1;rows[i]["At"]=Epoch.AddMilliseconds(i*10).ToString("O");rows[i]["DiskFlush"]=Kind(rows[i]) is "EVENT_INTENT_DURABLE" or "COMMAND_PUBLISH_INTENT" or "WM_NULL_POST_INTENT" or "WM_NULL_POST_RESULT" or "EVENT_RESULT" or "END" or "CALLBACK_TIMEOUT" or "COMPLETION_UNKNOWN"||Kind(rows[i]).Contains("ERROR",StringComparison.Ordinal);
         if(Kind(rows[i])=="EVENT_RESULT"){rows[i]["Data"]!["PostStartedAt"]=Epoch.AddMilliseconds(i*10-2).ToString("O");rows[i]["Data"]!["PostFinishedAt"]=Epoch.AddMilliseconds(i*10-1).ToString("O");}}
        var end=Data(rows,"END");end["LogRecordsBeforeEnd"]=rows.Count-1;end["LogDiskFlushesBeforeEnd"]=rows.Take(rows.Count-1).Count(r=>r["DiskFlush"]!.GetValue<bool>());end["LogBufferedRecordsBeforeEnd"]=rows.Take(rows.Count-1).Count(r=>!r["DiskFlush"]!.GetValue<bool>());end["LogStreamFlushesBeforeEnd"]=0;
        end["LogIoWriteDurationMsBeforeEnd"]=1.0;end["LogSerializationDurationMsBeforeEnd"]=0.5;end["LogWriteDurationMsBeforeEnd"]=1.5;
        return string.Join("\n",rows.Select(r=>r.ToJsonString()))+"\n";
    }
    private static void Need(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    internal static int RunReport(string output)
    {
        using var identity=WindowsIdentity.GetCurrent();bool admin=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);Need(admin,"ADMIN_REQUIRED");
        var checks=Run();string assembly=typeof(NativeCertifiedColdV17Verification).Assembly.Location;
        const string path=@"D:\lineage\LC_UpdateExecution_20261021_v1\market-v1\LC_PlayerMarket_v85\source-v1\NativeCertifiedColdV17Verification.cs";
        var report=new{Schema="V82NativeCertifiedColdV17VerificationV1",Success=checks.All(c=>c.Passed),IsAdministrator=admin,Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),Checks=checks,TestedAssembly=assembly,TestedAssemblySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),SourceHashes=new Dictionary<string,string>{{path,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}},SyntheticV17Receipt=true,Research82FixtureIsNotV17LiveEvidence=true,LcRead=0,NativeCalls=0,GameInput=0};
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(file,report,new JsonSerializerOptions{WriteIndented=true});file.Flush(true);return report.Success?0:1;
    }
    private const string Research82="""
{"At":"2026-10-16T19:44:54.3391498+00:00","Sequence":1,"Kind":"START","DiskFlush":false,"Data":{"Version":"82.0.0.1","ManagedProgramVersion":"82.0.0.1","NativeProtocolVersion":82,"NativeGuardContract":82,"NativePayloadVersion":"82.0.0.1","PostCodeRecheckContract":1,"PostCodeRecheckMaximumMilliseconds":1000,"ReturnedLifecycleRecoveryEnabled":false,"ForceEnabled":false,"ExplicitColdTrial":true,"SubmitPreflightRecheckMaximumMilliseconds":250,"Mode":"buy","MaximumNativeRequests":1,"MouseMessages":0,"KeyMessages":0,"TouchCalls":0,"ForegroundChanges":0,"DirectSelectedOwnerWrites":0,"SharedOwnershipCounterUpdates":true,"OpenProcessAccess":"0x410","DeferredCodePolicy":"Research82 explicit fixed-source once only; strict by default (mask0); only exact Create/Copy NOACCESS may defer with consumed durable token; current bytes not claimed; historical same-build evidence plus current metadata/other bodies; immediate strict full postbody","Binding":"Fixed PID/creation/base/path/full SHA plus independently verified current source","CurrentOwnerBits":64,"LogPolicy":"DurableDispatchIntent_Result_End_Error;BufferedVerboseV2","ResearchOnly":true}}
{"At":"2026-10-16T19:44:55.2664615+00:00","Sequence":2,"Kind":"SOURCE","DiskFlush":false,"Data":{"Pid":5408,"Creation":134366508555397744,"Image":140702510874624,"Hwnd":263744,"Tid":18804,"Shop":2687142035360,"Owner":18446744073709551615,"TargetOwner":89985545,"Path":"C:\\Program Files (x86)\\NCSOFT\\Lineage Classic\\LC.exe","Sha256":"38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1","Timestamp":"0x6ACEDB9A","ImageSize":"0x3F5F000","ReadOnlyProcessHandle":true}}
{"At":"2026-10-16T19:44:55.3546316+00:00","Sequence":3,"Kind":"PACKET_CODE_PENDING","DiskFlush":false,"Data":{"Rva":"0x72D300","Length":91,"Bit":1,"Page":{"Base":140702518398976,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"Phase":"Preflight","ResearchOnly":true,"ExplicitColdTrial":true,"CurrentBytesMatched":false,"HistoricalSameBuildBodyCertificate":true,"Meaning":"Not called yet; current bytes unavailable; LC fault handling unproven; research token still required before hook","ProtectionChanged":false}}
{"At":"2026-10-16T19:44:55.3789961+00:00","Sequence":4,"Kind":"PACKET_CODE_PENDING","DiskFlush":false,"Data":{"Rva":"0x76BEB0","Length":67,"Bit":2,"Page":{"Base":140702518652928,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"Phase":"Preflight","ResearchOnly":true,"ExplicitColdTrial":true,"CurrentBytesMatched":false,"HistoricalSameBuildBodyCertificate":true,"Meaning":"Not called yet; current bytes unavailable; LC fault handling unproven; research token still required before hook","ProtectionChanged":false}}
{"At":"2026-10-16T19:44:55.3854061+00:00","Sequence":5,"Kind":"CURRENT_NATIVE_CODE_GUARDS","DiskFlush":false,"Data":{"Bodies":5,"Bytes":4262,"DeferredMask":3,"DeferredCodeBytes":158,"CurrentPendingBytesClaimedMatched":false,"BorrowedScalarSource":true,"AdditionalDestructorCalls":0}}
{"At":"2026-10-16T19:44:55.3918517+00:00","Sequence":6,"Kind":"NONINVOKED_CODE_OBSERVATION","DiskFlush":false,"Data":{"Rva":"0x73B790","Role":"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies","DispatchPrerequisite":false,"BodyCallableClaimed":false,"HistoricalBytesSubstituted":false,"Pages":[{"Rva":"0x73B790","Length":73,"Page":{"Base":140702518456320,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"CurrentlyReadableExecutable":false,"CurrentBytesMatched":null}]}}
{"At":"2026-10-16T19:44:55.425134+00:00","Sequence":7,"Kind":"NONINVOKED_CODE_OBSERVATION","DiskFlush":false,"Data":{"Rva":"0x104870","Role":"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies","DispatchPrerequisite":false,"BodyCallableClaimed":false,"HistoricalBytesSubstituted":false,"Pages":[{"Rva":"0x104870","Length":80,"Page":{"Base":140702511939584,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":12288,"State":4096,"Protect":32,"Type":16777216},"CurrentlyReadableExecutable":true,"CurrentBytesMatched":true}]}}
{"At":"2026-10-16T19:44:55.4313578+00:00","Sequence":8,"Kind":"NETWORK_SHARED_COUNTS_SAMPLE","DiskFlush":false,"Data":{"Phase":"A","NetworkControl":2687305948304,"AddressHex":"0x271AFEA9C98","Length":8,"Counts":{"Strong":1,"Weak":1},"Alive":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:55.4466132+00:00","Sequence":9,"Kind":"NETWORK_SHARED_COUNTS_SAMPLE","DiskFlush":false,"Data":{"Phase":"B","NetworkControl":2687305948304,"AddressHex":"0x271AFEA9C98","Length":8,"Counts":{"Strong":1,"Weak":1},"Alive":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:55.448927+00:00","Sequence":10,"Kind":"NETWORK_SHARED_COUNTS_AB","DiskFlush":false,"Data":{"NetworkControl":2687305948304,"CountsAddress":"0x271AFEA9C98","Length":8,"OwnCountsA":{"Strong":1,"Weak":1},"OwnCountsB":{"Strong":1,"Weak":1},"CountsChanged":false,"StableControlVtableAbClosed":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:55.4578583+00:00","Sequence":11,"Kind":"PREFLIGHT_AB_CLOSED","DiskFlush":false,"Data":{"ProofCount":85,"ReadCalls":207,"ReadBytes":13947,"Owner64":18446744073709551615,"CurrentShop":2687142035360,"TargetOwner":89985545,"Network":2687305948320,"NetworkControl":2687305948304,"NetworkChild":2687479925376,"PacketCreate":140702518399744,"PacketCopy":140702518656688,"SubsystemCount":64,"DeferredMask":3,"DeferredCodeBytes":158,"EverySynchronousGuardBodyCurrentlyMatched":false,"ColdCodeBytesPending":true,"AsyncAckNotInvokedByHelper":true,"OwnCountsA":{"Strong":1,"Weak":1},"OwnCountsB":{"Strong":1,"Weak":1},"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:56.1307093+00:00","Sequence":12,"Kind":"RESEARCH_TOKEN_CONSUMED","DiskFlush":false,"Data":{"Path":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.v82-cold-trial.consumed.json","Sha256":"AA8557725D7362478DF188C4640D807450B6F3687183D823B17249847224AB6D","Binding":{"Pid":5408,"Creation":134366508555397744,"Image":140702510874624,"Hwnd":263744,"Tid":18804,"Shop":2687142035360,"CurrentOwner":18446744073709551615,"TargetOwner":89985545,"Mode":1,"ImagePath":"C:\\Program Files (x86)\\NCSOFT\\Lineage Classic\\LC.exe","ImageSha":"38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1","LogPath":"D:\\lineage\\LC_PlayerMarket_v82\\research-cold-trial-v1\\live-v2\\native.jsonl","ManagedSha256":"5065674C945748E6F63AC25A9A95C22D7682FC44AB1ED74AD31F44B0335557C5","NativeSha256":"CC84775671617B6C627B55A95CE5C9399F2C40E3BC5C04E6C71D7FF6FDD1B8D6"},"BeforeHook":true,"Permanent":true,"AutomaticRetransmission":false}}
{"At":"2026-10-16T19:44:56.1380515+00:00","Sequence":13,"Kind":"HOOK_INSTALL_INTENT","DiskFlush":false,"Data":{"Pid":5408,"Creation":134366508555397744,"Tid":18804,"Hwnd":263744,"Mode":1,"TargetOwner":89985545,"EntryRva":"0x287910","NativePacketType":"CS_PERSONAL_SHOP_LIST_REQ"}}
{"At":"2026-10-16T19:44:56.1656299+00:00","Sequence":14,"Kind":"NATIVE_PAYLOAD","DiskFlush":false,"Data":{"Path":"D:\\lineage\\LC_PlayerMarket_v82\\research-cold-trial-v1\\release-v1\\LC_PlayerShopListResearchHookV82.dll","Sha256":"CC84775671617B6C627B55A95CE5C9399F2C40E3BC5C04E6C71D7FF6FDD1B8D6","Export":"PlayerShopListResearchHookV82","NewProtocol":true,"ResearchOnly":true}}
{"At":"2026-10-16T19:44:56.1765078+00:00","Sequence":15,"Kind":"HOOK_INSTALL_RESULT","DiskFlush":false,"Data":{"Installed":true,"Win32Error":0,"HookType":"WH_GETMESSAGE","Thread":18804,"GlobalHook":false}}
{"At":"2026-10-16T19:44:56.185002+00:00","Sequence":16,"Kind":"PACKET_CODE_PENDING","DiskFlush":false,"Data":{"Rva":"0x72D300","Length":91,"Bit":1,"Page":{"Base":140702518398976,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"Phase":"Preflight","ResearchOnly":true,"ExplicitColdTrial":true,"CurrentBytesMatched":false,"HistoricalSameBuildBodyCertificate":true,"Meaning":"Not called yet; current bytes unavailable; LC fault handling unproven; research token still required before hook","ProtectionChanged":false}}
{"At":"2026-10-16T19:44:56.1867491+00:00","Sequence":17,"Kind":"PACKET_CODE_PENDING","DiskFlush":false,"Data":{"Rva":"0x76BEB0","Length":67,"Bit":2,"Page":{"Base":140702518652928,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"Phase":"Preflight","ResearchOnly":true,"ExplicitColdTrial":true,"CurrentBytesMatched":false,"HistoricalSameBuildBodyCertificate":true,"Meaning":"Not called yet; current bytes unavailable; LC fault handling unproven; research token still required before hook","ProtectionChanged":false}}
{"At":"2026-10-16T19:44:56.1868738+00:00","Sequence":18,"Kind":"CURRENT_NATIVE_CODE_GUARDS","DiskFlush":false,"Data":{"Bodies":5,"Bytes":4262,"DeferredMask":3,"DeferredCodeBytes":158,"CurrentPendingBytesClaimedMatched":false,"BorrowedScalarSource":true,"AdditionalDestructorCalls":0}}
{"At":"2026-10-16T19:44:56.1869322+00:00","Sequence":19,"Kind":"NONINVOKED_CODE_OBSERVATION","DiskFlush":false,"Data":{"Rva":"0x73B790","Role":"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies","DispatchPrerequisite":false,"BodyCallableClaimed":false,"HistoricalBytesSubstituted":false,"Pages":[{"Rva":"0x73B790","Length":73,"Page":{"Base":140702518456320,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"CurrentlyReadableExecutable":false,"CurrentBytesMatched":null}]}}
{"At":"2026-10-16T19:44:56.1870135+00:00","Sequence":20,"Kind":"NONINVOKED_CODE_OBSERVATION","DiskFlush":false,"Data":{"Rva":"0x104870","Role":"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies","DispatchPrerequisite":false,"BodyCallableClaimed":false,"HistoricalBytesSubstituted":false,"Pages":[{"Rva":"0x104870","Length":80,"Page":{"Base":140702511939584,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":12288,"State":4096,"Protect":32,"Type":16777216},"CurrentlyReadableExecutable":true,"CurrentBytesMatched":true}]}}
{"At":"2026-10-16T19:44:56.18767+00:00","Sequence":21,"Kind":"NETWORK_SHARED_COUNTS_SAMPLE","DiskFlush":false,"Data":{"Phase":"A","NetworkControl":2687305948304,"AddressHex":"0x271AFEA9C98","Length":8,"Counts":{"Strong":1,"Weak":1},"Alive":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:56.1882382+00:00","Sequence":22,"Kind":"NETWORK_SHARED_COUNTS_SAMPLE","DiskFlush":false,"Data":{"Phase":"B","NetworkControl":2687305948304,"AddressHex":"0x271AFEA9C98","Length":8,"Counts":{"Strong":1,"Weak":1},"Alive":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:56.1882817+00:00","Sequence":23,"Kind":"NETWORK_SHARED_COUNTS_AB","DiskFlush":false,"Data":{"NetworkControl":2687305948304,"CountsAddress":"0x271AFEA9C98","Length":8,"OwnCountsA":{"Strong":1,"Weak":1},"OwnCountsB":{"Strong":1,"Weak":1},"CountsChanged":false,"StableControlVtableAbClosed":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:56.1895209+00:00","Sequence":24,"Kind":"PREFLIGHT_AB_CLOSED","DiskFlush":false,"Data":{"ProofCount":85,"ReadCalls":414,"ReadBytes":27894,"Owner64":18446744073709551615,"CurrentShop":2687142035360,"TargetOwner":89985545,"Network":2687305948320,"NetworkControl":2687305948304,"NetworkChild":2687479925376,"PacketCreate":140702518399744,"PacketCopy":140702518656688,"SubsystemCount":64,"DeferredMask":3,"DeferredCodeBytes":158,"EverySynchronousGuardBodyCurrentlyMatched":false,"ColdCodeBytesPending":true,"AsyncAckNotInvokedByHelper":true,"OwnCountsA":{"Strong":1,"Weak":1},"OwnCountsB":{"Strong":1,"Weak":1},"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:57.092359+00:00","Sequence":25,"Kind":"EVENT_INTENT_DURABLE","DiskFlush":true,"Data":{"Path":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.pending.json","SingleTrialPending":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.v15-singletrial.pending.json","Mode":1,"Calls":1,"NoAutomaticRetransmission":true}}
{"At":"2026-10-16T19:44:57.3355042+00:00","Sequence":26,"Kind":"COMMAND_PUBLISH_INTENT","DiskFlush":true,"Data":{"Sequence":1,"Nonce":7732591523287629023,"Mode":1,"TargetOwner":89985545,"MaximumRequests":1}}
{"At":"2026-10-16T19:44:57.8071321+00:00","Sequence":27,"Kind":"WM_NULL_POST_INTENT","DiskFlush":true,"Data":{"Sequence":1,"Nonce":7732591523287629023,"TargetThread":18804,"Posts":1,"TransportOnly":true}}
{"At":"2026-10-16T19:44:58.273814+00:00","Sequence":28,"Kind":"PACKET_CODE_PENDING","DiskFlush":false,"Data":{"Rva":"0x72D300","Length":91,"Bit":1,"Page":{"Base":140702518398976,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"Phase":"Preflight","ResearchOnly":true,"ExplicitColdTrial":true,"CurrentBytesMatched":false,"HistoricalSameBuildBodyCertificate":true,"Meaning":"Not called yet; current bytes unavailable; LC fault handling unproven; research token still required before hook","ProtectionChanged":false}}
{"At":"2026-10-16T19:44:58.2750233+00:00","Sequence":29,"Kind":"PACKET_CODE_PENDING","DiskFlush":false,"Data":{"Rva":"0x76BEB0","Length":67,"Bit":2,"Page":{"Base":140702518652928,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"Phase":"Preflight","ResearchOnly":true,"ExplicitColdTrial":true,"CurrentBytesMatched":false,"HistoricalSameBuildBodyCertificate":true,"Meaning":"Not called yet; current bytes unavailable; LC fault handling unproven; research token still required before hook","ProtectionChanged":false}}
{"At":"2026-10-16T19:44:58.2751291+00:00","Sequence":30,"Kind":"CURRENT_NATIVE_CODE_GUARDS","DiskFlush":false,"Data":{"Bodies":5,"Bytes":4262,"DeferredMask":3,"DeferredCodeBytes":158,"CurrentPendingBytesClaimedMatched":false,"BorrowedScalarSource":true,"AdditionalDestructorCalls":0}}
{"At":"2026-10-16T19:44:58.2751794+00:00","Sequence":31,"Kind":"NONINVOKED_CODE_OBSERVATION","DiskFlush":false,"Data":{"Rva":"0x73B790","Role":"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies","DispatchPrerequisite":false,"BodyCallableClaimed":false,"HistoricalBytesSubstituted":false,"Pages":[{"Rva":"0x73B790","Length":73,"Page":{"Base":140702518456320,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":4096,"State":4096,"Protect":1,"Type":16777216},"CurrentlyReadableExecutable":false,"CurrentBytesMatched":null}]}}
{"At":"2026-10-16T19:44:58.275255+00:00","Sequence":32,"Kind":"NONINVOKED_CODE_OBSERVATION","DiskFlush":false,"Data":{"Rva":"0x104870","Role":"Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies","DispatchPrerequisite":false,"BodyCallableClaimed":false,"HistoricalBytesSubstituted":false,"Pages":[{"Rva":"0x104870","Length":80,"Page":{"Base":140702511939584,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":12288,"State":4096,"Protect":32,"Type":16777216},"CurrentlyReadableExecutable":true,"CurrentBytesMatched":true}]}}
{"At":"2026-10-16T19:44:58.2758531+00:00","Sequence":33,"Kind":"NETWORK_SHARED_COUNTS_SAMPLE","DiskFlush":false,"Data":{"Phase":"A","NetworkControl":2687305948304,"AddressHex":"0x271AFEA9C98","Length":8,"Counts":{"Strong":1,"Weak":1},"Alive":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:58.2762104+00:00","Sequence":34,"Kind":"NETWORK_SHARED_COUNTS_SAMPLE","DiskFlush":false,"Data":{"Phase":"B","NetworkControl":2687305948304,"AddressHex":"0x271AFEA9C98","Length":8,"Counts":{"Strong":1,"Weak":1},"Alive":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:58.2762411+00:00","Sequence":35,"Kind":"NETWORK_SHARED_COUNTS_AB","DiskFlush":false,"Data":{"NetworkControl":2687305948304,"CountsAddress":"0x271AFEA9C98","Length":8,"OwnCountsA":{"Strong":1,"Weak":1},"OwnCountsB":{"Strong":1,"Weak":1},"CountsChanged":false,"StableControlVtableAbClosed":true,"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:58.2770353+00:00","Sequence":36,"Kind":"PREFLIGHT_AB_CLOSED","DiskFlush":false,"Data":{"ProofCount":85,"ReadCalls":619,"ReadBytes":41513,"Owner64":18446744073709551615,"CurrentShop":2687142035360,"TargetOwner":89985545,"Network":2687305948320,"NetworkControl":2687305948304,"NetworkChild":2687479925376,"PacketCreate":140702518399744,"PacketCopy":140702518656688,"SubsystemCount":64,"DeferredMask":3,"DeferredCodeBytes":158,"EverySynchronousGuardBodyCurrentlyMatched":false,"ColdCodeBytesPending":true,"AsyncAckNotInvokedByHelper":true,"OwnCountsA":{"Strong":1,"Weak":1},"OwnCountsB":{"Strong":1,"Weak":1},"CountEqualityRequired":false}}
{"At":"2026-10-16T19:44:58.2786816+00:00","Sequence":37,"Kind":"WM_NULL_POST_RESULT","DiskFlush":true,"Data":{"Posted":true,"Win32Error":0,"NoAutomaticRetransmission":true}}
{"At":"2026-10-16T19:44:58.7256744+00:00","Sequence":38,"Kind":"EVENT_RESULT","DiskFlush":true,"Data":{"State":{"Sequence":1,"Completed":1,"Active":0,"Claimed":1,"Entered":1,"Returned":1,"Status":1,"Exception":0,"ObservedPid":5408,"ObservedTid":18804,"Nonce":7732591523287629023,"Tick":18965812,"Callback":140728708436000,"Module":140728708431872,"Stage":5,"RequestMetadataBefore":0,"RequestMetadataAfter":0,"RequestUnchanged":1,"DestructorCalls":0,"HostDeferredMask":3,"NativeDeferredMask":3,"PostCodeConfirmed":1,"ExceptionStage":0,"ExceptionAddress":0,"ExceptionAccessAddress":0,"ExceptionAccessKind":0,"LeaseReleaseAttempted":1,"LeaseReleaseCompleted":1,"LeaseAcquired":1,"ExceptionCount":0,"ReleaseExceptionCode":0,"ReleaseExceptionAddress":0},"NativeReturned":true,"PostCodeConfirmed":true,"PostCodeErrorCode":null,"PostCodeErrorRva":null,"PostProof":[{"Rva":"0x3EC5C1C","Length":12,"Before":{"Base":140702576693248,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":630784,"State":4096,"Protect":2,"Type":16777216},"After":{"Base":140702576693248,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":630784,"State":4096,"Protect":2,"Type":16777216},"Matched":true,"BodyAbClosed":true,"PageAbClosed":true,"Metadata":true,"CurrentSha256":"540571BB493FC0409992149A976798A0F8BA52F8F46F5467EC002F3B2ABC8DEE"},{"Rva":"0x15371DC","Length":12,"Before":{"Base":140702533120000,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":1024000,"State":4096,"Protect":2,"Type":16777216},"After":{"Base":140702533120000,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":1024000,"State":4096,"Protect":2,"Type":16777216},"Matched":true,"BodyAbClosed":true,"PageAbClosed":true,"Metadata":true,"CurrentSha256":"39455C244CD3D4DE53471A0ADEC96067C74300DDC0BA6DB9733FE8F8E3A9FFC4"},{"Rva":"0x72D300","Length":91,"Before":{"Base":140702518398976,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":20480,"State":4096,"Protect":32,"Type":16777216},"After":{"Base":140702518398976,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":20480,"State":4096,"Protect":32,"Type":16777216},"Matched":true,"BodyAbClosed":true,"PageAbClosed":true,"Metadata":false,"CurrentSha256":"BA49FB37E824FC2A33864D390BCEBB5EB52872025FBACCA457CF9C8FF7AB6EFD"},{"Rva":"0x76BEB0","Length":67,"Before":{"Base":140702518652928,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":8192,"State":4096,"Protect":32,"Type":16777216},"After":{"Base":140702518652928,"AllocationBase":140702510874624,"AllocationProtect":128,"PartitionId":0,"Size":8192,"State":4096,"Protect":32,"Type":16777216},"Matched":true,"BodyAbClosed":true,"PageAbClosed":true,"Metadata":false,"CurrentSha256":"7E16D7DD5472FC8C48134A98B3ABC910F48B91B50F998758EBF38D51A153C80F"}],"PostStartedAt":"2026-10-16T19:44:58.7142665+00:00","PostFinishedAt":"2026-10-16T19:44:58.7248935+00:00","PostReadBeforeDurableResult":true,"PostCodeRecheck":{"Contract":1,"Attempts":1,"TemporaryUnavailableObservations":0,"ElapsedMilliseconds":4.9050999991595745,"Outcome":"Verified","ReadOnly":true,"NativeRequestsRetried":0},"ListReceivedVerified":false,"OwnerUpdatedByAckVerified":false,"TargetOwner":89985545}}
{"At":"2026-10-16T19:44:59.2539684+00:00","Sequence":39,"Kind":"PACKET_CODE_CONFIRMED","DiskFlush":false,"Data":{"Mode":"DeferredResolved","CreateRva":"0x72D300","CopyRva":"0x76BEB0","DeferredMask":3,"NativeDeferredMask":3,"AdditionalPostBodyReads":true,"SingleColdTrial":true,"PostReadBeforeDurableResult":true,"NoProtectionChanges":true}}
{"At":"2026-10-16T19:44:59.2584588+00:00","Sequence":40,"Kind":"BORROWED_SOURCE_VERIFIED","DiskFlush":false,"Data":{"RequestUnchanged":1,"RequestMetadataBefore":0,"RequestMetadataAfter":0,"DestructorCalls":0,"IndependentCloneOwnedByLc":true}}
{"At":"2026-10-16T19:44:59.3939589+00:00","Sequence":41,"Kind":"NATIVE_FINAL_STATE","DiskFlush":false,"Data":{"State":{"Sequence":1,"Completed":1,"Active":0,"Claimed":1,"Entered":1,"Returned":1,"Status":1,"Exception":0,"ObservedPid":5408,"ObservedTid":18804,"Nonce":7732591523287629023,"Tick":18965812,"Callback":140728708436000,"Module":140728708431872,"Stage":5,"RequestMetadataBefore":0,"RequestMetadataAfter":0,"RequestUnchanged":1,"DestructorCalls":0,"HostDeferredMask":3,"NativeDeferredMask":3,"PostCodeConfirmed":1,"ExceptionStage":0,"ExceptionAddress":0,"ExceptionAccessAddress":0,"ExceptionAccessKind":0,"LeaseReleaseAttempted":1,"LeaseReleaseCompleted":1,"LeaseAcquired":1,"ExceptionCount":0,"ReleaseExceptionCode":0,"ReleaseExceptionAddress":0},"QuiescentObserved":true,"DisarmedBeforeClaim":false,"EntireCallNextHookTailExitProven":false}}
{"At":"2026-10-16T19:44:59.399882+00:00","Sequence":42,"Kind":"UNHOOK","DiskFlush":false,"Data":{"Success":true,"Win32Error":0,"InterruptsActiveCallback":false}}
{"At":"2026-10-16T19:44:59.4016721+00:00","Sequence":43,"Kind":"LOCAL_VIEW_RELEASE","DiskFlush":false,"Data":{"Success":true,"Win32Error":0}}
{"At":"2026-10-16T19:44:59.4047057+00:00","Sequence":44,"Kind":"LOCAL_MAPPING_CLOSE","DiskFlush":false,"Data":{"Success":true,"Win32Error":0}}
{"At":"2026-10-16T19:44:59.4050916+00:00","Sequence":45,"Kind":"LOCAL_LIBRARY_FREE","DiskFlush":false,"Data":{"Success":true}}
{"At":"2026-10-16T19:44:59.4085442+00:00","Sequence":46,"Kind":"CLEANUP","DiskFlush":false,"Data":{"CleanupConfirmed":true,"QuiescentObserved":true,"Installed":true,"Unhooked":true,"LocalViewRetained":false,"MappingRetained":false,"LibraryRetained":false,"HookRetained":false,"RemoteModuleUnloadProven":false,"EntireCallNextHookTailExitProven":false,"AdditionalMessagesSent":0}}
{"At":"2026-10-16T19:44:59.7193046+00:00","Sequence":47,"Kind":"JOURNAL_SETTLED","DiskFlush":false,"Data":{"Path":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.resolved-bd83550ae7f2415284d6a083b381fe8c.jsonl"}}
{"At":"2026-10-16T19:44:59.8840368+00:00","Sequence":48,"Kind":"SINGLE_TRIAL_JOURNAL_SETTLED","DiskFlush":false,"Data":{"Path":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.v15-singletrial.resolved-225e81d0f02f4fe3be1a035af2dd01d7.jsonl"}}
{"At":"2026-10-16T19:44:59.8846635+00:00","Sequence":49,"Kind":"END","DiskFlush":true,"Data":{"ExitCode":0,"ResearchOnly":true,"ResearchTokenConsumed":true,"ResearchTokenPath":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.v82-cold-trial.consumed.json","ResearchTokenSha256":"AA8557725D7362478DF188C4640D807450B6F3687183D823B17249847224AB6D","AutomaticRetransmission":false,"PostCodeConfirmed":true,"EventResultDurable":true,"BorrowedSourceContractVerified":true,"NativeAlreadyReturned":true,"AckObserved":false,"NativeResult":{"Sequence":1,"Completed":1,"Active":0,"Claimed":1,"Entered":1,"Returned":1,"Status":1,"Exception":0,"ObservedPid":5408,"ObservedTid":18804,"Nonce":7732591523287629023,"Tick":18965812,"Callback":140728708436000,"Module":140728708431872,"Stage":5,"RequestMetadataBefore":0,"RequestMetadataAfter":0,"RequestUnchanged":1,"DestructorCalls":0,"HostDeferredMask":3,"NativeDeferredMask":3,"PostCodeConfirmed":1,"ExceptionStage":0,"ExceptionAddress":0,"ExceptionAccessAddress":0,"ExceptionAccessKind":0,"LeaseReleaseAttempted":1,"LeaseReleaseCompleted":1,"LeaseAcquired":1,"ExceptionCount":0,"ReleaseExceptionCode":0,"ReleaseExceptionAddress":0},"CleanupConfirmed":true,"SessionBlocked":false,"PendingJournal":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.pending.json","SingleTrialPendingJournal":"D:\\lineage\\LC_PlayerShopListAction_v1\\journal\\5408-134366508555397744.v15-singletrial.pending.json","SingleTrialPendingSchema":"V15SingleTrialPendingV1","ListReceivedVerified":false,"RemoteModuleUnloadProven":false,"EntireHookTailExitProven":false,"LogRecordsBeforeEnd":48,"LogDiskFlushesBeforeEnd":5,"LogStreamFlushesBeforeEnd":0,"LogBufferedRecordsBeforeEnd":43,"LogWriteDurationMsBeforeEnd":2387.4056,"LogIoWriteDurationMsBeforeEnd":1972.3586,"LogSerializationDurationMsBeforeEnd":415.0470000000002}}
""";
}
