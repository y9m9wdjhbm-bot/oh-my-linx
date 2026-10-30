using System.Text.Json;
using static MemoryHub.Service.ShopNativeV17Contract;

namespace MemoryHub.Service;

internal sealed partial record ShopNativeOutcome
{
    private static void V17Start(JsonElement start, SourceIdentity source)
    {
        Need(IsProducer(start,source) && PurposeMatches(start,ShopNativeLogPurpose.NativeRequest)
            && Text(start,"LogPolicy")==V14BufferedLogPolicy && N(start,"CurrentOwnerBits")==64
            && T(start,"OpenProcessAccess")=="0x410" && B(start,"SharedOwnershipCounterUpdates")
            && N(start,"PostCodeRecheckContract")==1 && N(start,"PostCodeRecheckMaximumMilliseconds")==1000
            && N(start,"ReturnedLifecycleRecoveryContract")==1 && N(start,"SubmitPreflightRecheckMaximumMilliseconds")==250
            && T(start,"Binding")=="Caller-supplied runtime identity, independently verified","V17 producer differs");
        foreach(string key in new[]{"MouseMessages","KeyMessages","TouchCalls","ForegroundChanges","DirectSelectedOwnerWrites"})
            Need(N(start,key)==0,"Unexpected game input/write");
    }
    private static JsonElement V17One(IReadOnlyList<JsonElement> rows,string kind)
    {var found=rows.Where(r=>T(r,"Kind")==kind).ToArray();Need(found.Length==1,"Missing/duplicate "+kind);return found[0];}
    private static JsonElement V17Data(IReadOnlyList<JsonElement> rows,string kind)=>F(V17One(rows,kind),"Data");
    private static bool ConfirmedV17CurrentEvidence(string text,SourceIdentity source,ulong image,ulong owner,JsonElement native,bool returned)
    {
        var rows=Rows(text); var start=V17Data(rows,"START");var end=V17Data(rows,"END");var identity=V17Data(rows,"SOURCE");
        V17Start(start,source);Need(TerminalMatches(start,end),"V17 terminal producer/certificate differs");
        int disk=rows.Take(rows.Count-1).Count(r=>B(r,"DiskFlush"));
        double io=F(end,"LogIoWriteDurationMsBeforeEnd").GetDouble(), serialization=F(end,"LogSerializationDurationMsBeforeEnd").GetDouble();
        Need(N(end,"LogRecordsBeforeEnd")== (ulong)rows.Count-1 && N(end,"LogDiskFlushesBeforeEnd")== (ulong)disk
            && N(end,"LogStreamFlushesBeforeEnd")==0 && N(end,"LogBufferedRecordsBeforeEnd")== (ulong)(rows.Count-1-disk)
            && double.IsFinite(io) && io>=0 && double.IsFinite(serialization) && serialization>=0
            && F(end,"LogWriteDurationMsBeforeEnd").GetDouble()==io+serialization,"V17 durability counters differ");
        Need(NativeSourceMatches(identity,source,image,owner),"V17 source differs");
        string[] allowed=["START","SOURCE","FORCE_DISPATCH","SUBMIT_CODE_PREFLIGHT_RECHECK","PACKET_CODE_PENDING",
            "CURRENT_NATIVE_CODE_GUARDS","NONINVOKED_CODE_OBSERVATION","NETWORK_SHARED_COUNTS_SAMPLE","NETWORK_SHARED_COUNTS_AB",
            "PREFLIGHT_AB_CLOSED","HOOK_INSTALL_INTENT","NATIVE_PAYLOAD","HOOK_INSTALL_RESULT","EVENT_INTENT_DURABLE",
            "COMMAND_PUBLISH_INTENT","WM_NULL_POST_INTENT","WM_NULL_POST_RESULT","EVENT_RESULT","PACKET_CODE_CONFIRMED",
            "BORROWED_SOURCE_VERIFIED","NATIVE_FINAL_STATE","UNHOOK","LOCAL_VIEW_RELEASE","LOCAL_MAPPING_CLOSE","LOCAL_LIBRARY_FREE",
            "CLEANUP","JOURNAL_SETTLED","SINGLE_TRIAL_JOURNAL_SETTLED","ERROR","END"];
        Need(rows.All(r=>allowed.Contains(T(r,"Kind"),StringComparer.Ordinal)),"Unexpected V17 event");
        var closed=rows.Where(r=>T(r,"Kind")=="PREFLIGHT_AB_CLOSED").ToArray();
        Need(closed.Length==3 && rows.Count(r=>T(r,"Kind")=="CURRENT_NATIVE_CODE_GUARDS")==3,"Three current preflight closures required");
        ulong prior=N(V17One(rows,"SOURCE"),"Sequence"),mask=0; int closureNumber=0;
        foreach(var last in closed)
        {
            var group=rows.Where(r=>N(r,"Sequence")>prior && N(r,"Sequence")<=N(last,"Sequence")).ToArray();
            mask=Group(group,image,N(identity,"Shop"),N(identity,"Owner"),owner);
            if(NewRangeProducer(start))ClosedRequestedRanges(F(last,"Data"),image,mask,new[]{"BeforeTransport","InstalledBeforeIntent","BeforeCommandPublication"}[closureNumber]);
            closureNumber++;
            foreach(var observation in group.Where(r=>T(r,"Kind")=="NONINVOKED_CODE_OBSERVATION"))
            {
                var d=F(observation,"Data");Need(Hex(T(d,"Rva")) is 0x73B790 or 0x104870
                    && !B(d,"DispatchPrerequisite") && !B(d,"BodyCallableClaimed") && !B(d,"HistoricalBytesSubstituted"),"Noninvoked body policy differs");
            }
            prior=N(last,"Sequence");
        }
        Need(!rows.Any(r=>T(r,"Kind")=="PACKET_CODE_PENDING" && N(r,"Sequence")>prior)
            && N(native,"HostDeferredMask")==mask && N(native,"NativeDeferredMask")<=3
            && N(native,"Sequence")==1 && N(native,"ObservedTid")==N(identity,"Tid"),"Native mask/thread not bound to last closure");
        V17Hook(rows,source,image,owner,start,identity);
        ulong seq(string kind)=>N(V17One(rows,kind),"Sequence");
        Need(N(closed[0],"Sequence")<seq("HOOK_INSTALL_INTENT") && seq("HOOK_INSTALL_RESULT")<N(closed[1],"Sequence")
            && N(closed[1],"Sequence")<seq("EVENT_INTENT_DURABLE") && seq("EVENT_INTENT_DURABLE")<seq("COMMAND_PUBLISH_INTENT")
            && seq("COMMAND_PUBLISH_INTENT")<seq("WM_NULL_POST_INTENT") && seq("WM_NULL_POST_INTENT")<N(closed[2],"Sequence")
            && N(closed[2],"Sequence")<seq("WM_NULL_POST_RESULT") && seq("WM_NULL_POST_RESULT")<seq("EVENT_RESULT")
            && seq("EVENT_RESULT")<seq("NATIVE_FINAL_STATE"),"V17 publication order differs");
        var intent=V17Data(rows,"EVENT_INTENT_DURABLE");var publish=V17Data(rows,"COMMAND_PUBLISH_INTENT");
        var post=V17Data(rows,"WM_NULL_POST_INTENT");var posted=V17Data(rows,"WM_NULL_POST_RESULT");
        ulong mode=T(start,"Mode")=="buy"?1UL:2UL;
        Need(T(intent,"Path")==T(end,"PendingJournal") && T(intent,"SingleTrialPending")==T(end,"SingleTrialPendingJournal")
            && N(intent,"Mode")==mode && N(intent,"Calls")==1 && B(intent,"NoAutomaticRetransmission")
            && N(publish,"Sequence")==1 && N(publish,"Nonce")==N(native,"Nonce") && N(native,"Nonce")!=0
            && N(publish,"Mode")==mode && N(publish,"TargetOwner")==owner && N(publish,"MaximumRequests")==1
            && N(post,"Sequence")==1 && N(post,"Nonce")==N(native,"Nonce") && N(post,"TargetThread")==N(identity,"Tid")
            && N(post,"Posts")==1 && B(post,"TransportOnly") && B(posted,"NoAutomaticRetransmission")
            && B(posted,"Posted") && N(posted,"Win32Error")==0,"Publication identity differs");
        foreach(string kind in new[]{"EVENT_INTENT_DURABLE","COMMAND_PUBLISH_INTENT","WM_NULL_POST_INTENT","WM_NULL_POST_RESULT","EVENT_RESULT","END"})
            Need(B(V17One(rows,kind),"DiskFlush"),"Required durable event missing");
        if(returned) Need(!rows.Any(r=>T(r,"Kind")=="ERROR"),"Returned proof contains error");
        var final=V17Data(rows,"NATIVE_FINAL_STATE");
        Need(JsonElement.DeepEquals(F(final,"State"),native) && B(final,"QuiescentObserved")
            && !B(final,"EntireCallNextHookTailExitProven"),"Native final state differs");
        V17Cleanup(rows,false);
        return true;
    }

    private static void V17Hook(IReadOnlyList<JsonElement> rows,SourceIdentity source,ulong image,ulong owner,JsonElement start,JsonElement identity)
    {
        var hook=V17Data(rows,"HOOK_INSTALL_INTENT");var payload=V17Data(rows,"NATIVE_PAYLOAD");var installed=V17Data(rows,"HOOK_INSTALL_RESULT");
        string path=T(payload,"Path");
        Need(N(V17One(rows,"HOOK_INSTALL_INTENT"),"Sequence")<N(V17One(rows,"NATIVE_PAYLOAD"),"Sequence")
            && N(V17One(rows,"NATIVE_PAYLOAD"),"Sequence")<N(V17One(rows,"HOOK_INSTALL_RESULT"),"Sequence"),"Payload installation order differs");
        Need(N(hook,"Pid")== (ulong)source.ProcessId && N(hook,"Creation")== (ulong)source.CreationFileTime
            && N(hook,"Tid")==N(identity,"Tid") && N(hook,"Hwnd")==N(identity,"Hwnd")
            && N(hook,"Mode")== (T(start,"Mode")=="buy"?1UL:2UL) && N(hook,"TargetOwner")==owner
            && Hex(T(hook,"EntryRva"))==0x287910 && T(hook,"NativePacketType")=="CS_PERSONAL_SHOP_LIST_REQ"
            && Path.IsPathFullyQualified(path) && Path.GetFileName(path)==ShopNativeHistoricalPackage.NativePayloadFile
            && ShopNativeHistoricalPackage.NativePayloadSha256.Length==64 && T(payload,"Sha256")==ShopNativeHistoricalPackage.NativePayloadSha256
            && T(payload,"Export")=="PlayerShopListHookV17" && B(payload,"NewProtocol") && Certificate(payload,false)
            && B(installed,"Installed") && N(installed,"Win32Error")==0 && T(installed,"HookType")=="WH_GETMESSAGE"
            && N(installed,"Thread")==N(identity,"Tid") && !B(installed,"GlobalHook"),"Exact V17 payload/hook differs");
    }

    private static void V17Cleanup(IReadOnlyList<JsonElement> rows,bool idle,ulong idleHostMask=0)
    {
        string[] tail=["NATIVE_FINAL_STATE","UNHOOK","LOCAL_VIEW_RELEASE","LOCAL_MAPPING_CLOSE","LOCAL_LIBRARY_FREE","CLEANUP"];
        ulong prior=0;
        foreach(string kind in tail)
        {
            var row=V17One(rows,kind);Need(prior==0 || N(row,"Sequence")==prior+1,"Cleanup tail incomplete");prior=N(row,"Sequence");
        }
        var final=V17Data(rows,"NATIVE_FINAL_STATE");var state=F(final,"State");
        Need(B(final,"QuiescentObserved") && !B(final,"EntireCallNextHookTailExitProven"),"Cleanup not quiescent");
        if(idle)
        {
            string[] fields=["Sequence","Completed","Active","Claimed","Entered","Returned","Status","Exception","ObservedPid","ObservedTid","Nonce","Tick","Callback","Module","Stage","RequestMetadataBefore","RequestMetadataAfter","RequestUnchanged","DestructorCalls","HostDeferredMask","NativeDeferredMask","PostCodeConfirmed","ExceptionStage","ExceptionAddress","ExceptionAccessAddress","ExceptionAccessKind","LeaseReleaseAttempted","LeaseReleaseCompleted","LeaseAcquired","ExceptionCount","ReleaseExceptionCode","ReleaseExceptionAddress"];
            Need(state.ValueKind==JsonValueKind.Object && state.EnumerateObject().Count()==fields.Length
                && fields.All(f=>N(state,f)==(f=="HostDeferredMask"?idleHostMask:0)) && !B(final,"DisarmedBeforeClaim"),"Unpublished native state not idle");
        }
        else Need(N(state,"Active")==0 && N(state,"ExceptionCount")==0 && N(state,"ReleaseExceptionCode")==0,"Native not quiescent");
        foreach(string kind in tail.Skip(1).Take(4))
        {
            var d=V17Data(rows,kind);Need(B(d,"Success") && (kind=="LOCAL_LIBRARY_FREE" || N(d,"Win32Error")==0),"Cleanup release failed");
        }
        Need(!B(V17Data(rows,"UNHOOK"),"InterruptsActiveCallback"),"Unhook interrupted callback");
        var cleanup=V17Data(rows,"CLEANUP");
        foreach(string key in new[]{"CleanupConfirmed","QuiescentObserved","Installed","Unhooked"})Need(B(cleanup,key),"Cleanup "+key);
        foreach(string key in new[]{"LocalViewRetained","MappingRetained","LibraryRetained","HookRetained","RemoteModuleUnloadProven","EntireCallNextHookTailExitProven"})Need(!B(cleanup,key),"Cleanup "+key);
        Need(N(cleanup,"AdditionalMessagesSent")==0,"Cleanup sent input");
    }

    private static bool ConfirmedUnpublishedV17Preflight(string text,SourceIdentity source,ulong image,ulong owner,int exitCode)
    {
        if(exitCode!=1)return false;
        var rows=Rows(text);var start=V17Data(rows,"START");var end=V17Data(rows,"END");var identity=V17Data(rows,"SOURCE");
        V17Start(start,source);Need(TerminalMatches(start,end) && NativeSourceMatches(identity,source,image,owner) && V15JournalContractMatches(end,source),"Unpublished V17 binding differs");
        var errorRow=V17One(rows,"ERROR");var error=F(errorRow,"Data");
        if(T(error,"Code")=="ContextUnavailable")return ConfirmedV17OwnerRefusal(rows,source,image,owner);
        if(rows.Any(r=>T(r,"Kind")=="PREFLIGHT_PAGE_AB_ERROR"))return ConfirmedV17PageAbRefusal(rows,source,image,owner);
        var unavailableRow=V17One(rows,"NATIVE_CODE_PAGE_UNAVAILABLE");var unavailable=F(unavailableRow,"Data");
        bool installed=T(error,"Phase")=="BeforePublish";
        Need(T(error,"Phase")=="Preflight" || installed,"Unknown unpublished phase");
        string[] expectedPrefix=rows.Count>1 && T(rows[1],"Kind")=="FORCE_DISPATCH" ? ["START","FORCE_DISPATCH","SOURCE"] : ["START","SOURCE"];
        for(int j=0;j<expectedPrefix.Length;j++)Need(T(rows[j],"Kind")==expectedPrefix[j],"Wrong preflight envelope");
        if(expectedPrefix.Length==3)Need(ValidForcePreflightJournal(F(rows[1],"Data"),end,source),"Wrong force journal");
        else Need(T(end,"PendingJournal")==Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",$"{source.ProcessId}-{source.CreationFileTime}.pending.json"),"Wrong normal journal");
        int cursor=expectedPrefix.Length; ulong installedHostMask=0;
        if(installed)
        {
            int closedIndex=rows.FindIndex(cursor,r=>T(r,"Kind")=="PREFLIGHT_AB_CLOSED");
            Need(closedIndex>=cursor,"Installed without first closure");
            var first=rows.Skip(cursor).Take(closedIndex-cursor+1).ToArray();
            V17PreflightKinds(first,true,image);installedHostMask=Group(first,image,N(identity,"Shop"),N(identity,"Owner"),owner);
            if(NewRangeProducer(start))ClosedRequestedRanges(F(rows[closedIndex],"Data"),image,installedHostMask,"BeforeTransport");
            cursor=closedIndex+1;
            foreach(string kind in new[]{"HOOK_INSTALL_INTENT","NATIVE_PAYLOAD","HOOK_INSTALL_RESULT"})Need(cursor<rows.Count && T(rows[cursor++],"Kind")==kind,"Installation sequence differs");
            V17Hook(rows,source,image,owner,start,identity);
        }
        int unavailableIndex=rows.FindIndex(r=>N(r,"Sequence")==N(unavailableRow,"Sequence"));
        Need(unavailableIndex>=cursor,"Failure sequence differs");
        var partial=rows.Skip(cursor).Take(unavailableIndex-cursor).ToArray();V17PreflightKinds(partial,false,image);_=PendingMask(partial,image);
        cursor=unavailableIndex;
        Need(T(rows[cursor++],"Kind")=="NATIVE_CODE_PAGE_UNAVAILABLE" && T(rows[cursor++],"Kind")=="ERROR","Failure order differs");
        if(installed)
        {
            foreach(string kind in new[]{"NATIVE_FINAL_STATE","UNHOOK","LOCAL_VIEW_RELEASE","LOCAL_MAPPING_CLOSE","LOCAL_LIBRARY_FREE","CLEANUP"})
                Need(cursor<rows.Count && T(rows[cursor++],"Kind")==kind,"Unpublished cleanup incomplete");
            V17Cleanup(rows,true,installedHostMask);
        }
        Need(cursor==rows.Count-1 && T(rows[cursor],"Kind")=="END","Extra event after preflight refusal");
        string rva=T(unavailable,"Rva");uint relative=checked((uint)Hex(rva));int length=relative switch {0x3FC90=>209,0x287910=>1776,0x288000=>1570,0x277CF0=>549,0x72D300=>91,0x76BEB0=>67,_=>0};
        Need(length>0 && relative is not (0x72D300 or 0x76BEB0)
            && T(unavailable,"Code")=="NativeCodeUnavailable" && T(unavailable,"Phase")=="Preflight"
            && T(error,"Type")=="NativePreflightException" && T(error,"Code")=="NativeCodeUnavailable"
            && Hex(T(error,"Rva"))==relative && T(error,"Message")=="Native code is not currently readable/executable MEM_IMAGE: "+rva
            && V16InitialUnpublishedPage(F(unavailable,"Page"),image,relative,length),"Unavailable page identity differs");
        foreach(var row in rows)Need(B(row,"DiskFlush")== (T(row,"Kind") is "ERROR" or "END"),"Unpublished durability differs");
        foreach(string key in new[]{"PostCodeConfirmed","EventResultDurable","BorrowedSourceContractVerified","NativeAlreadyReturned","AckObserved","ListReceivedVerified","RemoteModuleUnloadProven","EntireHookTailExitProven","SessionBlocked"})Need(!B(end,key),"Contradictory no-entry receipt");
        return N(end,"ExitCode")==1 && F(end,"NativeResult").ValueKind==JsonValueKind.Null && B(end,"CleanupConfirmed") && V14FlushCountersMatch(start,end,rows.Count);
    }

    private static void V17PreflightKinds(IReadOnlyList<JsonElement> rows,bool complete,ulong image)
    {
        int i=0;ulong previousRva=0,previousTotal=0;
        // Submit is exactly two body slices. A restored first page may be followed
        // by a second read-only recheck; neither record proves current body bytes.
        for(int count=0;count<2 && i<rows.Count && T(rows[i],"Kind")=="SUBMIT_CODE_PREFLIGHT_RECHECK";count++)
        {
            var d=F(rows[i++],"Data");ulong rva=Hex(T(d,"Rva"));
            ulong length=rva==0x287910?1776UL:rva==0x288000?1570UL:0;
            Need(Hex(T(d,"FunctionRva"))==0x287910 && length!=0 && rva>previousRva && N(d,"Length")==length,
                "Submit recheck slice/order differs");previousRva=rva;
            Need(B(d,"ReadOnly") && B(d,"RequestNotPublished") && !B(d,"ProtectionChanged")
                && !B(d,"Deferred") && !B(d,"CurrentBytesMatched") && B(d,"FullByteAndSourceProofStillRequired")
                && N(d,"MaximumWaitMilliseconds")==250,"Submit recheck policy differs");
            foreach(string key in new[]{"Before","After"})
            {
                var page=F(d,key);ulong at=checked(image+rva),start=N(page,"Base"),size=N(page,"Size");
                Need(N(page,"AllocationBase")==image && N(page,"State")==0x1000 && N(page,"Type")==0x1000000
                    && N(page,"AllocationProtect")<=uint.MaxValue && N(page,"PartitionId")<=ushort.MaxValue
                    && start>=image && start<=at && start-image<0x3F5F000 && size<=0x3F5F000-(start-image)
                    && size>=length && at-start<=size-length,"Submit recheck page span differs");
            }
            ulong queries=N(d,"Queries"),total=N(d,"TotalQueries");
            Need(N(F(d,"Before"),"Protect")==1 && queries<=10 && total>=queries && total<=10
                && (count==0 || total==previousTotal+queries),"Submit recheck budget/page differs");previousTotal=total;
            foreach(string key in new[]{"ElapsedMilliseconds","TotalWaitMilliseconds"})
            {double milliseconds=F(d,key).GetDouble();Need(double.IsFinite(milliseconds) && milliseconds>=0,"Submit recheck duration differs");}
            bool restored=T(d,"Outcome")=="PageChanged";
            if(restored)Need(B(d,"Attempted") && queries>0 && N(F(d,"After"),"Protect") is 0x20 or 0x40 or 0x80,"Submit page not restored");
            else Need(T(d,"Outcome")=="BudgetExhausted" && N(F(d,"After"),"Protect")==1
                && (B(d,"Attempted") || queries==0) && !complete && i==rows.Count,"Submit exhaustion cannot precede later proof");
        }
        Need(i==rows.Count || T(rows[i],"Kind")!="SUBMIT_CODE_PREFLIGHT_RECHECK","More than two Submit slices");
        while(i<rows.Count && T(rows[i],"Kind")=="PACKET_CODE_PENDING")i++;
        if(complete)foreach(string kind in new[]{"CURRENT_NATIVE_CODE_GUARDS","NONINVOKED_CODE_OBSERVATION","NONINVOKED_CODE_OBSERVATION","NETWORK_SHARED_COUNTS_SAMPLE","NETWORK_SHARED_COUNTS_SAMPLE","NETWORK_SHARED_COUNTS_AB","PREFLIGHT_AB_CLOSED"})
            Need(i<rows.Count && T(rows[i++],"Kind")==kind,"Unexpected complete preflight shape");
        Need(i==rows.Count,"Unexpected event before failure");
    }

    private static bool ConfirmedV17OwnerRefusal(IReadOnlyList<JsonElement> rows,SourceIdentity source,ulong image,ulong owner)
    {
        var identity=V17Data(rows,"SOURCE");var end=V17Data(rows,"END");var error=V17Data(rows,"ERROR");
        int i=1;
        if(T(rows[i],"Kind")=="FORCE_DISPATCH")Need(ValidForcePreflightJournal(F(rows[i++],"Data"),end,source),"Owner refusal force journal differs");
        else Need(T(end,"PendingJournal")==Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",$"{source.ProcessId}-{source.CreationFileTime}.pending.json"),"Owner refusal journal differs");
        Need(T(rows[i++],"Kind")=="SOURCE","Owner refusal source order");
        int begin=i;
        while(i<rows.Count && T(rows[i],"Kind")=="PACKET_CODE_PENDING")i++;
        Need(T(rows[i],"Kind")=="CURRENT_NATIVE_CODE_GUARDS","Owner refusal guard missing");
        _=Guard(F(rows[i++],"Data"),rows.Skip(begin).Take(i-begin-1),image);
        foreach(var spec in new[]{(Rva:"0x73B790",Length:73),(Rva:"0x104870",Length:80)})
            Need(T(rows[i],"Kind")=="NONINVOKED_CODE_OBSERVATION" && OwnerPreflightNonInvoked(F(rows[i++],"Data"),image,spec.Rva,spec.Length),"Owner refusal observation differs");
        Need(T(rows[i],"Kind")=="NETWORK_SHARED_COUNTS_SAMPLE","Owner refusal sample missing");var sample=F(rows[i++],"Data");var counts=F(sample,"Counts");
        ulong control=N(sample,"NetworkControl");
        Need(Pointer(control) && T(sample,"Phase")=="A" && Hex(T(sample,"AddressHex"))==control+8
            && N(sample,"Length")==8 && B(sample,"Alive") && !B(sample,"CountEqualityRequired")
            && N(counts,"Strong") is >0 and <int.MaxValue && N(counts,"Weak") is >0 and <int.MaxValue,"Owner refusal sample differs");
        Need(T(rows[i],"Kind")=="CURRENT_SHOP_OWNER_CHANGED","Owner refusal change missing");var changed=F(rows[i++],"Data");
        ulong before=N(identity,"Owner"),after=N(changed,"ObservedOwner");
        Need(N(changed,"ExpectedOwner")==before && before!=after && N(changed,"TargetOwner")==owner && B(changed,"RequestNotPublished")
            && T(error,"Type")=="NativePreflightException" && T(error,"Phase")=="Preflight" && F(error,"Rva").ValueKind==JsonValueKind.Null
            && T(error,"Message")==$"Current full 64-bit shop owner changed before dispatch: expected={before}, observed={after}.","Owner refusal identity differs");
        Need(i==rows.Count-2 && T(rows[i],"Kind")=="ERROR" && T(rows[i+1],"Kind")=="END","Extra owner refusal event");
        foreach(var row in rows)Need(B(row,"DiskFlush")== (T(row,"Kind") is "ERROR" or "END"),"Owner refusal durability differs");
        foreach(string key in new[]{"PostCodeConfirmed","EventResultDurable","BorrowedSourceContractVerified","NativeAlreadyReturned","AckObserved","ListReceivedVerified","RemoteModuleUnloadProven","EntireHookTailExitProven","SessionBlocked"})Need(!B(end,key),"Owner refusal contains native work");
        return B(end,"CleanupConfirmed") && F(end,"NativeResult").ValueKind==JsonValueKind.Null && N(end,"ExitCode")==1 && V14FlushCountersMatch(V17Data(rows,"START"),end,rows.Count);
    }

    internal static void ValidateV17RecoveryOriginal(string text,SourceIdentity source,ulong owner,int mode,ulong nonce,JsonElement receipt)
    {
        if (ShopNativePackage.SupportsBuild(source)) { ShopNativeB47COutcome.ValidateV17RecoveryOriginal(text,source,owner,mode,nonce,receipt); return; }
        if (ShopNativeLegacy79D1Package.SupportsBuild(source)) { ShopNative79D1Outcome.ValidateV17RecoveryOriginal(text,source,owner,mode,nonce,receipt); return; }
        // Legacy evidence keeps its original parser. Any V17 marker must match the whole V17 producer.
        bool tagged=receipt.TryGetProperty("ManagedProgramVersion",out _) || receipt.TryGetProperty("ColdCodePolicyId",out _)
            || receipt.TryGetProperty("ColdCodeCertificateSha256",out _) || receipt.TryGetProperty("MaximumDeferredMask",out _);
        if(!tagged && !text.Contains("17.0.0",StringComparison.Ordinal) && !text.Contains("17.0.1",StringComparison.Ordinal) && !text.Contains("ColdCodePolicyId",StringComparison.Ordinal)
            && !text.Contains("ColdCodeCertificateSha256",StringComparison.Ordinal))return;
        using var first=JsonDocument.Parse(text.Split('\n',StringSplitOptions.RemoveEmptyEntries).First());
        var marker=first.RootElement.GetProperty("Data");
        bool v17=Text(marker,"ManagedProgramVersion")=="17.0.0" || Text(marker,"NativePayloadVersion")=="17.0.0"
            || marker.TryGetProperty("NativeGuardContract",out var guard) && guard.ValueKind==JsonValueKind.Number && guard.TryGetInt32(out int g) && g==17
            || marker.TryGetProperty("ColdCodePolicyId",out _) || marker.TryGetProperty("ColdCodeCertificateSha256",out _);
        Need(v17 && SameManagedProducer(marker,receipt) && Certificate(receipt),"Recovery cannot downgrade V17 producer");
        var rows=Rows(text);var start=V17Data(rows,"START");var end=V17Data(rows,"END");var native=F(end,"NativeResult");
        Need(F(receipt,"OriginalEndAt").GetDateTimeOffset()==F(V17One(rows,"END"),"At").GetDateTimeOffset()
            && ShopPostCodeRecoveryProof.Same(T(receipt,"OriginalForcePendingPath"),T(end,"PendingJournal"))
            && ShopPostCodeRecoveryProof.Same(T(receipt,"OriginalSingleTrialPendingPath"),T(end,"SingleTrialPendingJournal")),
            "Recovery receipt is not bound to the original terminal time and journals");
        Need(IsProducer(start,source) && TerminalMatches(start,end) && mode is 1 or 2
            && T(start,"Mode")== (mode==1?"buy":"sell") && N(end,"ExitCode")==1
            && B(end,"CleanupConfirmed") && B(end,"SessionBlocked") && B(end,"NativeAlreadyReturned")
            && B(end,"EventResultDurable") && !B(end,"PostCodeConfirmed") && !B(end,"BorrowedSourceContractVerified")
            && !B(end,"AckObserved") && !B(end,"ListReceivedVerified") && !B(end,"RemoteModuleUnloadProven")
            && !B(end,"EntireHookTailExitProven"),"Recovery original V17 terminal differs");
        Need(N(native,"Entered")==1 && N(native,"Returned")==1 && N(native,"Completed")==1 && N(native,"Claimed")==1
            && N(native,"ObservedPid")== (ulong)source.ProcessId && N(native,"Status")==1 && N(native,"Stage")==5
            && N(native,"Nonce")==nonce && nonce!=0 && N(native,"Exception")==0 && N(native,"PostCodeConfirmed")==0
            && N(native,"LeaseAcquired")==1 && N(native,"LeaseReleaseAttempted")==1 && N(native,"LeaseReleaseCompleted")==1
            && N(native,"ReleaseExceptionCode")==0 && N(native,"RequestUnchanged")==1
            && N(native,"RequestMetadataBefore")==0 && N(native,"RequestMetadataAfter")==0 && N(native,"DestructorCalls")==0,
            "Recovery original returned/lease contract differs");
        ulong image=ShopRuntimeContext.ImageBase(source);
        Need(ConfirmedV17CurrentEvidence(text,source,image,owner,native,false),"Recovery original current proofs differ");
        Need(!rows.Any(r=>T(r,"Kind") is "PACKET_CODE_CONFIRMED" or "BORROWED_SOURCE_VERIFIED" or "JOURNAL_SETTLED" or "SINGLE_TRIAL_JOURNAL_SETTLED"),
            "Recovery original contains a contradictory settled result");
        var result=V17Data(rows,"EVENT_RESULT");var error=V17Data(rows,"ERROR");
        Need(B(V17One(rows,"ERROR"),"DiskFlush"),"Recovery original error not durable");
        Need(B(result,"NativeReturned") && !B(result,"PostCodeConfirmed") && B(result,"PostReadBeforeDurableResult")
            && !B(result,"ListReceivedVerified") && !B(result,"OwnerUpdatedByAckVerified")
            && N(result,"TargetOwner")==owner && JsonElement.DeepEquals(F(result,"State"),native)
            && T(result,"PostCodeErrorCode")=="PostCodeUnconfirmed" && Hex(T(result,"PostCodeErrorRva")) is 0x72D300 or 0x76BEB0
            && T(error,"Type")=="NativePreflightException" && T(error,"Phase")=="PostCode"
            && T(error,"Code")=="PostCodeUnconfirmed" && Hex(T(error,"Rva"))==Hex(T(result,"PostCodeErrorRva")),
            "Recovery original is not a returned post-code-unavailable attempt");
        var recheck=F(result,"PostCodeRecheck");
        Need(N(recheck,"Contract")==1 && B(recheck,"ReadOnly") && N(recheck,"NativeRequestsRetried")==0
            && T(recheck,"Outcome")=="BudgetExhausted" && F(result,"PostProof").ValueKind==JsonValueKind.Array
            && F(result,"PostProof").GetArrayLength()==0,"Recovery original post proof was not pending");
    }
}
