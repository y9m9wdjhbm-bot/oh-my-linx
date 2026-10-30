using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using MemoryHub;

namespace LcPlayerMarket;

internal static class ItemEvidenceConsumerVerification
{
    private const string Build="38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1";
    private static PersonalShopRow Item(byte flag=1,int raw=1) => new(0,"Observed",true,"測試道具","123",123,"金幣",1,"Observed","synthetic")
    { ItemKindDetailFlag=flag,ItemKindRaw=raw,ItemKind=flag==1?(raw switch{0=>"Blessed",1=>"Normal",2=>"Cursed",_=>null}):null,
      ItemKindState=flag==1?"BuildCertifiedItemKind":"Unknown",ItemIdentificationState=flag==1?"Identified":"Unidentified",ItemIdentificationEvidence="BuildCertified",
      ItemStateEvidence=new(){Mode="BuildCertified",SourceBuildSha256=Build,CertificateSha256=PersonalShopBuildCertificate.CertificateSha256,CurrentRuntimeCodeVerified=false,CurrentMetadataVerified=true} };
    private static MarketListing Listing(params PersonalShopRow[] rows)=>new(){Revision="synthetic-evidence",SourceSessionKey="historical-evidence",RequestId="synthetic-request",
        Owner=new(){Id=21,LocalIdentity="owner-21",Name="測試店主",ObservedAt=DateTimeOffset.UnixEpoch},ViewerDirection=ShopPageModes.ViewerBuy,
        CapturedAt=DateTimeOffset.UnixEpoch,State="Observed",Detail="Synthetic only",Complete=true,OwnerAndResponseMatched=true,Rows=rows};
    internal static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action body){try{body();checks.Add(new("item_evidence_consumer_"+name,true,"Synthetic JSON/cache/XLSX only; no LC read or native action."));}catch(Exception e){checks.Add(new("item_evidence_consumer_"+name,false,e.ToString()));}}
        string[] labels=["受祝福","一般","受詛咒"],kinds=["Blessed","Normal","Cursed"];
        for(int raw=0;raw<3;raw++)
        {
            int value=raw;
            Check("certified_identified_"+raw,()=>{var row=Item(1,value);Need(PersonalShopItemEvidenceRules.IsBuildCertified(row),"Core certificate rejected");
                Need(MarketItemStatePresentation.EffectiveKind(row)==kinds[value]&&MarketItemStatePresentation.Display(row)==labels[value]+"（已鑑定；版本認證）","Certified presentation wrong");});
            Check("certified_unidentified_"+raw,()=>{var row=Item(0,value);Need(PersonalShopItemEvidenceRules.IsBuildCertified(row),"Core flag0 certificate rejected");
                Need(MarketItemStatePresentation.EffectiveKind(row)=="Normal"&&MarketItemStatePresentation.EffectiveKindReason(row)==MarketItemStatePresentation.UnidentifiedRule&&MarketItemStatePresentation.Display(row)=="未知（未鑑定；版本認證）","Unidentified rule changed");
                Need(row.ItemKindRaw==value&&row.ItemKind is null,"Raw evidence changed");});
        }
        var original=Item();
        var counterexamples=new (string Name,PersonalShopRow Row)[]{
            ("missing",original with{ItemStateEvidence=null}),
            ("wrong_mode",original with{ItemStateEvidence=original.ItemStateEvidence! with{Mode="buildcertified"}}),
            ("wrong_build",original with{ItemStateEvidence=original.ItemStateEvidence! with{SourceBuildSha256=new string('A',64)}}),
            ("wrong_certificate",original with{ItemStateEvidence=original.ItemStateEvidence! with{CertificateSha256=new string('A',64)}}),
            ("metadata_false",original with{ItemStateEvidence=original.ItemStateEvidence! with{CurrentMetadataVerified=false}}),
            ("runtime_claim",original with{ItemStateEvidence=original.ItemStateEvidence! with{CurrentRuntimeCodeVerified=true}}),
            ("kind_value_mismatch",original with{ItemKind="Cursed"}),
            ("kind_marker",original with{ItemKindState="Unknown"}),
            ("ident_evidence",original with{ItemIdentificationEvidence="Verified"}),
            ("ident_state",original with{ItemIdentificationState="Unidentified"}),
            ("flag0_wrong_ident",Item(0,1) with{ItemIdentificationState="Identified"}),
            ("flag0_wrong_kind",Item(0,1) with{ItemKindState="BuildCertifiedItemKind"}),
            ("unsupported_raw",original with{ItemKindRaw=4}),
            ("missing_raw",original with{ItemKindRaw=null}),
            ("unsupported_flag",original with{ItemKindDetailFlag=2})};
        foreach(var c in counterexamples)Check("reject_"+c.Name,()=>Need(MarketItemStatePresentation.EffectiveKind(c.Row)=="Unknown"&&!MarketItemStatePresentation.Display(c.Row).Contains("版本認證"),"Invalid certificate adopted: "+c.Name));
        Check("legacy_runtime_rule_unchanged",()=>{var row=original with{ItemKindState="CurrentBuildItemKindAccessorVerified",ItemIdentificationEvidence="Verified",ItemStateEvidence=null};
            Need(MarketItemStatePresentation.Display(row)=="一般（已鑑定）"&&MarketItemStatePresentation.EffectiveKind(row)=="Normal","Legacy runtime rule changed");
            Need(MarketItemStatePresentation.Display(Item(0,1) with{ItemKindState="Unknown",ItemIdentificationEvidence="Verified",ItemStateEvidence=null})=="未知（未鑑定）","Old user rule changed");});
        Check("tooltip_discloses_static_vs_current_proof",()=>{string text=MarketItemStatePresentation.EvidenceTooltip(original);
            Need(text.Contains("BuildCertified")&&text.Contains(Build)&&text.Contains(PersonalShopBuildCertificate.CertificateSha256)&&text.Contains("當前程式碼驗證=false")&&text.Contains("當前中繼資料驗證=true")&&text.Contains("靜態語意"),text);});
        Check("json_preserves_evidence_and_legacy_null",()=>{
            var listing=Listing(original,Item(0,2),original with{ItemStateEvidence=null});
            string json=JsonSerializer.Serialize(listing,HubProtocol.Json);var restored=JsonSerializer.Deserialize<MarketListing>(json,HubProtocol.Json)!;
            Need(restored.Rows.SequenceEqual(listing.Rows),"JSON evidence not exact");
            var old=JsonSerializer.Deserialize<PersonalShopRow>("{\"rowIndex\":0,\"state\":\"Observed\",\"includedInListing\":true,\"name\":\"old\",\"itemKindRaw\":1,\"itemKindDetailFlag\":1,\"itemKindState\":\"BuildCertifiedItemKind\",\"itemIdentificationState\":\"Identified\",\"itemIdentificationEvidence\":\"BuildCertified\"}",HubProtocol.Json)!;
            Need(old.ItemStateEvidence is null&&MarketItemStatePresentation.EffectiveKind(old)=="Unknown","Old JSON retrofitted certificate");
        });
        Check("cache_v4_roundtrip_keeps_v2_v3_and_old_json",()=>{
            string root=Scratch("cache");string oldCache=Path.Combine(root,".history-read-cache-v2.bin");byte[] sentinel=[7,8,9];File.WriteAllBytes(oldCache,sentinel);
            string oldV3Cache=Path.Combine(root,".history-read-cache-v3.bin");byte[] v3Sentinel=[3,4,5,6];File.WriteAllBytes(oldV3Cache,v3Sentinel);
            var rows=new[]{original,Item(0,2),original with{ItemStateEvidence=null},counterexamples[3].Row};var listing=Listing(rows);
            string path=Path.Combine(root,"history.json");File.WriteAllText(path,JsonSerializer.Serialize(listing,HubProtocol.Json));var sourceHash=SHA256.HashData(File.ReadAllBytes(path));
            var cold=HistoryReadCache.Load(root);var warm=HistoryReadCache.Load(root);
            Need(cold.ParsedFileCount==1&&warm.ReusedFileCount==1&&warm.ParsedFileCount==0,"New codec not reused");
            Need(JsonSerializer.Serialize(warm.Listings.Single(),HubProtocol.Json)==JsonSerializer.Serialize(listing,HubProtocol.Json),"Cache lost evidence fields");
            Need(HistoryReadCache.CachePath(root).EndsWith("v4.bin")&&File.ReadAllBytes(oldCache).SequenceEqual(sentinel)&&File.ReadAllBytes(oldV3Cache).SequenceEqual(v3Sentinel)&&SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(sourceHash),"Old cache or JSON changed");
            Need(warm.Listings[0].Rows[2].ItemStateEvidence is null&&MarketItemStatePresentation.EffectiveKind(warm.Listings[0].Rows[3])=="Unknown","Cache retrofit or invalid certificate promotion");
        });
        Check("cache_v4_neutral_evidence_and_legacy_roundtrip",()=>{
            string root=Scratch("neutral-cache"),oldCache=Path.Combine(root,".history-read-cache-v3.bin");
            byte[] sentinel=[3,3,3,3];File.WriteAllBytes(oldCache,sentinel);
            var at=new DateTimeOffset(2026,10,17,0,0,0,TimeSpan.Zero);
            var evidence=new ShopEmptyPageObservation{RequestId="neutral-request",SourceSessionKey="neutral-source",OwnerId=21,
                RequestedDirection=ShopPageModes.ViewerBuy,RequestedAt=at,BaselineSampledAt=at.AddSeconds(-1),
                SampledAt=at.AddMilliseconds(500),ValidatedAt=at.AddSeconds(1),LayoutState="Present",BankState="Closed",
                BankFingerprint=new string('B',64),Complete=true,
                BankSummaries=[new(0,ShopPageModes.ViewerBuy,2,0,0,new string('C',64)),new(1,ShopPageModes.ViewerSell,1,0,0,new string('D',64))],
                Slots=Enumerable.Range(0,16).Select(i=>new ShopEmptySlotObservation(i/8,i%8,"EmptyPair",false)).ToArray(),
                ExcludedRowIndices=Enumerable.Range(0,8).ToArray(),NativeLifecycleSettled=true,
                DirectionVerified=false,ResponseMatched=false,ServerAckVerified=false};
            var neutral=Listing() with{Revision="neutral-cache",SourceSessionKey=evidence.SourceSessionKey,RequestId=evidence.RequestId,
                RequestedAt=evidence.RequestedAt,CapturedAt=evidence.SampledAt,State=ShopEmptyPageRules.State,
                Detail=ShopEmptyPageRules.Detail,Complete=false,PageEmpty=false,OwnerAndResponseMatched=false,
                EmptyPageObserved=true,EmptyPageObservation=evidence};
            Need(ShopEmptyPageRules.IsNeutralListing(neutral),"Invalid synthetic neutral fixture");
            string json=JsonSerializer.Serialize(neutral,HubProtocol.Json),path=Path.Combine(root,"neutral.json");
            File.WriteAllText(path,json);byte[] sourceHash=SHA256.HashData(File.ReadAllBytes(path));
            var legacy=Listing(original) with{Revision="legacy-no-empty-fields"};
            var node=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(legacy,HubProtocol.Json))!.AsObject();
            node.Remove("emptyPageObserved");node.Remove("emptyPageObservation");
            File.WriteAllText(Path.Combine(root,"legacy.json"),node.ToJsonString());
            var errors=new List<string>();var cold=HistoryReadCache.Load(root,error:errors.Add);var warm=HistoryReadCache.Load(root,error:errors.Add);
            Need(cold.ParsedFileCount==2&&warm.ParsedFileCount==0&&warm.ReusedFileCount==2&&errors.Count==0,"Neutral codec not reused");
            var restored=warm.Listings.Single(v=>v.Revision==neutral.Revision);
            Need(JsonSerializer.Serialize(restored,HubProtocol.Json)==json&&ShopEmptyPageRules.IsNeutralListing(restored)
                &&!restored.OwnerAndResponseMatched&&!restored.PageEmpty&&!restored.Complete&&restored.Rows.Length==0,"Neutral evidence lost or promoted");
            var old=warm.Listings.Single(v=>v.Revision==legacy.Revision);
            Need(!old.EmptyPageObserved&&old.EmptyPageObservation is null&&!ShopEmptyPageRules.IsNeutralListing(old),"Legacy history retrofitted neutral proof");
            byte[] cache=File.ReadAllBytes(HistoryReadCache.CachePath(root));File.WriteAllBytes(HistoryReadCache.CachePath(root),cache[..(cache.Length/2)]);
            errors.Clear();var fallback=HistoryReadCache.Load(root,error:errors.Add);
            Need(fallback.ParsedFileCount==2&&fallback.ReusedFileCount==0&&errors.Count>0
                &&JsonSerializer.Serialize(fallback.Listings.Single(v=>v.Revision==neutral.Revision),HubProtocol.Json)==json,"Truncated cache did not restore complete source evidence");
            Need(File.ReadAllBytes(oldCache).SequenceEqual(sentinel)&&SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(sourceHash),"Old v3 cache or source JSON changed");
        });
        Check("excel_all_evidence_fields_and_unknown_roundtrip",()=>{
            string root=Scratch("excel"),path=Path.Combine(root,"evidence.xlsx");var items=new[]{original,Item(0,2),counterexamples[3].Row,original with{ItemStateEvidence=null}};
            var listing=Listing(items);var views=items.Select(row=>new MarketRowView(listing,row,null,null,false)).ToArray();ExcelExporter.Write(path,views,"","original");
            using var zip=ZipFile.OpenRead(path);using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();var doc=XDocument.Load(stream);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            string Cell(string address)=>string.Concat(doc.Descendants(ns+"c").Single(x=>(string?)x.Attribute("r")==address).Descendants(ns+"t").Select(x=>x.Value));
            Need(Cell("L2")=="一般（已鑑定；版本認證）"&&Cell("L3")=="未知（未鑑定；版本認證）","Excel presentation differs");
            Need(Cell("S2")=="BuildCertified"&&Cell("T2")==Build&&Cell("U2")==PersonalShopBuildCertificate.CertificateSha256&&Cell("V2")=="false"&&Cell("W2")=="true","Excel certificate incomplete");
            Need(Cell("L4").Contains("未知")&&!Cell("L4").Contains("版本認證")&&Cell("U4")==new string('A',64),"Export rewrote invalid evidence");
            Need(Cell("S5")=="未記錄"&&Cell("V5")=="未知"&&Cell("W5")=="未知","Null evidence fabricated");
        });
        return checks.ToArray();
    }
    private static string Scratch(string kind){string path=Path.Combine(Path.GetTempPath(),"LC-V82-item-evidence-"+kind+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static void Need(bool value,string detail){if(!value)throw new InvalidOperationException(detail);}
    internal static int RunReport(string output)
    {
        var checks=Run();var names=new[]{"MarketItemStatePresentation.cs","HistoryReadCache.cs","ExcelExporter.cs","ExcelExportSelfTest.cs","ItemEvidenceConsumerVerification.cs","Program.cs"};string root=@"D:\lineage\LC_MarketReverification_20261023_v1\LC_PlayerMarket_v86\source-v1";
        var hashes=names.ToDictionary(n=>Path.Combine(root,n),n=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,n)))));
        var report=new{Success=checks.All(c=>c.Passed),IsAdministrator=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),Passed=checks.Count(c=>c.Passed),Failed=checks.Count(c=>!c.Passed),Checks=checks,TestedAssembly=typeof(ItemEvidenceConsumerVerification).Assembly.Location,TestedAssemblySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ItemEvidenceConsumerVerification).Assembly.Location))),SourceHashes=hashes,LCRead=0,NativeCalls=0,GameInput=0};
        using var file=new FileStream(output,FileMode.CreateNew);JsonSerializer.Serialize(file,report,new JsonSerializerOptions{WriteIndented=true});return report.Success?0:1;
    }
}
