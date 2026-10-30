using MemoryHub;
namespace LcPlayerMarket;
internal static class KindMappingSelfTest
{
 internal static OfflineCheck[] Run(){
  var result=new List<OfflineCheck>();
  void Check(string name,Action test){try{test();result.Add(new(name,true,"Passed"));}catch(Exception e){result.Add(new(name,false,e.ToString()));}}
  void Need(bool value){if(!value)throw new InvalidDataException("mapping invariant");}
  Check("current_build_buy_sell_three_known_states",()=>{foreach(int bank in new[]{0,1})foreach(var(raw,kind) in new[]{(0,"Blessed"),(1,"Normal"),(2,"Cursed")}){var value=PersonalShopItemKindRules.Decode(bank,raw,1,PersonalShopItemKindRules.VerifiedBuild);Need(value.Kind==kind&&value.State==PersonalShopItemKindRules.VerifiedState);}});
  Check("unknown_build_flag_bank_or_raw_not_normal",()=>{foreach(var(bank,raw,flag,sha) in new (int,int?,byte?,string?)[]{(0,1,0,PersonalShopItemKindRules.VerifiedBuild),(1,1,null,PersonalShopItemKindRules.VerifiedBuild),(1,null,1,PersonalShopItemKindRules.VerifiedBuild),(1,8,1,PersonalShopItemKindRules.VerifiedBuild),(2,1,1,PersonalShopItemKindRules.VerifiedBuild),(1,1,2,PersonalShopItemKindRules.VerifiedBuild),(1,1,1,null),(1,1,1,new string('A',64))})Need(PersonalShopItemKindRules.Decode(bank,raw,flag,sha).Kind is null);});
  return result.ToArray();
 }
}
