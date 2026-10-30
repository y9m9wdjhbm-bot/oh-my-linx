using System.Text.Json;
using static MemoryHub.Service.ShopNativeB47CContract;
namespace MemoryHub.Service;

internal sealed partial record ShopNativeB47COutcome
{
    internal static bool V17ReadOnlyPageAbRefusal(IReadOnlyList<JsonElement> rows,SourceIdentity source,ulong image)
    {
        var start=V17Data(rows,"START");var end=V17Data(rows,"END");var error=V17Data(rows,"ERROR");var page=V17Data(rows,"PREFLIGHT_PAGE_AB_ERROR");
        Need(IsProducer(start,source) && NewRangeProducer(start) && TerminalMatches(start,end)
            && PurposeMatches(start,ShopNativeLogPurpose.ReadOnlyPreflight) && T(rows[1],"Kind")=="SOURCE",
            "Readonly page refusal producer/purpose differs");
        int at=rows.ToList().FindIndex(r=>T(r,"Kind")=="PREFLIGHT_PAGE_AB_ERROR");
        Need(at>=2 && at==rows.Count-3 && T(rows[at+1],"Kind")=="ERROR","Readonly page refusal shape differs");
        ulong mask=V17PageFailureGroup(rows.Skip(2).Take(at-2).ToArray(),image),rva=Hex(T(page,"Rva"));
        var spec=RequestedRanges.SingleOrDefault(s=>s.Rva==rva && (ulong)s.Length==N(page,"Length"));Need(spec is not null,"Unknown readonly range");
        RequestedRange(page,image,spec!,mask,"BeforeTransport",false,false);
        Need(T(error,"Type")=="NativePreflightException" && T(error,"Code")=="NativePreflightPageChanged"
            && T(error,"Phase")=="Preflight" && Hex(T(error,"Rva"))==rva && N(error,"PageRangeProofContract")==2
            && T(error,"PreflightStage")=="BeforeTransport" && B(error,"RequestNotPublished")
            && T(error,"Message")==$"Current requested code range page proof changed: address=0x{image+rva:X} length={spec!.Length}.",
            "Readonly typed refusal differs");
        V17Durability(rows);return true;
    }
    private static void V17Durability(IReadOnlyList<JsonElement> rows)
    {
        var end=V17Data(rows,"END");ulong disk=(ulong)rows.Take(rows.Count-1).Count(r=>B(r,"DiskFlush"));
        double io=F(end,"LogIoWriteDurationMsBeforeEnd").GetDouble(),serial=F(end,"LogSerializationDurationMsBeforeEnd").GetDouble();
        Need(N(end,"LogRecordsBeforeEnd")== (ulong)rows.Count-1 && N(end,"LogDiskFlushesBeforeEnd")==disk
            && N(end,"LogStreamFlushesBeforeEnd")==0 && N(end,"LogBufferedRecordsBeforeEnd")== (ulong)rows.Count-1-disk
            && double.IsFinite(io) && io>=0 && double.IsFinite(serial) && serial>=0
            && F(end,"LogWriteDurationMsBeforeEnd").GetDouble()==io+serial,"Preflight durability counters differ");
        foreach(var row in rows)Need(B(row,"DiskFlush")== (T(row,"Kind") is "PREFLIGHT_PAGE_AB_ERROR" or "ERROR" or "END"),"Preflight row durability differs");
    }
    private static ulong V17PageFailureGroup(IReadOnlyList<JsonElement> rows,ulong image)
    {
        int prefix=0;while(prefix<rows.Count && T(rows[prefix],"Kind") is "SUBMIT_CODE_PREFLIGHT_RECHECK" or "PACKET_CODE_PENDING")prefix++;
        V17PreflightKinds(rows.Take(prefix).ToArray(),false,image);
        string[] suffix=["CURRENT_NATIVE_CODE_GUARDS","NONINVOKED_CODE_OBSERVATION","NONINVOKED_CODE_OBSERVATION","NETWORK_SHARED_COUNTS_SAMPLE","NETWORK_SHARED_COUNTS_SAMPLE","NETWORK_SHARED_COUNTS_AB"];
        Need(rows.Count==prefix+suffix.Length,"Page failure preflight group incomplete");
        for(int i=0;i<suffix.Length;i++)Need(T(rows[prefix+i],"Kind")==suffix[i],"Page failure preflight group order differs");
        ulong mask=Guard(F(rows[prefix],"Data"),rows.Take(prefix),image);
        Need(OwnerPreflightNonInvoked(F(rows[prefix+1],"Data"),image,"0x748700",73)
            && OwnerPreflightNonInvoked(F(rows[prefix+2],"Data"),image,"0x105270",80),"Noninvoked observation differs");
        var a=F(rows[prefix+3],"Data");var b=F(rows[prefix+4],"Data");var ab=F(rows[prefix+5],"Data");ulong control=N(ab,"NetworkControl");
        Need(Pointer(control) && V16InstalledCounts(a,"A",control,F(ab,"OwnCountsA")) && V16InstalledCounts(b,"B",control,F(ab,"OwnCountsB"))
            && Hex(T(ab,"CountsAddress"))==checked(control+8) && N(ab,"Length")==8 && B(ab,"StableControlVtableAbClosed")
            && !B(ab,"CountEqualityRequired") && B(ab,"CountsChanged")==!JsonElement.DeepEquals(F(ab,"OwnCountsA"),F(ab,"OwnCountsB")),
            "Ownership A/B group incomplete");
        return mask;
    }
    private static bool ConfirmedV17PageAbRefusal(IReadOnlyList<JsonElement> rows,SourceIdentity source,ulong image,ulong owner)
    {
        var start=V17Data(rows,"START");var end=V17Data(rows,"END");var identity=V17Data(rows,"SOURCE");
        V17Start(start,source);Need(TerminalMatches(start,end) && NativeSourceMatches(identity,source,image,owner)
            && V15JournalContractMatches(end,source),"Page refusal producer/source/journal differs");
        int cursor=1;
        if(T(rows[cursor],"Kind")=="FORCE_DISPATCH")Need(ValidForcePreflightJournal(F(rows[cursor++],"Data"),end,source),"Page refusal force journal differs");
        else Need(T(end,"PendingJournal")==System.IO.Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",$"{source.ProcessId}-{source.CreationFileTime}.pending.json"),"Page refusal journal differs");
        Need(T(rows[cursor++],"Kind")=="SOURCE" && N(identity,"Tid")>0 && N(identity,"Hwnd")>0 && Pointer(N(identity,"Shop")),"Page refusal source order/pointers differ");
        var pageRow=V17One(rows,"PREFLIGHT_PAGE_AB_ERROR");var page=F(pageRow,"Data");var error=V17Data(rows,"ERROR");
        bool modern=NewRangeProducer(start);string stage=modern?T(page,"PreflightStage"):"BeforeTransport";
        Need(stage is "BeforeTransport" or "InstalledBeforeIntent","Publication-intent stage remains uncertain");
        bool installed=stage=="InstalledBeforeIntent";
        Need(T(error,"Phase")== (installed?"BeforePublish":"Preflight"),"Failure phase/stage differ");
        ulong installedMask=0;
        if(installed)
        {
            int closure=rows.ToList().FindIndex(cursor,r=>T(r,"Kind")=="PREFLIGHT_AB_CLOSED");Need(closure>=cursor,"Missing complete initial closure");
            var first=rows.Skip(cursor).Take(closure-cursor+1).ToArray();V17PreflightKinds(first,true,image);
            installedMask=Group(first,image,N(identity,"Shop"),N(identity,"Owner"),owner);
            ClosedRequestedRanges(F(rows[closure],"Data"),image,installedMask,"BeforeTransport");cursor=closure+1;
            foreach(string kind in new[]{"HOOK_INSTALL_INTENT","NATIVE_PAYLOAD","HOOK_INSTALL_RESULT"})Need(T(rows[cursor++],"Kind")==kind,"Installation order differs");
            V17Hook(rows,source,image,owner,start,identity);
        }
        int errorAt=rows.ToList().FindIndex(r=>T(r,"Kind")=="PREFLIGHT_PAGE_AB_ERROR");Need(errorAt>=cursor,"Failure group missing");
        ulong mask=V17PageFailureGroup(rows.Skip(cursor).Take(errorAt-cursor).ToArray(),image);cursor=errorAt;
        Need(T(rows[cursor++],"Kind")=="PREFLIGHT_PAGE_AB_ERROR" && T(rows[cursor++],"Kind")=="ERROR","Failure tail order differs");
        if(modern)
        {
            ulong rva=Hex(T(page,"Rva"));var spec=RequestedRanges.SingleOrDefault(s=>s.Rva==rva && (ulong)s.Length==N(page,"Length"));
            Need(spec is not null,"Unknown failure requested range");RequestedRange(page,image,spec!,mask,stage,false,false);
            Need(T(error,"Type")=="NativePreflightException" && T(error,"Code")=="NativePreflightPageChanged"
                && Hex(T(error,"Rva"))==rva && N(error,"PageRangeProofContract")==2 && T(error,"PreflightStage")==stage
                && B(error,"RequestNotPublished") && T(error,"Message")==$"Current requested code range page proof changed: address=0x{image+rva:X} length={spec!.Length}.","Typed page refusal differs");
        }
        else
        {
            Need(rows.Count==12 && T(rows[1],"Kind")=="FORCE_DISPATCH" && mask==0 && errorAt==9,
                "Legacy path is restricted to the observed twelve-row producer shape");
            var a=F(page,"Before");var b=F(page,"After");PageShape(a,image);PageShape(b,image);ulong at=checked(image+0x27CA10);
            Need(page.EnumerateObject().Count()==6 && T(page,"Category")=="NativeCodePage" && N(page,"Address")==at
                && Hex(T(page,"AddressHex"))==at && N(page,"Length")==549 && RangeCovered(a,at,549) && RangeCovered(b,at,549)
                && StablePageAttributes(a,b) && N(a,"Size")!=N(b,"Size") && N(a,"AllocationBase")==image
                && N(a,"State")==0x1000 && N(a,"Type")==0x1000000 && N(a,"Protect") is 0x20 or 0x40 or 0x80
                && T(error,"Type")=="InvalidOperationException" && T(error,"Code")=="Unknown" && F(error,"Rva").ValueKind==JsonValueKind.Null
                && T(error,"Message")==$"Current code page metadata A/B changed: NativeCodePage address=0x{at:X} length=549.","Legacy failure is not exact covered Size-only observation");
        }
        if(installed)
        {
            foreach(string kind in new[]{"NATIVE_FINAL_STATE","UNHOOK","LOCAL_VIEW_RELEASE","LOCAL_MAPPING_CLOSE","LOCAL_LIBRARY_FREE","CLEANUP"})Need(T(rows[cursor++],"Kind")==kind,"Installed cleanup tail missing");
            V17Cleanup(rows,true,installedMask);
        }
        Need(cursor==rows.Count-1 && T(rows[cursor],"Kind")=="END","Unexpected event, intent or publication");
        foreach(string key in new[]{"PostCodeConfirmed","EventResultDurable","BorrowedSourceContractVerified","NativeAlreadyReturned","AckObserved","ListReceivedVerified","RemoteModuleUnloadProven","EntireHookTailExitProven","SessionBlocked"})Need(!B(end,key),"Contradictory no-entry terminal");
        V17Durability(rows);
        return N(end,"ExitCode")==1 && F(end,"NativeResult").ValueKind==JsonValueKind.Null && B(end,"CleanupConfirmed");
    }
}
