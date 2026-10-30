using MemoryHub;using System.Text.Json;
namespace LcPlayerMarket;
internal static class RecordedStateEvidence
{
 internal static int Run(string queryPath,string inventoryPath,string output){
  try{
   using var query=JsonDocument.Parse(File.ReadAllText(queryPath));using var inventory=JsonDocument.Parse(File.ReadAllText(inventoryPath));
   if(!query.RootElement.GetProperty("success").GetBoolean()||!inventory.RootElement.GetProperty("success").GetBoolean())throw new InvalidDataException("SOURCE_EVIDENCE_FAILED");
   var source=inventory.RootElement.GetProperty("observations")[0].GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json)!;
   var before=query.RootElement.GetProperty("before").Deserialize<PersonalShopListing>(HubProtocol.Json)!;
   if(source.DiskSha256!=PersonalShopItemKindRules.VerifiedBuild||before.SourceSessionKey!=source.SessionKey)throw new InvalidDataException("SOURCE_EVIDENCE_MISMATCH");
   var mapped=PersonalShopBankRules.Match(before,source.DiskSha256);
   if(mapped.PageModeState!="MatchedExclusiveBankAndRows"||mapped.Rows.Count(row=>row.IncludedInListing)!=8)throw new InvalidDataException("RECORDED_PAGE_NOT_CLOSED");
   var rows=mapped.Rows.Where(row=>row.IncludedInListing).Select(row=>new{row.RowIndex,row.Name,row.Price,row.Quantity,row.ItemKindRaw,row.ItemKindDetailFlag,row.ItemKind,row.ItemKindState,Effective=MarketItemStatePresentation.EffectiveKind(row),Display=MarketItemStatePresentation.Display(row)}).ToArray();
   if(rows.Count(row=>row.Effective=="Blessed")!=1||rows.Count(row=>row.Effective=="Normal")!=3||rows.Count(row=>row.Effective=="Unknown")!=4)throw new InvalidDataException("RECORDED_MAPPING_UNEXPECTED");
   File.WriteAllText(output,JsonSerializer.Serialize(new{Success=true,AtUtc=DateTimeOffset.UtcNow,SourceBuild=source.DiskSha256,Scope="Offline replay of previously captured typed shop bank; no new game query",mapped.PageMode,Rows=rows,NewGameReads=0,GameCalls=0},HubProtocol.Json));return 0;
  }catch(Exception e){File.WriteAllText(output,JsonSerializer.Serialize(new{Success=false,Error=e.ToString(),GameCalls=0},HubProtocol.Json));return 1;}
 }
}
