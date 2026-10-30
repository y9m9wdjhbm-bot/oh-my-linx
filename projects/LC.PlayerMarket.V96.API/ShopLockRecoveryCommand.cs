using System.Text.Json;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class ShopLockRecoveryCommand
{
    internal static int Run(string root,string output,string? nativeLogDirectory=null)
    {
        if(ShopStartupRecovery.OtherWorkRunning())throw new InvalidOperationException("請先正常關閉其他商店程式；未修改鎖定資料。");
        root=Path.GetFullPath(root);
        output=Path.GetFullPath(output);
        if(File.Exists(output))throw new IOException("恢復報告已存在，請使用新的輸出檔名；未修改鎖定資料。");
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("指定的商店資料目錄不存在；未建立新資料。");
        using var maintenanceLease=new FileStream(Path.Combine(root,"lock-tool-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(ShopStartupRecovery.OtherWorkRunning())throw new InvalidOperationException("另一個商店或原生查詢程序已啟動；未修改鎖定資料。");
        var scans=new ShopScanProgressStore(Path.Combine(root,"scan-progress"));
        var owners=new ShopSearchedOwnerStore(Path.Combine(root,"searched-owners"),Path.Combine(root,"scan-progress"));
        if(scans.Error is not null||owners.Error is not null)throw new InvalidOperationException(scans.Error+owners.Error);
        var source=scans.Source;
        var results=ShopStartupRecovery.Run(root,scans,owners,nativeLogDirectory:nativeLogDirectory);
        var report=new{At=DateTimeOffset.UtcNow,Results=results,ScanBlocked=source is not null&&scans.BlocksSource(source),
            OwnersBlocked=source is not null&&owners.BlocksSource(source),PendingCount=Directory.GetFiles(Path.Combine(root,"sessions"),"*.pending.json").Length,
            GameReads=0,GameInputs=0,AutoSearchStarted=false,HistoryDeleted=false};
        using(var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        {JsonSerializer.Serialize(stream,report,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
        return results.All(x=>x.State=="RecoveredStopped")&&!report.ScanBlocked&&!report.OwnersBlocked&&report.PendingCount==0?0:1;
    }
}
