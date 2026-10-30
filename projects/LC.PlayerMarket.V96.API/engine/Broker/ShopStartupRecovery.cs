using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MemoryHub.Service;

internal sealed record StartupRecoveryItem(string RequestId,string State,string Detail,string? BackupDirectory=null);
internal static partial class ShopStartupRecovery
{
    private sealed record Pending(string State,string RequestId,SourceIdentity Source,MarketOwner Owner,ulong TargetOwner,string ViewerDirection,string NativeLog);
    internal static StartupRecoveryItem[] Run(string root,ShopScanProgressStore scans,ShopSearchedOwnerStore owners,
        Func<bool>? busyCheck=null,Action<string>? checkpoint=null,string? onlyRequest=null,
        Action<ShopStartupStageEvent>? startupStage=null,string? nativeLogDirectory=null)
    {
        var output=new List<StartupRecoveryItem>();
        root=Path.GetFullPath(root);
        string nativeRoot=Path.GetFullPath(nativeLogDirectory??Path.Combine(root,"native"));
        string sessions=Path.Combine(root,"sessions");
        if(!Directory.Exists(sessions))return [];
        string[] pendingFiles=ShopStartupTrace.Run("recovery.pending-enumerate",startupStage,
            () => Directory.GetFiles(sessions,"*.pending.json"),files=>files.Length);
        var orphanBindings=new Dictionary<string,OrphanRecoveryBinding>(StringComparer.OrdinalIgnoreCase);
        string intents=Path.Combine(root,"scan-progress","source-intents");
        ShopStartupTrace.Run("recovery.orphan-bindings",startupStage,() =>
        {
            if(Directory.Exists(intents))foreach(string intentFile in Directory.GetFiles(intents,"*.intent.json"))
            {
                string intentKey=Path.GetFileName(intentFile)[..^".intent.json".Length];
                string recoveryDir=Path.Combine(root,"recovery",intentKey);
                if(File.Exists(intentFile[..^".intent.json".Length]+".resolved.json")
                    &&(!File.Exists(Path.Combine(recoveryDir,"proof.json"))||Directory.GetFiles(recoveryDir,"completed-*.json").Length>0))continue;
                var intent=JsonSerializer.Deserialize<ScanIntentEvidence>(File.ReadAllBytes(intentFile),HubProtocol.Json);
                if(intent is null||(onlyRequest is not null&&intent.RequestId!=onlyRequest))continue;
                if(ShopOrphanRecovery.TryBind(root,intent.RequestId,scans,out var binding,out _)&&binding is not null)
                    orphanBindings.Add(binding.SessionFile,binding);
            }
            return orphanBindings.Count;
        },count=>count);
        string[] files=pendingFiles.Concat(orphanBindings.Keys).ToArray();
        if(files.Length>16)return [new("","Deferred","待處理紀錄超過自動恢復界線，未改動。")];
        if(files.Length==0)return [];
        if((busyCheck??OtherWorkRunning)())return [new("","Deferred","另一個商店或原生查詢程序仍存在，未解除鎖定。")];
        using var lease=scans.AcquireRunLease();
        foreach(string file in files)
        {
            string request="";
            try
            {
                if(new FileInfo(file).Length>32768)throw new InvalidDataException("Pending exceeds bounds.");
                byte[] pendingBytes=File.ReadAllBytes(file);
                var p=JsonSerializer.Deserialize<Pending>(pendingBytes,HubProtocol.Json)??throw new InvalidDataException("Pending missing.");
                request=p.RequestId;
                if(onlyRequest is not null&&request!=onlyRequest)continue;
                bool force=p.State=="ForceNativeLaunchIntent_NoAutomaticReplay";
                if((!force&&p.State!="NativeLaunchIntent_NoAutomaticRetry")||p.Source is null||p.Owner is null||p.TargetOwner!=p.Owner.Id
                    ||p.RequestId.Length is 0 or >128||p.RequestId.Any(char.IsControl)
                    ||p.ViewerDirection is not("ViewerBuy" or "ViewerSell")
                    ||(force&&orphanBindings.ContainsKey(file))
                    ||(!force&&!orphanBindings.ContainsKey(file)&&Path.GetFileName(file)!=$"{p.Source.ProcessId}-{p.Source.CreationFileTime}.pending.json"))throw new InvalidDataException("Pending identity mismatch.");
                string nativeName=Path.GetFileName(p.NativeLog);
                if(nativeName.Length!=38||!nativeName.EndsWith(".jsonl",StringComparison.Ordinal)||!Guid.TryParseExact(nativeName[..32],"N",out _)
                    ||!string.Equals(Path.GetFullPath(p.NativeLog),Path.Combine(nativeRoot,nativeName),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Native log outside exact request directory.");
                string shared=Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",$"{p.Source.ProcessId}-{p.Source.CreationFileTime}.pending.json");
                if(File.Exists(shared))throw new InvalidDataException("Shared native pending remains; no recovery.");
                var manual=ManualLockReleaseStore.Read(root).SingleOrDefault(x=>x.RequestId==request);
                if(manual is not null)
                {
                    if(force)throw new InvalidDataException("Force pending requires complete dedicated no-dispatch evidence; manual release does not substitute for it.");
                    if(manual.Source!=p.Source||manual.OwnerId!=p.TargetOwner||manual.Direction!=p.ViewerDirection||Hash(file)!=manual.PendingSha256)
                        throw new InvalidDataException("中斷人工解除的 pending 身分不符。");
                    File.Move(file,file[..^".pending.json".Length]+".manually-released-"+Guid.NewGuid().ToString("N")+".json");
                    output.Add(new(request,"ManualReleasedStopped",ManualLockReleaseStore.Warning,manual.BackupDirectory));
                    continue;
                }
                if(!ulong.TryParse(p.Source.ImageBase.Replace("0x","",StringComparison.OrdinalIgnoreCase),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out ulong image))throw new InvalidDataException("Image base invalid.");
                string parsedNativeHash=Hash(p.NativeLog);
                var proof=ShopNativeOutcome.ParseFile(p.NativeLog,p.Source,image,p.Owner.Id,1);
                if(Hash(p.NativeLog)!=parsedNativeHash)throw new InvalidDataException("Native evidence changed during parsing.");
                bool ownerChanged=IsOwnerChangeProof(proof);
                if(force?!ownerChanged:(!ownerChanged&&(!proof.SafeToSettle||!proof.NoEntryConfirmed||proof.Entered||proof.Returned||proof.Failure is not{Phase:"Preflight"}||proof.Failure.Code is not("NativePreflightReadUnavailable" or "NativeCodeUnavailable"))))
                    throw new InvalidDataException("缺少完整未派送證據；原鎖保留。"+proof.Detail);
                if(scans.Error is not null||scans.Source!=p.Source||scans.PlanId is null)throw new InvalidDataException("Scan plan differs or cannot replay.");
                ForceRecoveryBinding? forceBinding=force?BindForceOwnerChange(root,file,p,scans,proof):null;
                ForceRecoveryInput[]? ownerChangeInputs=ownerChanged?BindOwnerChangeMode(file,p,force,parsedNativeHash):null;
                if(!File.ReadAllBytes(file).AsSpan().SequenceEqual(pendingBytes))throw new InvalidDataException("Pending changed while binding recovery evidence.");
                owners.ValidateUnpublishedRecovery(request,p.Source,p.Owner.Id,p.Owner.LocalIdentity,p.ViewerDirection);
                string eventDir=Path.Combine(root,"scan-progress",scans.PlanId+".events");
                var eventFiles=Directory.GetFiles(eventDir,"*.json").Order(StringComparer.Ordinal).ToArray();
                if(eventFiles.Length==0)throw new InvalidDataException("No terminal scan event.");
                string terminal=eventFiles[^1];
                var last=JsonSerializer.Deserialize<ScanProgressEvent>(File.ReadAllBytes(terminal),HubProtocol.Json)??throw new InvalidDataException("No terminal event.");
                if(last.PlanId!=scans.PlanId||last.RequestId!=p.RequestId||last.OwnerId!=p.Owner.Id||last.OwnerIdentity!=p.Owner.LocalIdentity||last.Direction!=p.ViewerDirection
                    ||last.NativeEntered||last.NativeReturned||last.ResponseVerified||last.Kind is not("Result" or "NativeSettled"))throw new InvalidDataException("Terminal event has different request or possible dispatch.");
                string backup=Path.Combine(root,"recovery",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request))));
                string receiptPath=Path.Combine(backup,"proof.json"),originalPath=Path.Combine(backup,"original-terminal-result.json");
                string nativeHash=Hash(p.NativeLog);
                if(!File.Exists(receiptPath))
                {
                    if(last.Kind!="Result"||!last.SessionBlocked)throw new InvalidDataException("New recovery requires exact terminal blocked result.");
                    Directory.CreateDirectory(backup);
                    if(forceBinding is not null)BackupForceEvidence(root,backup,forceBinding);
                    if(orphanBindings.TryGetValue(file,out var orphan))
                    {
                        ShopOrphanRecovery.CheckUnchanged(orphan);
                        foreach(string input in orphan.EvidenceFiles)BackupExact(input,Path.Combine(backup,"orphan-"+Path.GetFileName(input)));
                        string bindingPath=Path.Combine(backup,"orphan-binding.json");
                        if(!File.Exists(bindingPath))WriteNew(bindingPath,orphan);
                        // Back up the complete persistent owner ledger before
                        // changing the terminal event or settlement index.
                        string ownerRoot=Path.Combine(root,"searched-owners");
                        var ownerFiles=Directory.GetFiles(ownerRoot,"*",SearchOption.AllDirectories)
                            .Where(x=>!x.EndsWith(".lock",StringComparison.OrdinalIgnoreCase)&&!x.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase)).ToArray();
                        if(ownerFiles.Length>20000||ownerFiles.Sum(x=>new FileInfo(x).Length)>128L*1024*1024)throw new InvalidDataException("Owner ledger backup exceeds bounds.");
                        foreach(string input in ownerFiles)
                        {string to=Path.Combine(backup,"original-owner-ledger",Path.GetRelativePath(ownerRoot,input));Directory.CreateDirectory(Path.GetDirectoryName(to)!);BackupExact(input,to);}
                    }
                    BackupExact(file,Path.Combine(backup,"original-session.pending.json"));
                    BackupExact(p.NativeLog,Path.Combine(backup,"native.jsonl"));
                    BackupExact(terminal,originalPath);
                    WriteNew(receiptPath,new{Schema=1,RequestId=request,Source=p.Source,OwnerId=p.Owner.Id,Direction=p.ViewerDirection,
                        NativeSha256=nativeHash,OriginalEventSha256=Hash(originalPath),OriginalSequence=last.Sequence,At=DateTimeOffset.UtcNow,
                        Proof="Complete helper preflight no-dispatch; cleanup confirmed",GameRequests=0});
                    if(forceBinding is not null)checkpoint?.Invoke("ForceProofSaved");
                }
                using(var doc=JsonDocument.Parse(File.ReadAllBytes(receiptPath)))
                {
                    var r=doc.RootElement;
                    if(r.GetProperty("requestId").GetString()!=request||r.GetProperty("nativeSha256").GetString()!=nativeHash
                        ||r.GetProperty("originalEventSha256").GetString()!=Hash(originalPath))throw new InvalidDataException("Recovery receipt changed.");
                }
                var original=JsonSerializer.Deserialize<ScanProgressEvent>(File.ReadAllBytes(originalPath),HubProtocol.Json)!;
                if(original.PlanId!=scans.PlanId||original.RequestId!=request||original.OwnerId!=p.Owner.Id||original.OwnerIdentity!=p.Owner.LocalIdentity||original.Direction!=p.ViewerDirection
                    ||original.Kind!="Result"||!original.SessionBlocked||original.NativeEntered||original.NativeReturned||original.ResponseVerified)
                    throw new InvalidDataException("Original failed result proof invalid.");
                if((busyCheck??OtherWorkRunning)())throw new InvalidDataException("Work started during recovery; pending retained.");
                if(orphanBindings.TryGetValue(file,out var bound))ShopOrphanRecovery.CheckUnchanged(bound);
                CheckOwnerChangeUnchanged(ownerChangeInputs,p.Source);
                if(forceBinding is not null)CheckForceUnchanged(forceBinding);
                string detail="啟動時已依完整原生 LOG 證實未派送，結清本次誤鎖；原始失敗紀錄及來源證據保留於 "+backup+"。未自動搜尋。";
                if(last.Sequence==original.Sequence&&last.Kind=="Result"&&last.SessionBlocked)
                {
                    if(Hash(terminal)!=Hash(originalPath))throw new InvalidDataException("Terminal changed since backup.");
                    var settled=original with{Kind="NativeSettled",SessionBlocked=false,At=DateTimeOffset.UtcNow,Detail=detail};
                    string temp=terminal+".recovery-"+Guid.NewGuid().ToString("N")+".tmp";
                    WriteNew(temp,settled);File.Move(temp,terminal,true);
                    checkpoint?.Invoke("TerminalSettled");
                    if((busyCheck??OtherWorkRunning)())throw new InvalidDataException("Work started during recovery.");
                    if(orphanBindings.TryGetValue(file,out var afterTerminal))ShopOrphanRecovery.CheckUnchanged(afterTerminal);
                    CheckOwnerChangeUnchanged(ownerChangeInputs,p.Source);
                    if(forceBinding is not null)CheckForceUnchanged(forceBinding);
                    scans.Reload();
                    if(scans.Error is not null)throw new InvalidDataException(scans.Error);
                    last=settled;
                }
                if(last.Kind=="NativeSettled"&&last.Sequence==original.Sequence)
                {
                    scans.Result(request,proof.Failure!.Attach(new(){RequestId=request,State="RecoveredStopped",Detail=detail}));
                    checkpoint?.Invoke("ResultRestored");
                }
                else if(last.Kind!="Result"||last.SessionBlocked||last.Sequence!=original.Sequence+1)
                    throw new InvalidDataException("Recovery tail inconsistent.");
                if((busyCheck??OtherWorkRunning)())throw new InvalidDataException("Work started during recovery.");
                if(orphanBindings.TryGetValue(file,out var beforeOwner))ShopOrphanRecovery.CheckUnchanged(beforeOwner);
                CheckOwnerChangeUnchanged(ownerChangeInputs,p.Source);
                if(forceBinding is not null)CheckForceUnchanged(forceBinding);
                owners.ValidateUnpublishedRecovery(request,p.Source,p.Owner.Id,p.Owner.LocalIdentity,p.ViewerDirection);
                owners.SettleUnpublishedRecovery(request,p.Source,p.Owner.Id,p.ViewerDirection);
                checkpoint?.Invoke("OwnerSettled");
                scans.Reload();
                if(scans.Error is not null)throw new InvalidDataException(scans.Error);
                // Resolve only this exact session pending after all persistent
                // stores replay. Other uncertain operations remain untouched.
                if(!File.ReadAllBytes(file).AsSpan().SequenceEqual(pendingBytes))throw new InvalidDataException("Pending changed during recovery.");
                if((busyCheck??OtherWorkRunning)())throw new InvalidDataException("Work started before recovery completion.");
                if(orphanBindings.TryGetValue(file,out var finalBinding))ShopOrphanRecovery.CheckUnchanged(finalBinding);
                CheckOwnerChangeUnchanged(ownerChangeInputs,p.Source);
                if(forceBinding is not null)CheckForceUnchanged(forceBinding);
                if(!orphanBindings.ContainsKey(file))File.Move(file,file[..^".pending.json".Length]+".resolved-preflight-"+Guid.NewGuid().ToString("N")+".json");
                WriteNew(Path.Combine(backup,"completed-"+Guid.NewGuid().ToString("N")+".json"),new{At=DateTimeOffset.UtcNow,RequestId=request,State="RecoveredStopped",GameRequests=0});
                output.Add(new(request,"RecoveredStopped",detail,backup));
            }
            catch(Exception ex){output.Add(new(request,"Deferred",ex.Message));}
        }
        return output.ToArray();
    }
    internal static bool OtherWorkRunning()
    {
        foreach(var process in Process.GetProcesses())
        {
            using(process)try
            {
                if(process.Id==Environment.ProcessId)continue;
                string name=process.ProcessName;
                if(name.StartsWith("LC.PlayerMarket.V",StringComparison.OrdinalIgnoreCase)
                    ||name.StartsWith("LC_PlayerShopListActionV",StringComparison.OrdinalIgnoreCase))return true;
            }
            catch(InvalidOperationException){}
        }
        return false;
    }
    private static string Hash(string path){using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f));}
    private static void BackupExact(string from,string to){if(!File.Exists(to))File.Copy(from,to);if(Hash(from)!=Hash(to))throw new InvalidDataException("Backup mismatch.");}
    private static void WriteNew<T>(string path,T value){using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(f,value,HubProtocol.Json);f.Flush(true);}
}
