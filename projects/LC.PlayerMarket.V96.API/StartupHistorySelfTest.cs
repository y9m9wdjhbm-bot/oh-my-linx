using MemoryHub;
using MemoryHub.Service;
using System.Text.Json;
namespace LcPlayerMarket;
internal static class StartupHistorySelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();string root=Path.Combine(Path.GetTempPath(),"LC-startup-history-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Check(string name,Action work){try{work();checks.Add(new(name,true,"通過"));}catch(Exception e){checks.Add(new(name,false,e.ToString()));}}
        static void Need(bool ok,string reason){if(!ok)throw new Exception(reason);}
        var at=DateTimeOffset.UtcNow.AddDays(-1);
        MarketListing Page(int id)=>new(){Revision="r"+id,RequestId="q"+id,SourceSessionKey="offline-session",Owner=new(){Id=(ulong)id,LocalIdentity="owner"+id,Name="測試"+id,Position=new(100+id,200),MapName="測試圖",ObservedAt=at},ViewerDirection=ShopPageModes.ViewerBuy,RequestedAt=at,CapturedAt=at.AddSeconds(1),State="OwnerAndResponseMatched",OwnerAndResponseMatched=true,Complete=true};
        string legacy=Path.Combine(root,"legacy");Directory.CreateDirectory(legacy);
        for(int i=1;i<=130;i++)File.WriteAllText(Path.Combine(legacy,i+".json"),JsonSerializer.Serialize(Page(i),HubProtocol.Json));
        string ledger=Path.Combine(root,"ledger"),progress=Path.Combine(root,"progress");
        Check("startup_import_batches_preserve_all_dedup_pages",()=>{
            var store=new ShopSearchedOwnerStore(ledger,progress,legacy);Need(store.Error is null,store.Error??"");Need(store.Summary(true,"").ConfirmedPages==130,"漏掉匯入頁");Need(store.ShouldSkip(130,ShopPageModes.ViewerBuy),"未保留去重");Need(Directory.GetFiles(ledger,"*.json").Length==5,"未以3批+2控制事件保存");
        });
        Check("startup_second_open_does_not_reimport_legacy_or_rewrite_ledger",()=>{
            var before=Directory.GetFiles(ledger).ToDictionary(p=>Path.GetFileName(p)!,p=>(new FileInfo(p).Length,File.GetLastWriteTimeUtc(p)));
            var store=new ShopSearchedOwnerStore(ledger,progress,legacy);Need(store.Error is null&&store.Summary(true,"").ConfirmedPages==130,"重開遺失頁");
            Need(before.All(k=>new FileInfo(Path.Combine(ledger,k.Key!)).Length==k.Value.Length&&File.GetLastWriteTimeUtc(Path.Combine(ledger,k.Key!))==k.Value.Item2),"重開重寫了ledger");
        });
        Check("startup_clear_reopen_new_legacy_does_not_resurrect_old_epoch",()=>{
            var store=new ShopSearchedOwnerStore(ledger,progress,legacy);store.Clear(store.Revision);
            File.WriteAllText(Path.Combine(legacy,"999.json"),JsonSerializer.Serialize(Page(999),HubProtocol.Json));
            var reopened=new ShopSearchedOwnerStore(ledger,progress,legacy);Need(reopened.Error is null&&reopened.Summary(true,"").ConfirmedPages==0&&!reopened.ShouldSkip(1,ShopPageModes.ViewerBuy),"清除後舊epoch復活");
        });
        Check("startup_new_intent_after_clear_survives_reopen_and_blocks_uncertain",()=>{
            var source=new SourceIdentity(123,456,"offline-only","0x10000",1,65536,new string('A',64),"offline-session");
            var store=new ShopSearchedOwnerStore(ledger,progress,legacy);store.Begin(source,Page(131).Owner,ShopPageModes.ViewerBuy,"new-intent");
            var reopened=new ShopSearchedOwnerStore(ledger,progress,legacy);Need(reopened.BlocksSource(source)&&reopened.Summary(true,"").UncertainPages==1,"未定意圖失效");
            reopened.Settle("new-intent",true);reopened.Confirm("new-intent");Need(reopened.Summary(true,"").ConfirmedPages==1,"新頁不能完成");
        });
        Check("startup_incomplete_import_resumes_without_duplicate_members",()=>{
            string partial=Path.Combine(root,"partial");Directory.CreateDirectory(partial);
            foreach(var file in Directory.GetFiles(ledger,"*.json").Order().Take(2))File.Copy(file,Path.Combine(partial,Path.GetFileName(file)));
            var second=Directory.GetFiles(partial,"*.json").Order().Last();
            File.WriteAllText(Path.Combine(partial,"head.checkpoint"),JsonSerializer.Serialize(new SearchedOwnerHead(2,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(second)))),HubProtocol.Json));
            var resumed=new ShopSearchedOwnerStore(partial,progress,legacy);Need(resumed.Error is null&&resumed.Summary(true,"").ConfirmedPages==131,"部分批次恢復失敗/重複");
        });
        Check("startup_corrupt_batch_fails_closed",()=>{
            string bad=Path.Combine(root,"bad");Directory.CreateDirectory(bad);foreach(var f in Directory.GetFiles(ledger))File.Copy(f,Path.Combine(bad,Path.GetFileName(f)));
            string batch=Path.Combine(bad,"0000000002.json");var value=JsonSerializer.Deserialize<SearchedOwnerEvent>(File.ReadAllText(batch),HubProtocol.Json)!;
            File.WriteAllText(batch,JsonSerializer.Serialize(value with {ImportedEntries=[value.ImportedEntries![0],value.ImportedEntries[0]]},HubProtocol.Json));
            var invalid=new ShopSearchedOwnerStore(bad,progress,legacy);Need(invalid.Error is not null,"重複batch成員未拒絕");
        });
        Check("startup_clear_history_skips_obsolete_invalid_json",()=>{
            var store=new LocalStore(Path.Combine(root,"ui"));store.SaveListing(Page(1));Need(store.ClearListings(at.AddDays(1)),"清除失敗");
            File.WriteAllText(Path.Combine(store.HistoryDirectory,"obsolete-invalid.json"),"not json");int errors=0;store.Error+=_=>errors++;
            Need(store.LoadHistory().Length==0&&errors==0&&store.StoredFileCount==2,"清除後仍讀舊檔");
        });
        Check("startup_background_merge_obeys_new_clear_and_keeps_newer_page",()=>{
            var store=new LocalStore(Path.Combine(root,"merge"));var index=new MarketListingIndex();var old=Page(4);var fresh=old with {Revision="fresh",CapturedAt=at.AddHours(2)};index.Upsert(fresh);
            BackgroundHistory.Merge(store,index,[old]);Need(index.Values.Single().Revision=="fresh","舊載入覆蓋新頁");store.ClearListings(at.AddDays(1));index.Clear();BackgroundHistory.Merge(store,index,[old]);Need(index.Count==0,"背景資料在清除後復活");
        });
        Check("startup_history_precancel_is_not_swallowed",()=>{
            using var token=new CancellationTokenSource();token.Cancel();try{new LocalStore(root).LoadHistory(token.Token);}catch(OperationCanceledException){return;}throw new Exception("取消被吞掉");
        });
        return checks.ToArray();
    }
}
