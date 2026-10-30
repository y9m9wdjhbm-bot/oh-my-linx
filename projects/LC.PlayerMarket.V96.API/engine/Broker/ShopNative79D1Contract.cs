using System.Globalization;
using System.Text.Json;

namespace MemoryHub.Service;

/// <summary>Exact producer contract. Wire 15 alone never grants the certified cold policy.</summary>
internal static partial class ShopNative79D1Contract
{
    internal const string Policy = "79D1 exact seven saved AB bodies; every invoked body must currently be readable/executable and match; no deferred NOACCESS; strict postbody; maximum mask0";
    internal static bool IsProducer(JsonElement start, SourceIdentity source)
    {
        try
        {
            return ShopNativeLegacy79D1Package.SupportsBuild(source) && T(start,"Version")=="15.0.0"
                && IsManagedProducer17(start) && T(start,"NativePayloadVersion")=="17.0.0"
                && N(start,"NativeProtocolVersion")==15 && N(start,"NativeGuardContract")==17
                && T(start,"DeferredCodePolicy")==Policy && Certificate(start);
        }
        catch(Exception e) when(e is InvalidDataException or InvalidOperationException or FormatException or OverflowException) { return false; }
    }
    internal static bool Certificate(JsonElement value, bool maximum = true) =>
        T(value,"ColdCodePolicyId")==ShopNativeCurrent79D1Policy.Id
        && T(value,"ColdCodeCertificateSha256")==ShopNativeCurrent79D1Policy.CertificateSha256
        && (!maximum || N(value,"MaximumDeferredMask")==ShopNativeCurrent79D1Policy.MaximumMask);
    internal static int PendingBytes(ulong mask) => (int)(((mask & 1)!=0 ? 91 : 0) + ((mask & 2)!=0 ? 67 : 0));
    internal static ulong PendingMask(IEnumerable<JsonElement> rows, ulong image)
    {
        Need(!rows.Any(r=>T(r,"Kind")=="PACKET_CODE_PENDING"),"Current 79D1 producer has no cold/deferred authority");
        return 0;
    }

    internal static ulong Guard(JsonElement guard, IEnumerable<JsonElement> pending, ulong image)
    {
        ulong mask=PendingMask(pending,image);
        Need(mask==0 && Certificate(guard) && N(guard,"Bodies")==5 && N(guard,"Bytes")==4262
            && N(guard,"DeferredMask")==mask && N(guard,"DeferredCodeBytes")== (ulong)PendingBytes(mask)
            && !B(guard,"CurrentPendingBytesClaimedMatched") && B(guard,"BorrowedScalarSource")
            && N(guard,"AdditionalDestructorCalls")==0,"Guard does not match certified pending bodies");
        return mask;
    }

    internal static void Closed(JsonElement closed, ulong image, ulong shop, ulong owner, ulong target, ulong mask)
    {
        Need(Certificate(closed) && N(closed,"Owner64")==owner && N(closed,"CurrentShop")==shop
            && N(closed,"TargetOwner")==target && N(closed,"PacketCreate")==checked(image+0x731AA0)
            && N(closed,"PacketCopy")==checked(image+0x7710F0)
            && N(closed,"ProofCount")>0 && N(closed,"ReadCalls") is >0 and <=8192
            && N(closed,"ReadBytes") is >0 and <=4*1024*1024 && N(closed,"SubsystemCount") is >0 and <=256
            && N(closed,"DeferredMask")==mask && N(closed,"DeferredCodeBytes")== (ulong)PendingBytes(mask)
            && B(closed,"EverySynchronousGuardBodyCurrentlyMatched")== (mask==0)
            && B(closed,"ColdCodeBytesPending")== (mask!=0) && B(closed,"AsyncAckNotInvokedByHelper")
            && !B(closed,"CountEqualityRequired"),"Certified source/code closure differs");
    }

    internal static ulong Group(IReadOnlyList<JsonElement> rows, ulong image, ulong shop, ulong owner, ulong target)
    {
        JsonElement One(string kind) { var found=rows.Where(r=>T(r,"Kind")==kind).ToArray(); Need(found.Length==1,"Missing/duplicate "+kind); return found[0]; }
        var gr=One("CURRENT_NATIVE_CODE_GUARDS"); var cr=One("PREFLIGHT_AB_CLOSED");
        var pending=rows.Where(r=>T(r,"Kind")=="PACKET_CODE_PENDING").ToArray();
        Need(pending.All(r=>N(r,"Sequence")<N(gr,"Sequence")),"Pending observation follows guard");
        ulong mask=Guard(F(gr,"Data"),pending,image); var closed=F(cr,"Data"); Closed(closed,image,shop,owner,target,mask);
        foreach(var recheck in rows.Where(r=>T(r,"Kind")=="SUBMIT_CODE_PREFLIGHT_RECHECK"))
        {
            var d=F(recheck,"Data");
            Need(B(d,"ReadOnly") && B(d,"RequestNotPublished") && !B(d,"ProtectionChanged") && !B(d,"Deferred")
                && !B(d,"CurrentBytesMatched") && B(d,"FullByteAndSourceProofStillRequired")
                && N(d,"MaximumWaitMilliseconds")==250 && N(recheck,"Sequence")<N(gr,"Sequence"),"Recheck changed certified policy");
        }
        var observations=rows.Where(r=>T(r,"Kind")=="NONINVOKED_CODE_OBSERVATION").ToArray();
        Need(observations.Length==2 && Hex(T(F(observations[0],"Data"),"Rva"))==0x740090
            && Hex(T(F(observations[1],"Data"),"Rva"))==0x104CA0,"Noninvoked observations missing");
        foreach(var observation in observations)
        {
            var d=F(observation,"Data");Need(!B(d,"DispatchPrerequisite") && !B(d,"BodyCallableClaimed")
                && !B(d,"HistoricalBytesSubstituted") && N(observation,"Sequence")>N(gr,"Sequence"),"Noninvoked observation changed policy");
        }
        var samples=rows.Where(r=>T(r,"Kind")=="NETWORK_SHARED_COUNTS_SAMPLE").ToArray();
        Need(samples.Length==2,"Ownership samples missing");
        Need(N(observations[0],"Sequence")<N(observations[1],"Sequence") && N(observations[1],"Sequence")<N(samples[0],"Sequence"),"Noninvoked observation order differs");
        var ar=One("NETWORK_SHARED_COUNTS_AB"); var ab=F(ar,"Data");
        ulong control=N(closed,"NetworkControl"); Need(Pointer(control) && Pointer(N(closed,"Network")) && Pointer(N(closed,"NetworkChild")),"Network pointers missing");
        bool Equal(JsonElement a,JsonElement b)=>N(a,"Strong")==N(b,"Strong") && N(a,"Weak")==N(b,"Weak");
        for(int i=0;i<2;i++)
        {
            var d=F(samples[i],"Data"); var counts=F(d,"Counts");
            Need(T(d,"Phase")== (i==0?"A":"B") && N(d,"NetworkControl")==control
                && Hex(T(d,"AddressHex"))==checked(control+8) && N(d,"Length")==8 && B(d,"Alive")
                && !B(d,"CountEqualityRequired") && N(counts,"Strong") is >0 and <int.MaxValue
                && N(counts,"Weak") is >0 and <int.MaxValue
                && Equal(counts,F(closed,i==0?"OwnCountsA":"OwnCountsB"))
                && Equal(counts,F(ab,i==0?"OwnCountsA":"OwnCountsB")),"Ownership A/B sample differs");
        }
        Need(N(ab,"NetworkControl")==control && Hex(T(ab,"CountsAddress"))==checked(control+8)
            && N(ab,"Length")==8 && B(ab,"StableControlVtableAbClosed") && !B(ab,"CountEqualityRequired")
            && B(ab,"CountsChanged")==!Equal(F(ab,"OwnCountsA"),F(ab,"OwnCountsB"))
            && N(gr,"Sequence")<N(samples[0],"Sequence") && N(samples[0],"Sequence")<N(samples[1],"Sequence")
            && N(samples[1],"Sequence")<N(ar,"Sequence") && N(ar,"Sequence")<N(cr,"Sequence"),"Ownership closure/order differs");
        return mask;
    }

    internal static List<JsonElement> Rows(string text)
    {
        Need(text.EndsWith('\n'),"Truncated native receipt");
        var rows=new List<JsonElement>(); DateTimeOffset prior=DateTimeOffset.MinValue;
        foreach(string line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            if(string.IsNullOrWhiteSpace(line)) continue;
            using var doc=JsonDocument.Parse(line); var r=doc.RootElement;
            bool timeParsed=DateTimeOffset.TryParse(T(r,"At"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var at);
            Need(ShopPostCodeRecoveryProof.Unique(r) && r.ValueKind==JsonValueKind.Object
                && N(r,"Sequence")== (ulong)rows.Count+1 && F(r,"Data").ValueKind==JsonValueKind.Object
                && timeParsed && at!=default && at>=prior,"Native envelope invalid"); prior=at; rows.Add(r.Clone());
            Need(rows.Count<=8192,"Native row limit");
        }
        Need(rows.Count>=3 && T(rows[0],"Kind")=="START" && T(rows[^1],"Kind")=="END","Native envelope order");
        return rows;
    }
    internal static bool Pointer(ulong p)=>p>=0x10000 && (p&7)==0 && p<=0x7FFFFFFFFFFFUL-16;
    internal static JsonElement F(JsonElement v,string k)=>v.ValueKind==JsonValueKind.Object && v.TryGetProperty(k,out var r)?r:throw new InvalidDataException("Missing "+k);
    internal static string T(JsonElement v,string k) {var r=F(v,k);Need(r.ValueKind==JsonValueKind.String,"Not text "+k);return r.GetString()!;}
    internal static ulong N(JsonElement v,string k) {var r=F(v,k);Need(r.ValueKind==JsonValueKind.Number&&r.TryGetUInt64(out _),"Not unsigned "+k);return r.GetUInt64();}
    internal static bool B(JsonElement v,string k) {var r=F(v,k);Need(r.ValueKind is JsonValueKind.True or JsonValueKind.False,"Not Boolean "+k);return r.GetBoolean();}
    internal static ulong Hex(string s)=>ulong.Parse(s.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?s[2..]:s,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
    internal static void Need(bool value,string detail) {if(!value)throw new InvalidDataException(detail);}
}
