using MemoryHub;
namespace LcPlayerMarket;
internal sealed partial class MainForm
{
    private Task? historyRestoreTask;
    private Task? historyIoTask;
    private async Task RestoreHistoryAsync()
    {
        StartupTiming.Mark("history-start");
        Report("HISTORY_LOADING", "正在背景載入歷史；可在讀取來源就緒後刷新周遭及搜尋。歷史未載完前，畫面與匯出只包含目前已載入商品。");
        try
        {
            var token=lifetime.Token;
            var readTask=Task.Run(()=>BackgroundHistory.Read(store.Root,token),token);
            historyIoTask=readTask;
            var result=await readTask;
            StartupTiming.Mark("history-read-end");
            if(closing||IsDisposed)return;
            store.AdoptHistoryFileCount(result.FileCount);
            foreach(var batch in result.Listings.Chunk(100))
            {
                if(closing||IsDisposed)return;
                // Recheck current clear/resume state on the UI thread, not the
                // worker's earlier copy. Upsert preserves newer verified pages.
                BackgroundHistory.Merge(store,history,batch);
                await Task.Yield();
            }
            history.Trim(LocalStore.ViewHistoryLimit);
            foreach(var error in result.Errors) Report("HISTORY_LOAD_ERROR",error);
            Report("HISTORY_LOADED",$"背景歷史已載入：{result.FileCount} 個檔案，目前 {history.Count} 筆店家買／賣頁。",new {result.FileCount,Pages=result.Listings.Length,result.ErrorCount});
            RefreshRows();
            StartupTiming.Mark("history-ready");
            // Keep synchronization in the awaited restore task, after history is ready.
            // Closing cancels and awaits this task; no detached worker survives the form.
            try
            {
                var syncTask=Task.Run(()=>BackgroundHistory.SynchronizeCatalog(store.Root,token),token);
                historyIoTask=syncTask;
                var catalog=await syncTask;
                if(!closing&&!IsDisposed&&catalog is not null)
                    Report("MOBILE_CATALOG_SYNC",$"商品備份核對完成：新增 {catalog.Copied}、沿用 {catalog.Reused}。",catalog);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception error)
            {if(!closing&&!IsDisposed)Report("MOBILE_CATALOG_SYNC_FAILED","商品備份核對失敗："+error.Message);}
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch(Exception error) {if(!closing&&!IsDisposed)Report("HISTORY_LOAD_FAILED","歷史背景載入失敗："+error.Message);}
        finally {StartupTiming.Mark("history-end");}
    }
}
internal sealed record HistoryReadResult(MarketListing[] Listings,int FileCount,string[] Errors,int ErrorCount,CatalogSyncResult? CatalogSync=null);
internal static class BackgroundHistory
{
    internal static HistoryReadResult Read(string root,CancellationToken token)
    {
        var reader=new LocalStore(root);var errors=new List<string>();int errorCount=0;
        reader.Error+=message=>{errorCount++;if(errors.Count<20)errors.Add(message);};
        var values=reader.LoadHistory(token);
        if(errorCount>errors.Count)errors.Add($"另有 {errorCount-errors.Count} 筆無效歷史；原始檔保留。");
        return new(values,reader.StoredFileCount,errors.ToArray(),errorCount);
    }
    internal static CatalogSyncResult? SynchronizeCatalog(string root,CancellationToken token)
    {token.ThrowIfCancellationRequested();return new LocalStore(root).SynchronizeMobileCatalog(token);}
    internal static void Merge(LocalStore store,MarketListingIndex target,IEnumerable<MarketListing> pages)
    {foreach(var value in pages)if(store.IsListingVisible(value))target.Upsert(value);}
}
