using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal sealed record LockToolEntry(string RequestId,int SourcePid,string SourceCreationTime,ulong OwnerId,string Direction,string At,
    string State,string DispatchEvidence,string CleanupEvidence,bool CanRecoverSafely,bool CanForce,string Reason,string Fingerprint,string[] Files);
internal sealed record LockToolScan(string At,string Root,bool Busy,bool Supported,string Detail,LockToolEntry[] Entries);
internal sealed record LockToolPlan(string Root,string RequestId,string Fingerprint,string Mode,bool ConfirmedRisk);
internal sealed record LockToolAction(bool Success,string State,string Detail,string BackupDirectory="",string AuditPath="");
internal sealed record LockBackupFile(string RelativePath,string Sha256,string BackupFile);
internal sealed record LockBackupManifest(int Schema,string Root,string RequestId,string Mode,string BeforeFingerprint,string AfterFingerprint,
    LockBackupFile[] Before,LockBackupFile[] After,DateTimeOffset At);
internal static class MarketLockToolCommands
{
    private static readonly string[] Areas=["sessions","scan-progress","searched-owners","manual-lock-releases","recovery"];
    private static readonly JsonSerializerOptions Json=new(HubProtocol.Json){WriteIndented=true};
    internal static int Run(string command,string input,string output)
    {
        object report;
        try{report=command switch{"--lock-tool-scan"=>Scan(input),"--lock-tool-apply"=>Apply(JsonSerializer.Deserialize<LockToolPlan>(File.ReadAllBytes(input),new JsonSerializerOptions(Json){PropertyNameCaseInsensitive=true})!),"--lock-tool-rollback"=>Rollback(input),_=>throw new ArgumentException("Unknown tool command")};}
        catch(Exception ex){report=command=="--lock-tool-scan"?new LockToolScan(Taiwan(DateTimeOffset.UtcNow),input,true,false,ex.Message,[]):new LockToolAction(false,"Rejected",ex.Message);}
        File.WriteAllText(output,JsonSerializer.Serialize(report,Json));return 0; // The structured action status, not process success, describes the outcome.
    }
    internal static string Taiwan(DateTimeOffset time)=>time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss '台灣時間'",CultureInfo.InvariantCulture);
    private static string Root(string root)
    {
        string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(!full.StartsWith(@"D:\lineage\",StringComparison.OrdinalIgnoreCase)||full.StartsWith(@"\\")||!Directory.Exists(full))throw new InvalidDataException("僅支援本機 D:\\lineage 下既有資料目錄。");
        if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("不接受重新解析的資料路徑。");
        return full;
    }
    internal static LockToolScan Scan(string root,Func<bool>? busyCheck=null)
    {
        root=Root(root);RejectLinks(root);bool busy=(busyCheck??ShopStartupRecovery.OtherWorkRunning)();
        foreach(string required in new[]{"sessions","scan-progress","searched-owners","native"})if(!Directory.Exists(Path.Combine(root,required)))throw new InvalidDataException("缺少已支援的商店資料目錄："+required);
        var scans=ShopScanProgressStore.OpenReadOnly(Path.Combine(root,"scan-progress"));
        var owners=ShopSearchedOwnerStore.OpenReadOnly(Path.Combine(root,"searched-owners"));
        if(scans.Error is not null||owners.Error is not null)throw new InvalidDataException(scans.Error+owners.Error);
        string fingerprint=Fingerprint(Snapshot(root));
        var paths=Directory.GetFiles(Path.Combine(root,"sessions"),"*.pending.json");if(paths.Length>16)throw new InvalidDataException("待處理 pending 超過 16 筆，請人工診斷。");
        var entries=new List<LockToolEntry>();var ids=new HashSet<string>();
        foreach(string path in paths)
        {
            if(new FileInfo(path).Length>32768)throw new InvalidDataException("Pending 大小無效。");
            using var doc=JsonDocument.Parse(File.ReadAllBytes(path));var p=doc.RootElement;
            string id=p.GetProperty("requestId").GetString()!;var source=p.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json)!;
            ulong owner=p.GetProperty("targetOwner").GetUInt64();string direction=p.GetProperty("viewerDirection").GetString()!;
            string native=p.GetProperty("nativeLog").GetString()!;
            if(!ids.Add(id)||!ShopNativePackage.SupportsBuild(source)||p.GetProperty("state").GetString()!="NativeLaunchIntent_NoAutomaticRetry"
                ||Path.GetFileName(path)!=$"{source.ProcessId}-{source.CreationFileTime}.pending.json"
                ||Path.GetFullPath(native)!=Path.Combine(root,"native",Path.GetFileName(native)))throw new InvalidDataException("Pending 來源、格式或元件版本未支援。");
            string intent=Path.Combine(root,"scan-progress","source-intents",ManualLockReleaseStore.Key(id)+".intent.json");
            if(!File.Exists(intent))throw new InvalidDataException("Pending 缺少相符 source-intent。");
            using var intentDoc=JsonDocument.Parse(File.ReadAllBytes(intent));string planId=intentDoc.RootElement.GetProperty("planId").GetString()!;
            if(intentDoc.RootElement.GetProperty("requestId").GetString()!=id||intentDoc.RootElement.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json)!=source)throw new InvalidDataException("Source-intent 身分不符。");
            ulong image=ulong.Parse(source.ImageBase.Replace("0x","",StringComparison.OrdinalIgnoreCase),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            var proof=ShopNativeOutcome.ParseFile(native,source,image,owner,1);
            bool noEntry=proof.SafeToSettle&&proof.NoEntryConfirmed&&!proof.Entered&&!proof.Returned&&proof.Failure is{Code:"NativePreflightReadUnavailable"};
            bool cleanup=noEntry||ForceCleanupEvidence(native,source,image,owner);
            bool shared=File.Exists(Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",$"{source.ProcessId}-{source.CreationFileTime}.pending.json"));
            bool terminal=false;string[] events=Directory.Exists(Path.Combine(root,"scan-progress",planId+".events"))?Directory.GetFiles(Path.Combine(root,"scan-progress",planId+".events"),"*.json").Order(StringComparer.Ordinal).ToArray():[];
            if(events.Length>0){var last=JsonSerializer.Deserialize<ScanProgressEvent>(File.ReadAllBytes(events[^1]),HubProtocol.Json)!;terminal=scans.PlanId==planId&&last.RequestId==id&&last.Kind=="Result"&&last.SessionBlocked&&!last.NativeEntered&&!last.NativeReturned&&!last.ResponseVerified;}
            string reason=busy?"商店或原生 helper 仍在執行，請先正常關閉。":shared?"共用原生 pending 尚未結清，不能強制解除可能仍有活呼叫的鎖。":noEntry?"完整 LOG 證實未派送；可一致結清三處誤鎖。":cleanup?ManualLockReleaseStore.Warning:"無完整 cleanup／来源證據；不能僅憑 PID 消失解除。";
            entries.Add(new(id,source.ProcessId,Taiwan(DateTimeOffset.FromFileTime(source.CreationFileTime)),owner,direction,
                Taiwan(p.GetProperty("at").GetDateTimeOffset()),noEntry?"ConfirmedNotDispatched":"OutcomeUnknown",
                noEntry?"完整序列證明尚未建立派送":$"entered={proof.Entered}, returned={proof.Returned}；未宣稱成功",cleanup?"完整 END 確認 cleanup，無活 helper 才可修":"清理未確認",
                !busy&&!shared&&noEntry&&terminal,!busy&&!shared&&cleanup,reason,fingerprint,
                [path,intent,Path.Combine(root,"scan-progress",planId+".events"),Path.Combine(root,"searched-owners"),ManualLockReleaseStore.PathFor(root,id)]));
        }
        var released=ManualLockReleaseStore.Read(root).Select(x=>x.RequestId).ToHashSet(StringComparer.Ordinal);
        foreach(string intent in Directory.GetFiles(Path.Combine(root,"scan-progress","source-intents"),"*.intent.json"))
        {
            string resolved=intent[..^".intent.json".Length]+".resolved.json";if(File.Exists(resolved))continue;
            using var doc=JsonDocument.Parse(File.ReadAllBytes(intent));string id=doc.RootElement.GetProperty("requestId").GetString()!;
            if(ids.Contains(id)||released.Contains(id))continue;
            if(ShopOrphanRecovery.TryBind(root,id,scans,out var bound,out var why)&&bound is not null)
                entries.Add(new(id,bound.Source.ProcessId,Taiwan(DateTimeOffset.FromFileTime(bound.Source.CreationFileTime)),bound.Owner.Id,bound.Direction,
                    Taiwan(bound.At),"ConfirmedNotDispatchedOrphan","完整 request/response/native/plan 與唯一 resolved 綁定","完整 helper cleanup/no-dispatch",!busy,false,
                    busy?"其他工作仍在執行，請先正常關閉。":why,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint+bound.Fingerprint))),bound.EvidenceFiles));
            else
                entries.Add(new(id,0,"未知",0,"未知",Taiwan(doc.RootElement.GetProperty("at").GetDateTimeOffset()),"OrphanIntent","未能閉合原始證據","未知",false,false,why,fingerprint,[intent]));
        }
        return new(Taiwan(DateTimeOffset.UtcNow),root,busy,true,entries.Count==0?"未發現未結清鎖定；不會自動開始搜尋。":$"發現 {entries.Count} 筆。普通恢復與強制解除分開，原始 LOG／歷史保留。",entries.ToArray());
    }
    private static bool ForceCleanupEvidence(string native,SourceIdentity source,ulong image,ulong owner)
    {
        try
        {
            if(new FileInfo(native).Length>4*1024*1024)return false;
            var rows=File.ReadLines(native).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonDocument.Parse(x)).ToArray();
            try
            {
                if(rows.Length<3||rows.Length>8192||rows.Any(x=>!Unique(x.RootElement))||rows[0].RootElement.GetProperty("Kind").GetString()!="START"||rows[1].RootElement.GetProperty("Kind").GetString()!="SOURCE"||rows.Count(x=>x.RootElement.GetProperty("Kind").GetString()=="START")!=1||rows.Count(x=>x.RootElement.GetProperty("Kind").GetString()=="SOURCE")!=1||rows.Count(x=>x.RootElement.GetProperty("Kind").GetString()=="END")!=1)return false;
                for(int i=0;i<rows.Length;i++)if(rows[i].RootElement.GetProperty("Sequence").GetInt32()!=i+1)return false;
                var start=rows[0].RootElement.GetProperty("Data");
                if(start.GetProperty("Version").GetString()!="13.0.0"||start.GetProperty("NativePayloadVersion").GetString()!="13.0.0"||start.GetProperty("NativeProtocolVersion").GetInt32()!=13||start.GetProperty("NativeGuardContract").GetInt32()!=13||start.GetProperty("MaximumNativeRequests").GetInt32()!=1)return false;
                var s=rows.Single(x=>x.RootElement.GetProperty("Kind").GetString()=="SOURCE").RootElement.GetProperty("Data");
                if(s.GetProperty("Pid").GetInt32()!=source.ProcessId||s.GetProperty("Creation").GetInt64()!=source.CreationFileTime||s.GetProperty("Image").GetUInt64()!=image||s.GetProperty("Sha256").GetString()!=source.DiskSha256||s.GetProperty("TargetOwner").GetUInt64()!=owner)return false;
                if(!string.Equals(s.GetProperty("Path").GetString(),source.ImagePath,StringComparison.OrdinalIgnoreCase)||Convert.ToUInt32(s.GetProperty("Timestamp").GetString(),16)!=source.PeTimestamp||Convert.ToUInt32(s.GetProperty("ImageSize").GetString(),16)!=source.ImageSize)return false;
                var root=rows[^1].RootElement;if(root.GetProperty("Kind").GetString()!="END"||!root.GetProperty("DiskFlush").GetBoolean())return false;
                var end=root.GetProperty("Data");if(!end.GetProperty("CleanupConfirmed").GetBoolean()||end.GetProperty("SessionBlocked").GetBoolean()||end.GetProperty("LogRecordsBeforeEnd").GetInt32()!=rows.Length-1)return false;
                var state=end.GetProperty("NativeResult");return state.ValueKind==JsonValueKind.Object&&state.GetProperty("Active").GetInt32()==0&&state.GetProperty("Completed").GetInt32()==1;
            }
            finally{foreach(var doc in rows)doc.Dispose();}
        }
        catch{return false;}
    }
    internal static LockToolAction Apply(LockToolPlan plan,Func<bool>? busyCheck=null,Action<string>? checkpoint=null)
    {
        if(plan is null||plan.Mode is not("Safe" or "Force")||plan.Mode=="Force"&&!plan.ConfirmedRisk)throw new InvalidDataException("缺少本次操作風險確認。");
        string root=Root(plan.Root);if((busyCheck??ShopStartupRecovery.OtherWorkRunning)())throw new InvalidOperationException("先正常關閉商店／helper，未修改資料。");
        using var lease=new FileStream(Path.Combine(root,"lock-tool-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var scan=Scan(root,busyCheck);var entry=scan.Entries.SingleOrDefault(x=>x.RequestId==plan.RequestId)??throw new InvalidDataException("請重新掃描選擇目前鎖定。");
        if(scan.Busy||!scan.Supported||entry.Fingerprint!=plan.Fingerprint||!(plan.Mode=="Safe"?entry.CanRecoverSafely:entry.CanForce))throw new InvalidDataException("狀態已改變或不符合解除條件，未修改。");
        string backup=Path.Combine(root,"lock-tool-backups",DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
        var before=Snapshot(root);Backup(root,backup,before);
        string manifestPath=Path.Combine(backup,"manifest.json");
        Write(manifestPath,new LockBackupManifest(1,root,plan.RequestId,plan.Mode,Fingerprint(before),"",before,[],DateTimeOffset.UtcNow));
        bool success=false;string detail;
        try
        {
            if((busyCheck??ShopStartupRecovery.OtherWorkRunning)()||Fingerprint(Snapshot(root))!=Fingerprint(before))throw new InvalidOperationException("備份期間出現寫入者或資料變動，未修復。");
            var scans=new ShopScanProgressStore(Path.Combine(root,"scan-progress"));var owners=new ShopSearchedOwnerStore(Path.Combine(root,"searched-owners"),Path.Combine(root,"scan-progress"));
            if(scans.Error is not null||owners.Error is not null)throw new InvalidDataException(scans.Error+owners.Error);
            if(plan.Mode=="Safe")
            {
                var result=ShopStartupRecovery.Run(root,scans,owners,busyCheck,onlyRequest:plan.RequestId);
                if(result is not[{State:"RecoveredStopped"}])throw new InvalidOperationException(JsonSerializer.Serialize(result));
                detail="已安全解除所選誤鎖；商品／歷史保留，未啟動搜尋。";
            }
            else
            {
                string pending=entry.Files[0];using var doc=JsonDocument.Parse(File.ReadAllBytes(pending));var p=doc.RootElement;
                var source=p.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json)!;
                string native=p.GetProperty("nativeLog").GetString()!;
                using var intent=JsonDocument.Parse(File.ReadAllBytes(entry.Files[1]));string planId=intent.RootElement.GetProperty("planId").GetString()!;
                File.Copy(pending,Path.Combine(backup,"original-pending.json"));File.Copy(native,Path.Combine(backup,"native.jsonl"));
                var release=new ManualLockRelease(1,entry.RequestId,planId,source,entry.OwnerId,entry.Direction,DateTimeOffset.UtcNow,
                    "ManualRiskAccepted_OutcomeUnknown",true,ManualLockReleaseStore.Hash(native),ManualLockReleaseStore.Hash(pending),backup);
                string certificate=ManualLockReleaseStore.PathFor(root,entry.RequestId);Directory.CreateDirectory(Path.GetDirectoryName(certificate)!);
                if(File.Exists(certificate))throw new InvalidDataException("已有人工解除證據；請重新掃描，不覆寫。");
                Write(certificate,release);
                checkpoint?.Invoke("ManualCertificateSaved");
                File.Move(pending,pending[..^".pending.json".Length]+".manually-released-"+Guid.NewGuid().ToString("N")+".json");
                checkpoint?.Invoke("ManualPendingRenamed");
                scans.Reload();var check=ShopSearchedOwnerStore.OpenReadOnly(Path.Combine(root,"searched-owners"));
                if(scans.Error is not null||check.Error is not null)throw new InvalidDataException(scans.Error+check.Error);
                if(!ManualLockReleaseStore.Read(root).Any(x=>x.RequestId==plan.RequestId)||File.Exists(pending))throw new InvalidDataException("人工解除尚未完整保存。");
                detail=ManualLockReleaseStore.Warning+"原始事件完整保留，沒有偽造未派送或成功。此人工解除需 Market V43 以上讀取；舊版仍會保守封鎖。";
            }
            success=true;
        }
        catch(Exception ex){detail="恢復未完整完成，請保留備份並重新掃描或回退："+ex.Message;}
        var after=Snapshot(root);Write(manifestPath,new LockBackupManifest(1,root,plan.RequestId,plan.Mode,Fingerprint(before),Fingerprint(after),before,after,DateTimeOffset.UtcNow));
        string audit=Path.Combine(backup,"audit.json");Write(audit,new{At=DateTimeOffset.UtcNow,plan.RequestId,plan.Mode,plan.ConfirmedRisk,Success=success,Detail=detail,GameInputs=0,AutoResend=false});
        return new(success,success?"RecoveredStopped":"IncompleteStopped",detail,backup,audit);
    }
    internal static LockToolAction Rollback(string manifestPath,Func<bool>? busyCheck=null)
    {
        if((busyCheck??ShopStartupRecovery.OtherWorkRunning)())throw new InvalidOperationException("回退前請正常關閉商店與 helper。");
        var manifest=JsonSerializer.Deserialize<LockBackupManifest>(File.ReadAllBytes(manifestPath),HubProtocol.Json)??throw new InvalidDataException("備份 manifest 無效。");
        string root=Root(manifest.Root),backup=Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        if(manifest.Schema!=1||!backup.StartsWith(Path.Combine(root,"lock-tool-backups")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("不支援的備份位置或版本。");
        RejectLinks(root);RejectLinks(backup);
        ValidateManifestFiles(root,manifest.Before);ValidateManifestFiles(root,manifest.After);
        if(Fingerprint(manifest.Before)!=manifest.BeforeFingerprint||Fingerprint(manifest.After)!=manifest.AfterFingerprint)throw new InvalidDataException("Manifest 清單校驗不符。");
        using var lease=new FileStream(Path.Combine(root,"lock-tool-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string marker=Path.Combine(backup,"rollback.json");string current=Fingerprint(Snapshot(root));
        if(File.Exists(marker)&&current==manifest.BeforeFingerprint)return new(true,"AlreadyRolledBack","先前已回退，未重複修改。",backup,marker);
        if(string.IsNullOrEmpty(manifest.AfterFingerprint)||current!=manifest.AfterFingerprint)throw new InvalidDataException("恢復後已有其他操作或資料改變，禁止覆寫新紀錄。");
        foreach(var file in manifest.Before){string saved=Bounded(backup,file.BackupFile);if(ManualLockReleaseStore.Hash(saved)!=file.Sha256)throw new InvalidDataException("備份雜湊不符。");}
        var old=manifest.Before.ToDictionary(x=>x.RelativePath,StringComparer.OrdinalIgnoreCase);
        foreach(var file in manifest.After)
        {
            if(old.ContainsKey(file.RelativePath))continue;
            string path=Bounded(root,file.RelativePath),to=Bounded(backup,"rollback-quarantine/"+file.RelativePath);Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Move(path,to);
        }
        foreach(var file in manifest.Before)
        {
            string path=Bounded(root,file.RelativePath);if(File.Exists(path)&&ManualLockReleaseStore.Hash(path)==file.Sha256)continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temp=path+".rollback-"+Guid.NewGuid().ToString("N")+".tmp";File.Copy(Bounded(backup,file.BackupFile),temp);File.Move(temp,path,true);
        }
        if(Fingerprint(Snapshot(root))!=manifest.BeforeFingerprint)throw new InvalidDataException("回退後校驗未通過，保持停止。");
        Write(marker,new{At=DateTimeOffset.UtcNow,Success=true,RestoredOriginalLocks=true,GameInputs=0});
        return new(true,"RolledBack","已回退本次工具修改；原鎖與原始未知狀態恢復，未啟動搜尋。",backup,marker);
    }
    private static string Bounded(string root,string relative)
    {if(Path.IsPathFullyQualified(relative))throw new InvalidDataException("備份包含絕對路徑。");string full=Path.GetFullPath(Path.Combine(root,relative));if(!full.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("備份路徑超出界線。");return full;}
    private static LockBackupFile[] Snapshot(string root)
    {
        var result=new List<LockBackupFile>();long total=0;
        foreach(string area in Areas){string folder=Path.Combine(root,area);if(!Directory.Exists(folder))continue;
            foreach(string file in SafeFiles(folder).Order(StringComparer.Ordinal))
            {var info=new FileInfo(file);if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("狀態含重新解析檔案。");if(info.Name.EndsWith(".lock",StringComparison.OrdinalIgnoreCase)||info.Name.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))continue;
                total+=info.Length;if(total>128*1024*1024||result.Count>=20000)throw new InvalidDataException("狀態超過有界備份範圍。");
                string relative=Path.GetRelativePath(root,file);result.Add(new(relative,ManualLockReleaseStore.Hash(file),"files/"+relative));}}
        return result.OrderBy(x=>x.RelativePath,StringComparer.Ordinal).ToArray();
    }
    private static bool Unique(JsonElement value)
    {if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject())if(!names.Add(p.Name)||!Unique(p.Value))return false;}else if(value.ValueKind==JsonValueKind.Array){foreach(var e in value.EnumerateArray())if(!Unique(e))return false;}return true;}
    private static void RejectLinks(string path)
    {for(var current=new DirectoryInfo(Path.GetFullPath(path));current!=null;current=current.Parent)if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("不接受重新解析路徑。");}
    private static IEnumerable<string> SafeFiles(string folder)
    {RejectLinks(folder);foreach(string file in Directory.EnumerateFiles(folder))yield return file;foreach(string dir in Directory.EnumerateDirectories(folder))foreach(string file in SafeFiles(dir))yield return file;}
    private static void ValidateManifestFiles(string root,LockBackupFile[] files)
    {if(files.Length>20000||files.Select(x=>x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Length)throw new InvalidDataException("備份檔案數或重複路徑無效。");foreach(var f in files){string path=Bounded(root,f.RelativePath);if(Path.GetRelativePath(root,path)!=f.RelativePath||!Areas.Contains(f.RelativePath.Split(Path.DirectorySeparatorChar)[0],StringComparer.Ordinal)||f.BackupFile!="files/"+f.RelativePath)throw new InvalidDataException("Manifest 不在允許的狀態檔案範圍。");RejectLinks(Path.GetDirectoryName(path)!);}}
    private static string Fingerprint(LockBackupFile[] files)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',files.Select(x=>x.RelativePath+"="+x.Sha256)))));
    private static void Backup(string root,string backup,LockBackupFile[] files){foreach(var f in files){string to=Bounded(backup,f.BackupFile);Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(Bounded(root,f.RelativePath),to);if(ManualLockReleaseStore.Hash(to)!=f.Sha256)throw new InvalidDataException("備份校驗失敗。");}}
    private static void Write<T>(string path,T value){string temp=path+".tmp-"+Guid.NewGuid().ToString("N");using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){JsonSerializer.Serialize(f,value,Json);f.Flush(true);}File.Move(temp,path,true);}
}
