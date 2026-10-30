using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MemoryHub.Reader;

public sealed record TemplateReaderProfileCheck(string Name, bool Passed, string Detail);
public static class TemplateReaderProfileSelfTest
{
    public static IReadOnlyList<TemplateReaderProfileCheck> Run(string fixturesRoot, string? currentProfilePath = null)
    {
        if(Directory.Exists(fixturesRoot))throw new IOException("Template fixture directory already exists");
        Directory.CreateDirectory(fixturesRoot);
        var checks=new List<TemplateReaderProfileCheck>();
        JsonObject Fixture()=>JsonNode.Parse("""
        {"schema":"lc-location-profile-v1","revision":1,"id":"offline-template-v1","status":"candidate",
        "identity":{"sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","fileLength":4096,"peOffset":256,"peTimestamp":1,"imageSize":8192,"machine":34404},
        "symbols":{"director":{"kind":"module_rva","value":4096,"status":"located","requiredGates":["source","range","anchor","closure"]},"service":{"kind":"module_rva","value":4104,"status":"located","requiredGates":["source","range","anchor","closure"]}},
        "fields":{"selfId":{"status":"located","requiredSymbols":["director","service"],"requiredGates":["source","rtti","ownership","closure","value"]}},
        "legacyReader":{"layout":"LC-20260908-structure-v1","rvaKeys":{"25034968":"director","23341920":"service"},"typeKeys":{}},"nativeCallsEnabled":false}
        """)!.AsObject();
        void Case(string name,Action<JsonObject>? mutate,bool expected,string? overrideSha=null, Func<ReaderProfile,bool>? inspect=null)
        {
            var fixture=Fixture();mutate?.Invoke(fixture);
            string path=Path.Combine(fixturesRoot,"template-profile-"+name+".json");
            byte[] bytes=System.Text.Encoding.UTF8.GetBytes(fixture.ToJsonString());
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write)){file.Write(bytes);file.Flush(true);}
            string sha=overrideSha??Convert.ToHexString(SHA256.HashData(bytes));
            bool loaded=false;string detail="";
            try
            {
                var profile=TemplateReaderProfileAdapter.LoadCandidate(path,sha);
                loaded=profile.Resolve(0x17E00D8)==4096&&profile.Resolve(0x1642B60)==4104&&profile.Allows("selfId")&&(inspect?.Invoke(profile)??true);
                detail="candidate imported; live source/RTTI/closure still required; no runtime Verified report created";
            }
            catch(Exception ex) when(ex is InvalidDataException or KeyNotFoundException or JsonException or ProbeFailure or LcLocationTemplate.LocationContractException)
            {detail=ex.Message;}
            checks.Add(new(name,loaded==expected,detail));
        }
        Case("candidate_module_map",null,true);
        Case("reject_schema_revision2",j=>j["revision"]=2,false);
        Case("reject_bad_sha",null,false,new string('B',64));
        Case("reject_unknown_root",j=>{j["symbols"]!["director"]!["status"]="unknown";j["symbols"]!["director"]!["value"]=null;},false);
        Case("reject_field_offset_as_rva",j=>j["symbols"]!["director"]!["kind"]="field_offset",false);
        Case("reject_native_rva_as_read",j=>{j["symbols"]!["director"]!["kind"]="native_function_rva";j["symbols"]!["director"]!["callable"]=false;},false);
        Case("reject_native_enablement",j=>j["nativeCallsEnabled"]=true,false);
        Case("reject_unimplemented_gate",j=>j["fields"]!["selfId"]!["requiredGates"]!.AsArray().Add("unimplemented"),false);
        Case("reject_semantic_change_in_required_gate",j=>j["fields"]!["selfId"]!["requiredGates"]!.AsArray().Add("semantic_change"),false);
        Case("reject_unknown_field",j=>j["fields"]!["selfId"]!["status"]="unknown",false);
        Case("reject_missing_global",j=>j["legacyReader"]!["rvaKeys"]!.AsObject().Remove("23341920"),false);
        Case("reject_rva_outside_image",j=>j["symbols"]!["director"]!["value"]=8192,false);
        Case("accept_valid_unused_auxiliary_type",j=>j["legacyReader"]!["typeKeys"]![".?AVAuxiliaryConsumerType@lineage@@"]=JsonNode.Parse("{\"descriptor\":\"director\",\"col\":\"service\",\"vtable\":\"director\"}"),true);
        Case("reject_invalid_unused_auxiliary_type",j=>j["legacyReader"]!["typeKeys"]![".?AVAuxiliaryConsumerType@lineage@@"]=JsonNode.Parse("{\"descriptor\":\"missing\",\"col\":\"service\",\"vtable\":\"director\"}"),false);

        const string OptionalPartyType=".?AV?$_Ref_count_obj2@VPartyPlayer@lineage@@@std@@";
        void UnknownOptional(JsonObject j,string kind="module_rva",string gate="closure")
        {
            foreach(string part in new[]{"descriptor","col","vtable"})
                j["symbols"]!["optional_"+part]=new JsonObject
                {
                    ["kind"]=kind,["status"]="unknown",["value"]=null,
                    ["requiredGates"]=new JsonArray("source","range",gate)
                };
            j["legacyReader"]!["typeKeys"]![OptionalPartyType]=new JsonObject
            { ["descriptor"]="optional_descriptor",["col"]="optional_col",["vtable"]="optional_vtable" };
        }
        void HotbarCollection(JsonObject j,string name="offset:HudSlotDataSubsystem:slotsHead",string extra="collection")
        {
            j["symbols"]![name]=new JsonObject
            { ["kind"]="field_offset",["status"]="located",["value"]=name.EndsWith(":focus")?68:name.StartsWith("offset:Actor:")?24:32,
              ["requiredGates"]=new JsonArray("source","rtti","ownership",extra,"closure","hotbar_layout") };
        }
        Case("accept_unknown_optional_party_type",j=>UnknownOptional(j),true,inspect:p=>!p.TypeBindings.ContainsKey(OptionalPartyType));
        Case("reject_unknown_required_party_type",j=>{UnknownOptional(j);j["fields"]!["selfId"]!["requiredSymbols"]!.AsArray().Add("optional_descriptor");},false);
        Case("reject_unknown_optional_native_type",j=>UnknownOptional(j,"native_function_rva"),false);
        Case("reject_unknown_optional_unimplemented_gate",j=>UnknownOptional(j,gate:"unimplemented"),false);
        Case("accept_exact_hotbar_collection_gate",j=>HotbarCollection(j),true,inspect:p=>p.FieldOffsets.GetValueOrDefault("offset:HudSlotDataSubsystem:slotsHead")==32);
        Case("reject_hotbar_focus_collection_gate",j=>HotbarCollection(j,"offset:HudSlotDataSubsystem:focus"),false);
        Case("reject_actor_collection_as_hotbar_gate",j=>HotbarCollection(j,"offset:Actor:id"),false);
        Case("reject_unknown_hotbar_extra_gate",j=>HotbarCollection(j,extra:"unimplemented"),false);

        var goodBytes=File.ReadAllBytes(Path.Combine(fixturesRoot,"template-profile-candidate_module_map.json"));
        var goodPointer=new{schemaVersion=1,fileName="template-profile-candidate_module_map.json",sha256=Convert.ToHexString(SHA256.HashData(goodBytes))};
        File.WriteAllText(Path.Combine(fixturesRoot,"template-active.json"),JsonSerializer.Serialize(goodPointer),new System.Text.UTF8Encoding(true));
        bool bomAccepted=false;try{bomAccepted=TemplateReaderProfileAdapter.Load(fixturesRoot).Allows("selfId");}catch(Exception error){checks.Add(new("bom_pointer_error",false,error.Message));}
        checks.Add(new("accept_utf8_bom_pointer",bomAccepted,"PowerShell UTF-8 BOM pointer accepted without changing profile bytes or SHA"));
        var activeDir=Path.Combine(fixturesRoot,"duplicate-index");Directory.CreateDirectory(activeDir);
        File.WriteAllText(Path.Combine(activeDir,"template-active.json"),"{\"schemaVersion\":1,\"schemaVersion\":1,\"fileName\":\"template-profile-safe.json\",\"sha256\":\""+new string('A',64)+"\"}");
        bool rejected=false;try{_=TemplateReaderProfileAdapter.Load(activeDir);}catch(InvalidDataException){rejected=true;}
        checks.Add(new("reject_duplicate_index_key",rejected,"No LC process opened; duplicate pointer rejected"));
        string imports=Path.Combine(fixturesRoot,"versioned-imports");
        var first=TemplateProfileImport.Import(Path.Combine(fixturesRoot,"template-profile-candidate_module_map.json"),imports);
        var second=TemplateProfileImport.Import(Path.Combine(fixturesRoot,"template-profile-candidate_module_map.json"),imports);
        checks.Add(new("preserve_previous_template_import",first.SavedDirectory!=second.SavedDirectory&&Directory.Exists(first.SavedDirectory),"Each import creates a new immutable directory; previous versions retained"));
        var activated=new ProfileStore(imports).Load();
        checks.Add(new("activate_versioned_candidate",activated?.Allows("selfId")==true&&activated.Resolve(0x17E00D8)==4096,"Original live-reader gates remain required after activation"));
        bool badImportRejected=false;
        try{_=TemplateProfileImport.Import(Path.Combine(fixturesRoot,"template-profile-reject_unknown_root.json"),imports);}
        catch(LcLocationTemplate.LocationContractException){badImportRejected=true;}
        catch(InvalidDataException){badImportRejected=true;}
        checks.Add(new("invalid_import_not_activated",badImportRejected&&new ProfileStore(imports).Load()?.Allows("selfId")==true,"Incomplete import has no activation pointer and cannot replace the valid version"));

        if(currentProfilePath is not null)
        {
            ReaderProfile? current=null;string detail="";
            try
            {
                string actual=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(currentProfilePath)));
                if(actual!="CF13401E92D8E7F74EFB1BC30F369FAE2A68B112B3D06A6F0FB321A761B307F8")throw new InvalidDataException("Frozen V5 profile SHA mismatch");
                var imported=TemplateProfileImport.Import(currentProfilePath,Path.Combine(fixturesRoot,"current-v5-import"));
                current=new ProfileStore(imported.SavedDirectory).Load();
                detail=$"Frozen profile {actual}; consumer candidate only; no LC open, runtime report, input or native call";
            }
            catch(Exception error){detail=error.ToString();}
            checks.Add(new("current_frozen_v5_profile_import",current is not null,detail));
            checks.Add(new("current_shop_unknown_preserved",current is not null&&!current.Allows("playerShopStates")&&!current.Allows("personalShopListing"),"Both V5 unknown shop fields remain unavailable"));
            checks.Add(new("current_optional_unknown_party_not_synthesized",current is not null&&!current.TypeBindings.ContainsKey(OptionalPartyType),"Unknown PartyPlayer TD/COL/vtable are not inherited or guessed"));
            checks.Add(new("current_hotbar_collection_offsets_loaded",current is not null&&current.FieldOffsets.GetValueOrDefault("offset:HudSlotDataSubsystem:slotsHead")==224&&current.FieldOffsets.GetValueOrDefault("offset:HudSlotDataSubsystem:slotsCount")==232&&current.FieldOffsets.GetValueOrDefault("offset:HudSlotDataSubsystem:page")==256&&!current.FieldOffsets.ContainsKey("offset:HudSlotDataSubsystem:focus"),"Exact current offsets 224/232/256 loaded; focus remains unknown"));
        }

        return checks;
    }
}
