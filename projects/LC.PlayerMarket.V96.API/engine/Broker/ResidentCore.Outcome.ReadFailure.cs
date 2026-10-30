using System.Globalization;
using System.Text.Json;
namespace MemoryHub.Service;

internal sealed partial record ShopResidentNativeOutcome
{
    // An explicit producer latch and a complete ordered log are required.
    // Cleanup=true / NativeResult=null alone must never establish no dispatch.
    private static bool TryUnpublishedReadFailure(string text,SourceIdentity source,ulong image,ulong owner,int requestOutcomeCode,out ShopFailure? failure)
    {
        failure=null;
        var rows=new List<JsonElement>();
        foreach(string line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            using var doc=JsonDocument.Parse(line);
            if(!UniqueProperties(doc.RootElement))return false;
            rows.Add(doc.RootElement.Clone());
        }
        if(requestOutcomeCode!=1||rows.Count<4||rows.Count>8192)return false;
        for(int i=0;i<rows.Count;i++)
            if(rows[i].GetProperty("Sequence").GetInt32()!=i+1)return false;
        if(Text(rows[0],"Kind")!="START"||Text(rows[1],"Kind")!="SOURCE"
            ||Text(rows[^2],"Kind")!="ERROR"||Text(rows[^1],"Kind")!="END")return false;
        var start=rows[0].GetProperty("Data");var identity=rows[1].GetProperty("Data");
        var error=rows[^2].GetProperty("Data");var end=rows[^1].GetProperty("Data");
        if(Text(start,"Version")!="13.0.0"||Text(start,"NativePayloadVersion")!="13.0.0"
            ||start.GetProperty("NativeProtocolVersion").GetInt32()!=13||start.GetProperty("NativeGuardContract").GetInt32()!=13
            ||start.GetProperty("MaximumNativeRequests").GetInt32()!=1||Text(start,"Mode") is not("buy" or "sell")
            ||start.GetProperty("MouseMessages").GetInt32()!=0||start.GetProperty("KeyMessages").GetInt32()!=0
            ||start.GetProperty("TouchCalls").GetInt32()!=0||start.GetProperty("ForegroundChanges").GetInt32()!=0
            ||start.GetProperty("DirectSelectedOwnerWrites").GetInt32()!=0||Text(start,"OpenProcessAccess")!="0x410")return false;
        if(identity.GetProperty("Pid").GetInt32()!=source.ProcessId||identity.GetProperty("Creation").GetInt64()!=source.CreationFileTime
            ||identity.GetProperty("Image").GetUInt64()!=image||identity.GetProperty("TargetOwner").GetUInt64()!=owner
            ||Text(identity,"Sha256")!=source.DiskSha256||!string.Equals(Text(identity,"Path"),source.ImagePath,StringComparison.OrdinalIgnoreCase)
            ||Rva(Text(identity,"Timestamp"))!=$"0x{source.PeTimestamp:X}"||Rva(Text(identity,"ImageSize"))!=$"0x{source.ImageSize:X}"
            ||!identity.GetProperty("ReadOnlyProcessHandle").GetBoolean())return false;
        if(Text(error,"Phase")!="Preflight"||end.GetProperty("ExitCode").GetInt32()!=1
            ||end.GetProperty("NativeResult").ValueKind!=JsonValueKind.Null||!end.GetProperty("CleanupConfirmed").GetBoolean()
            ||end.GetProperty("SessionBlocked").GetBoolean()||end.GetProperty("PostCodeConfirmed").GetBoolean()
            ||end.GetProperty("BorrowedSourceContractVerified").GetBoolean()||end.GetProperty("NativeAlreadyReturned").GetBoolean()
            ||end.GetProperty("AckObserved").GetBoolean()||end.GetProperty("ListReceivedVerified").GetBoolean()
            ||end.GetProperty("LogRecordsBeforeEnd").GetInt32()!=rows.Count-1)return false;
        int disk=0,stream=0;
        for(int i=0;i<rows.Count-1;i++){if(rows[i].GetProperty("DiskFlush").GetBoolean())disk++;else stream++;}
        if(!rows[^2].GetProperty("DiskFlush").GetBoolean()||!rows[^1].GetProperty("DiskFlush").GetBoolean()
            ||end.GetProperty("LogDiskFlushesBeforeEnd").GetInt32()!=disk||end.GetProperty("LogStreamFlushesBeforeEnd").GetInt32()!=stream)return false;

        bool modern=Text(start,"ManagedVersion")=="13.0.1"
            &&start.TryGetProperty("PreflightDiagnosticContract",out var contract)&&contract.GetInt32()==1;
        string category="UnknownLegacyRead";bool localTransient=false;
        if(modern)
        {
            if(Text(error,"Type")!="SourceReadException"||Text(error,"Code")!="SourceReadIncomplete"
                ||!end.TryGetProperty("PreflightNoDispatchProof",out var proof)||proof.ValueKind!=JsonValueKind.Object
                ||proof.GetProperty("Contract").GetInt32()!=1||Text(proof,"Stage")!="InitialVerify"
                ||!proof.GetProperty("SourceEstablished").GetBoolean()||proof.GetProperty("TransportConstructionAttempted").GetBoolean()
                ||proof.GetProperty("DurableIntentWritten").GetBoolean()||proof.GetProperty("RequestPublicationPossible").GetBoolean())return false;
            var read=error.GetProperty("ReadFailure");int count=read.GetProperty("RequestedBytes").GetInt32();
            ulong address=read.GetProperty("Address").GetUInt64(),actual=read.GetProperty("ActualBytes").GetUInt64();
            int calls=read.GetProperty("ReadCall").GetInt32(),win32=read.GetProperty("Win32Error").GetInt32();
            category=Text(read,"Category")??"";
            if(count is <=0 or >65536||address<0x10000||address>0x7FFFFFFFFFFFUL-(uint)count
                ||calls is <=0 or >8192||actual>(uint)count||win32<0||category.Length is 0 or >128
                ||read.GetProperty("ApiSucceeded").GetBoolean()&&actual==(uint)count)return false;
            for(int i=2;i<rows.Count-2;i++)
                if(Text(rows[i],"Kind") is not("CURRENT_NATIVE_CODE_GUARDS" or "NONINVOKED_CODE_OBSERVATION"
                    or "NETWORK_SHARED_COUNTS_SAMPLE" or "NETWORK_SHARED_COUNTS_AB" or "PACKET_PENDING_RESOLVED_DURING_PREFLIGHT"
                    or "SUBMIT_CODE_PREFLIGHT_RECHECK" or "PACKET_CODE_PENDING"))return false;
            string baseCategory=category.EndsWith("/AB",StringComparison.Ordinal)?category[..^3]:category;
            localTransient=baseCategory is "NetworkRequestChild" or "NetworkConnectionTreeHead" or "NetworkConnectionTreeRoot"
                or "NetworkConnectionNodeFlag" or "CurrentShopOwner64";
        }
        else
        {
            // Exact observed V13 eight-record path. First VerifyFull precedes
            // transport construction; the second runs with phase BeforePublish.
            // No generic legacy RPM error, null result, or absence-only proof.
            string[] kinds=["START","SOURCE","CURRENT_NATIVE_CODE_GUARDS","NONINVOKED_CODE_OBSERVATION",
                "NONINVOKED_CODE_OBSERVATION","NETWORK_SHARED_COUNTS_SAMPLE","ERROR","END"];
            if(rows.Count!=kinds.Length||disk!=1||stream!=6||Text(error,"Type")!="InvalidOperationException"
                ||Text(error,"Code")!="Unknown"||Text(error,"Message")!="ReadProcessMemory incomplete: 299")return false;
            for(int i=0;i<kinds.Length;i++)if(Text(rows[i],"Kind")!=kinds[i]||rows[i].GetProperty("DiskFlush").GetBoolean()!=(i>=6))return false;
            var guards=rows[2].GetProperty("Data");
            if(guards.GetProperty("Bodies").GetInt32()!=5||guards.GetProperty("Bytes").GetInt32()!=4262
                ||guards.GetProperty("DeferredMask").GetInt32()!=0||guards.GetProperty("DeferredCodeBytes").GetInt32()!=0
                ||guards.GetProperty("CurrentPendingBytesClaimedMatched").GetBoolean()||!guards.GetProperty("BorrowedScalarSource").GetBoolean()
                ||guards.GetProperty("AdditionalDestructorCalls").GetInt32()!=0)return false;
            for(int i=3;i<=4;i++)
            {
                var item=rows[i].GetProperty("Data");
                if(Rva(Text(item,"Rva"))!=(i==3?"0x713A40":"0x102A00")||item.GetProperty("DispatchPrerequisite").GetBoolean()
                    ||item.GetProperty("BodyCallableClaimed").GetBoolean()||item.GetProperty("HistoricalBytesSubstituted").GetBoolean())return false;
            }
            var counts=rows[5].GetProperty("Data");ulong control=counts.GetProperty("NetworkControl").GetUInt64();
            if(Text(counts,"Phase")!="A"||control<0x10000||(control&7)!=0||counts.GetProperty("Length").GetInt32()!=8
                ||!counts.GetProperty("Alive").GetBoolean()||counts.GetProperty("CountEqualityRequired").GetBoolean()
                ||Text(counts,"AddressHex")!=$"0x{control+8:X}")return false;
            uint strong=counts.GetProperty("Counts").GetProperty("Strong").GetUInt32(),weak=counts.GetProperty("Counts").GetProperty("Weak").GetUInt32();
            if(strong is 0 or >=int.MaxValue||weak is 0 or >=int.MaxValue)return false;
        }
        string detail=$"已由完整前置 LOG 證實尚未建立派送；讀取欄位={category}。"+(Text(error,"Message")??"");
        if(!modern)detail+=" 舊 LOG 未記錄失敗位址與長度，保留批次停止，不能推定可自動重試。";
        failure=new("NativePreflightReadUnavailable",localTransient?"Page":"Global","Preflight",null,detail);
        return true;
    }
}
