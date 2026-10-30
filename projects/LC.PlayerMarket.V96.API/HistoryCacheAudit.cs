using System.Diagnostics;
using System.Text.Json;
using MemoryHub;
namespace LcPlayerMarket;
internal static class HistoryCacheAudit
{
    internal static int Run(string history,string output)
    {
        if(File.Exists(output))return 2;
        var errors=new List<string>();var records=new List<object>();
        HistoryCacheReadResult Measure(string phase){var clock=Stopwatch.StartNew();var value=HistoryReadCache.Load(history,error:errors.Add);records.Add(new{Phase=phase,ElapsedMs=clock.Elapsed.TotalMilliseconds,value.FileCount,value.ParsedFileCount,value.ReusedFileCount,ValidPages=value.Listings.Length,Rows=value.Listings.Sum(v=>v.Rows.Length)});return value;}
        var first=Measure("actual-first-cache");var second=Measure("actual-unchanged");
        bool valid=second.ParsedFileCount==0&&second.Listings.Length==first.Listings.Length;
        string a=Path.Combine(history,"cache-audit-new-a.json"),b=Path.Combine(history,"cache-audit-new-b.json");
        if(File.Exists(a)||File.Exists(b)||first.Listings.Length==0)throw new InvalidOperationException("Audit fixtures conflict or no sample");
        var sample=first.Listings[0];
        try{
            File.WriteAllText(a,JsonSerializer.Serialize(sample with{Revision="cache-audit-new-a"},HubProtocol.Json));
            File.WriteAllText(b,JsonSerializer.Serialize(sample with{Revision="cache-audit-new-b"},HubProtocol.Json));
            var added=Measure("actual-plus-two-isolated-fixtures");valid &= added.ParsedFileCount==2&&added.ReusedFileCount==second.Listings.Length;
        }finally {File.Delete(a);File.Delete(b);}
        var removed=Measure("fixtures-removed");valid &= removed.ParsedFileCount==0&&removed.Listings.Length==second.Listings.Length;
        File.WriteAllText(output,JsonSerializer.Serialize(new {Success=valid,At=DateTimeOffset.UtcNow,HistoryDirectory=history,Records=records,Errors=errors,GameCalls=0,FixturesRemoved=true},new JsonSerializerOptions{WriteIndented=true}));
        return valid?0:1;
    }
}
