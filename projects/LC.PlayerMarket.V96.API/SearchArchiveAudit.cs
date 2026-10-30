using MemoryHub;
using MemoryHub.Service;
using System.Diagnostics;
using System.Text.Json;
namespace LcPlayerMarket;
internal static class SearchArchiveAudit
{
    internal static int Run(string source,string target,string output)
    {
        try {
            var clock=Stopwatch.StartNew();var metadata=SearchedOwnerArchive.CreateFromLegacy(source,target);double migrationMs=clock.Elapsed.TotalMilliseconds;
            clock.Restart();var store=new ShopSearchedOwnerStore(target,Path.Combine(target,"no-legacy-progress"));
            if(store.Error is not null)throw new InvalidDataException(store.Error);
            var summary=store.Summary(true,"");double replayMs=clock.Elapsed.TotalMilliseconds;
            clock.Restart();var second=new ShopSearchedOwnerStore(target,Path.Combine(target,"no-legacy-progress"));
            if(second.Error is not null || second.Summary(true,"")!=summary)throw new InvalidDataException("Archive reopen changed summary");
            File.WriteAllText(output,JsonSerializer.Serialize(new {Success=true,AtUtc=DateTimeOffset.UtcNow,Source=source,Target=target,Metadata=metadata,Summary=summary,MigrationMs=migrationMs,ReplayMs=replayMs,SecondReplayMs=clock.Elapsed.TotalMilliseconds,AppendedFiles=Directory.GetFiles(target,"*.json").Length,SourceWritten=false,GameCalls=0},HubProtocol.Json));return 0;
        }catch(Exception e){File.WriteAllText(output,JsonSerializer.Serialize(new {Success=false,Error=e.ToString()},HubProtocol.Json));return 1;}
    }
    internal static OfflineCheck[] SelfTest()
    {
        var checks=new List<OfflineCheck>();string root=Path.Combine(Path.GetTempPath(),"LC-Archive-Store-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Check(string name,Action action){try{action();checks.Add(new(name,true,"Passed"));}catch(Exception e){checks.Add(new(name,false,e.ToString()));}}
        void Need(bool ok){if(!ok)throw new InvalidDataException("Archive store invariant failed");}
        string source=Path.Combine(root,"source"),target=Path.Combine(root,"archive");
        var identity=new SourceIdentity(123,456,"offline","0x10000",1,65536,new string('A',64),"offline");
        MarketOwner Owner(ulong id)=>new(){Id=id,Name="fixture",LocalIdentity="fixture"+id,Position=new(100,100),ObservedAt=DateTimeOffset.UtcNow};
        Check("archive_store_preserves_clear_and_cross_epoch_uncertain",()=>{
            var original=new ShopSearchedOwnerStore(source,Path.Combine(root,"none"));original.Begin(identity,Owner(1),ShopPageModes.ViewerBuy,"pending");original.Clear(original.Revision);
            var before=original.Summary(true,"");SearchedOwnerArchive.CreateFromLegacy(source,target);
            var store=new ShopSearchedOwnerStore(target,Path.Combine(root,"none"));Need(store.Error is null&&store.Summary(true,"")==before&&store.BlocksSource(identity)&&!store.ShouldSkip(1,ShopPageModes.ViewerBuy));
        });
        Check("archive_store_appends_settlement_and_new_epoch_without_resurrection",()=>{
            var store=new ShopSearchedOwnerStore(target,Path.Combine(root,"none"));store.Settle("pending",false);Need(!store.BlocksSource(identity));store.Begin(identity,Owner(2),ShopPageModes.ViewerBuy,"new");store.Settle("new",true);store.Confirm("new");
            var reopened=new ShopSearchedOwnerStore(target,Path.Combine(root,"none"));Need(reopened.Error is null&&reopened.Summary(true,"").ConfirmedPages==1&&!reopened.ShouldSkip(1,ShopPageModes.ViewerBuy)&&reopened.ShouldSkip(2,ShopPageModes.ViewerBuy));
            reopened.Clear(reopened.Revision);var cleared=new ShopSearchedOwnerStore(target,Path.Combine(root,"none"));Need(cleared.Error is null&&cleared.Summary(true,"").ConfirmedPages==0);
        });
        Check("archive_store_rejects_missing_tail_and_damaged_prefix",()=>{
            string bad=Path.Combine(root,"bad");Directory.CreateDirectory(bad);foreach(var path in Directory.GetFiles(target))File.Copy(path,Path.Combine(bad,Path.GetFileName(path)));
            File.Delete(Directory.GetFiles(bad,"*.json").Order().First());Need(new ShopSearchedOwnerStore(bad,Path.Combine(root,"none")).Error is not null);
            string blob=Path.Combine(target,"events.archive");byte[] bytes=File.ReadAllBytes(blob);bytes[^1]^=1;File.WriteAllBytes(blob,bytes);Need(new ShopSearchedOwnerStore(target,Path.Combine(root,"none")).Error is not null);
        });
        return checks.ToArray();
    }
}
