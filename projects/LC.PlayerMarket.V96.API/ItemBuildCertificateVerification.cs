using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;

namespace LcPlayerMarket;

internal static class ItemBuildCertificateVerification
{
    private const ulong Image=0x10000000;
    private const string Build=PersonalShopBuildCertificate.BuildSha256;
    internal static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action body){try{body();checks.Add(new("item_build_certificate_"+name,true,"Synthetic only; LC reads=0; native calls=0; input=0."));}catch(Exception error){checks.Add(new("item_build_certificate_"+name,false,error.ToString()));}}
        Check("embedded_exact_certificate_and_tamper",()=>
        {
            Need(PersonalShopBuildCertificate.IsValid,"Embedded certificate hash invalid");
            using var stream=typeof(PersonalShopBuildCertificate).Assembly.GetManifestResourceStream("MemoryHub.ItemStateCertificate38A7V1")!;
            using var bytes=new MemoryStream();stream.CopyTo(bytes);byte[] data=bytes.ToArray();Need(PersonalShopBuildCertificate.VerifyBytes(data),"Exact certificate rejected");
            data[^1]^=1;Need(!PersonalShopBuildCertificate.VerifyBytes(data),"Changed certificate accepted");
            Need(!PersonalShopBuildCertificate.VerifyBytes(data[..^1])&&!PersonalShopBuildCertificate.VerifyBytes([]),"Truncated/missing certificate accepted");
        });
        Check("noaccess_reads_only_seven_metadata_spans",()=>
        {
            var f=new Fixture();var result=f.Capture();
            Need(result.MetadataVerified&&!result.RuntimeVerified&&!result.StableContradiction,"Wrong proof tier");
            Need(f.Reads.Count==14&&f.Reads.Sum(r=>r.Count)==112&&!f.Reads.Any(r=>r.Address==Image+0x51C2F0),"NOACCESS code read or wrong bounded metadata set");
            Need(PersonalShopSchemaVerifier.ClosePages(result,f.Query),"Final page closure failed");
        });
        Check("readable_code_retains_runtime_path",()=>
        {
            var f=new Fixture{CodeProtection=0x20};var r=f.Capture();
            Need(r.RuntimeVerified&&r.MetadataVerified&&!r.StableContradiction&&f.Reads.Count==16,"Runtime path changed tier");
        });
        foreach(string mutation in new[]{"formatter","pdata","string","descriptor-pointer","descriptor-length"})Check("stable_"+mutation+"_contradiction",()=>
        {
            var f=new Fixture{CodeProtection=mutation=="formatter"?0x20u:1u};
            ulong address=Image+(mutation switch{"formatter"=>0x51C2F0u,"pdata"=>0x3EA7484u,"string"=>0x121BB48u,_=>0x121BB50u});
            f.Bytes[address][mutation=="descriptor-length"?8:0]^=1;var r=f.Capture();
            Need(r.StableContradiction&&!r.MetadataVerified&&!r.RuntimeVerified,"Stable contradiction became fallback");
        });
        Check("short_read_is_unknown_not_permanent_contradiction",()=>
        {
            var f=new Fixture();f.ReadOverride=(a,n)=>f.Bytes[a][..^1];var r=f.Capture();Need(!r.MetadataVerified&&!r.StableContradiction,"Short read caused semantics or permanent latch");
        });
        Check("unstable_bytes_do_not_latch",()=>
        {
            var f=new Fixture();int count=0;f.ReadOverride=(a,n)=>{var b=f.Bytes[a].ToArray();if(++count%2==0)b[0]^=1;return b;};
            var r=f.Capture();Need(!r.MetadataVerified&&!r.StableContradiction,"Transient mismatch became permanent");
        });
        Check("metadata_unreadable_does_not_fallback",()=>
        {
            var f=new Fixture();f.PageOverride=a=>a==Image+0x3EA7484?f.Page(a,1):f.Page(a,a==Image+0x51C2F0?1u:2u);
            var r=f.Capture();Need(!r.MetadataVerified&&!r.StableContradiction&&f.Reads.Count==0,"Unreadable metadata promoted");
        });
        Check("guard_page_and_wrong_allocation_never_fallback",()=>
        {
            var f=new Fixture{CodeProtection=0x101};Need(!f.Capture().MetadataVerified&&f.Reads.Count==0,"Guard accepted");
            f=new Fixture();f.PageOverride=a=>f.Page(a,1) with {AllocationBase=Image+4096};Need(!f.Capture().MetadataVerified&&f.Reads.Count==0,"Wrong allocation accepted");
        });
        Check("page_change_after_proof_rejects_closure",()=>
        {
            var f=new Fixture();var proof=f.Capture();f.CodeProtection=0x20;Need(!PersonalShopSchemaVerifier.ClosePages(proof,f.Query),"NOACCESS page switch accepted at closure");
        });
        Check("unknown_build_does_not_read",()=>
        {
            var f=new Fixture();var r=PersonalShopSchemaVerifier.Capture(new string('A',64),Image,f.Read,f.Query);
            Need(!r.MetadataVerified&&!r.RuntimeVerified&&f.Reads.Count==0,"Unknown build read or certified");
        });
        Check("negative_ledger_survives_reader_and_scene_change",()=>
        {
            string root=Path.Combine(Path.GetTempPath(),"item-cert-negative-"+Guid.NewGuid().ToString("N"));
            var source=Source();var store=new ItemStateContradictionStore(root);Need(!store.IsRejected(source),"Fresh source unexpectedly rejected");
            store.RecordClosedContradiction(source,"Synthetic closed stable byte mismatch");
            Need(new ItemStateContradictionStore(root).IsRejected(source with{SessionKey="another-scene-self"}),"Same module contradiction forgotten");
            Need(!new ItemStateContradictionStore(root).IsRejected(source with{CreationFileTime=source.CreationFileTime+1}),"New process cannot start from zero");
            Need(ItemStateContradictionStore.BindingKey(source)!=ItemStateContradictionStore.BindingKey(source with{ImageBase="0x20000000"}),"Base missing from key");
            Need(ItemStateContradictionStore.BindingKey(source)!=ItemStateContradictionStore.BindingKey(source with{DiskSha256=new string('B',64)}),"SHA missing from key");
        });
        Check("malformed_negative_ledger_still_denies",()=>
        {
            string root=Path.Combine(Path.GetTempPath(),"item-cert-partial-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var s=Source();
            File.WriteAllText(Path.Combine(root,ItemStateContradictionStore.BindingKey(s)+".rejected.json"),"{");
            Need(new ItemStateContradictionStore(root).IsRejected(s),"Partial rejection file was ignored");
        });
        foreach(int bank in new[]{0,1})foreach(int raw in new[]{0,1,2})foreach(byte flag in new byte[]{0,1})Check($"joined_bank{bank}_raw{raw}_flag{flag}",()=>
        {
            var original=Listing(bank,raw,flag);var row=PersonalShopBankRules.Match(original,Build,false,false,PersonalShopBuildCertificate.Evidence()).Rows.Single();
            Need(PersonalShopItemEvidenceRules.IsBuildCertified(row)&&row.ItemIdentificationEvidence=="BuildCertified","New provenance missing");
            Need(row.ItemIdentificationState==(flag==0?"Unidentified":"Identified"),"Wrong identification");
            Need(row.ItemKind==(flag==0?null:raw switch{0=>"Blessed",1=>"Normal",_=>"Cursed"}),"Wrong kind");
            Need(row.ItemKindState==(flag==0?"Unknown":"BuildCertifiedItemKind"),"Wrong kind proof tier");
            Need(original.Rows.Single().ItemStateEvidence is null&&original.Rows.Single().ItemKindRaw is null,"Historical input mutated");
        });
        Check("failed_page_clears_prior_slot_certificate",()=>
        {
            var l=PersonalShopBankRules.Match(Listing(0,0,1),Build,false,false,PersonalShopBuildCertificate.Evidence());
            Need(l.SlotRows.Any(s=>s.ItemStateEvidence is not null),"Fixture missing prior certificate");
            var rejected=PersonalShopBankRules.Match(l with{Complete=false},Build,false,false,PersonalShopBuildCertificate.Evidence());
            Need(rejected.Rows.All(r=>r.ItemStateEvidence is null)&&rejected.SlotRows.All(s=>s.ItemStateEvidence is null),"Old row/slot certificate survived failed join");
        });
        Check("ledger_write_failure_is_visible_and_process_stays_rejected",()=>
        {
            string root=Path.Combine(Path.GetTempPath(),"item-cert-ledger-io-"+Guid.NewGuid().ToString("N"));
            File.WriteAllText(root,"occupy directory path");var source=Source();var ledger=new ItemStateContradictionStore(root);bool failed=false;
            try{ledger.RecordClosedContradiction(source,"Synthetic closed contradiction");}catch(IOException){failed=true;}
            Need(failed,"Ledger IO failure was swallowed");
            Need(new ItemStateContradictionStore(root).IsRejected(source),"Current-process negative lost after failed persistence");
        });
        foreach(string broken in new[]{"layout","bank","price","duplicate","two-banks"})Check("broken_join_"+broken,()=>
        {
            var l=Listing(0,0,1);
            l=broken switch{"layout"=>l with{Complete=false},"bank"=>l with{BankState="Unknown"},"price"=>l with{Rows=[l.Rows[0] with{Price=101}]},
                "duplicate"=>l with{SlotRows=l.SlotRows.Select((s,i)=>i==1?l.SlotRows[0]:s).ToArray()},
                _=>l with{SlotRows=l.SlotRows.Select((s,i)=>i==8?l.SlotRows[0] with{BankIndex=1}:s).ToArray()}};
            var r=PersonalShopBankRules.Match(l,Build,false,false,PersonalShopBuildCertificate.Evidence()).Rows.Single();
            Need(r.ItemStateEvidence is null&&r.ItemKind is null&&r.ItemIdentificationEvidence=="Unverified","Broken page reused certificate");
        });
        Check("bad_raw_flag_build_and_marker_remain_unknown",()=>
        {
            var e=PersonalShopBuildCertificate.Evidence();
            Need(PersonalShopItemKindRules.Decode(0,3,1,Build,false,e).Kind is null,"Unsupported raw accepted");
            Need(PersonalShopItemKindRules.Decode(0,0,2,Build,false,e).Kind is null,"Unsupported flag accepted");
            Need(PersonalShopItemKindRules.Decode(0,0,1,new string('B',64),false,e).Kind is null,"Unknown build accepted");
            Need(!PersonalShopItemEvidenceRules.IsBuildCertified(e with{CertificateSha256="fake"})&&!PersonalShopItemEvidenceRules.IsBuildCertified(e with{CurrentRuntimeCodeVerified=true})&&!PersonalShopItemEvidenceRules.IsBuildCertified(e with{CurrentMetadataVerified=false}),"Forged evidence accepted");
            var old=Listing(0,1,1).Rows.Single() with{ItemKindRaw=1,ItemKindDetailFlag=1,ItemKindState="BuildCertifiedItemKind"};
            Need(!PersonalShopItemEvidenceRules.IsBuildCertified(old),"Marker alone promoted old JSON");
        });
        return checks.ToArray();
    }
    internal static int RunReport(string output)
    {
        var checks=Run();var report=new{Success=checks.All(c=>c.Passed),IsAdministrator=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),Checks=checks,NativeCalls=0,GameInput=0,NewLCReads=0};
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(file,report,new JsonSerializerOptions{WriteIndented=true});file.Flush(true);return report.Success?0:1;
    }
    private static void Need(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static SourceIdentity Source()=>new(4242,123456789,"C:\\offline\\LC.exe","0x10000000",0x6ACEDB9A,0x3F5F000,Build,"original-scene-self");
    private sealed class Fixture
    {
        internal uint CodeProtection=1;internal Dictionary<ulong,byte[]> Bytes=new();internal List<(ulong Address,int Count)> Reads=[];
        internal Func<ulong,int,byte[]>? ReadOverride;internal Func<ulong,ItemStatePage?>? PageOverride;
        internal Fixture()
        {
            var c=PersonalShopItemCodeContracts.ForBuild(Build)!;
            foreach(var s in c.CodeSpans)Bytes[Image+s.Rva]=Convert.FromHexString(s.Hex);
            foreach(var d in c.PrefixDescriptors){byte[] b=new byte[16];BinaryPrimitives.WriteUInt64LittleEndian(b,Image+d.TextRva);BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8),d.Length);Bytes[Image+d.DescriptorRva]=b;}
        }
        internal ItemStatePage Page(ulong address,uint protect)=>new(address&~4095UL,Image,4096,0x1000,protect,0x1000000);
        internal ItemStatePage? Query(ulong address)=>PageOverride?.Invoke(address)??Page(address,address==Image+0x51C2F0?CodeProtection:2);
        internal byte[] Read(ulong address,int count){Reads.Add((address,count));return ReadOverride?.Invoke(address,count)??Bytes[address].ToArray();}
        internal ItemStateSchemaProof Capture()=>PersonalShopSchemaVerifier.Capture(Build,Image,Read,Query);
    }
    private static PersonalShopListing Listing(int bank,int raw,byte flag)
    {
        var slots=(from b in new[]{0,1} from i in Enumerable.Range(0,8) select new PersonalShopSlotRow{BankIndex=b,RowIndex=i,State="EmptyPair"}).ToArray();
        slots[bank*8]=new(){BankIndex=bank,RowIndex=0,State="TypedPayloadCandidate",PairPresent=true,ControlVerified=true,PayloadVerified=true,
            RawPriceField20=100,PriceCandidate=100,RawQuantityField28=1,QuantityCandidate=1,PayloadQuantityCandidate=1,QuantityMatchesPayload=true,
            RawDisplayTextCandidate="fixture",UidCandidate=123,ItemKindRaw=raw,ItemKindDetailFlag=flag};
        return new(){Complete=true,LayoutState="Present",BankState="Closed",BankFingerprint="synthetic-closed",SlotRows=slots,
            RawBankSummary=new[]{0,1}.Select(b=>new PersonalShopBankSummary(b,b==0?ShopPageModes.ViewerBuy:ShopPageModes.ViewerSell,b==0?2:1,b==bank?1:0,b==bank?1:0,"fixture")).ToArray(),
            Rows=[new(0,"Observed",true,"fixture","100",100,"coin",null,"Unverified","synthetic")]};
    }
}
