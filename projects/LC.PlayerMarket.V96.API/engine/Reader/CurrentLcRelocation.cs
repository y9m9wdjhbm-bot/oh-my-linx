using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace MemoryHub.Reader;

public sealed partial class CurrentLcSource
{
    private sealed record Pending(RelocationCandidate Public,ReaderProfile Profile);
    private Pending? pending;
    public Task<RelocationCandidate> DiscoverAsync(ReadSelection selected,IProgress<RelocationProgress>? progress,CancellationToken token)
        =>Task.Run(()=>Discover(selected,progress,token),token);
    public Task<RelocationCandidate> DiscoverDiagnosticAsync(ReadSelection selected,IProgress<RelocationProgress>? progress,CancellationToken token,bool forceRootSearch)
        =>Task.Run(()=>Discover(selected,progress,token,forceRootSearch),token);
    private RelocationCandidate Discover(ReadSelection selected,IProgress<RelocationProgress>? progress,CancellationToken token,bool forceRootSearch=false)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);pending=null;
            var originalSelection=selected;selected=RelocationFieldPlan.AllFields();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(90));token=deadline.Token;
            var timer=Stopwatch.StartNew();var budget=new Budget(timer,token,maximumMilliseconds:90000,maximumReadBytes:256L*1024*1024);
            void Notify(RelocationProgress value){budget.Check();progress?.Report(value);Emit(new(DateTimeOffset.UtcNow,"RELOCATION_SCAN",value.Message,new{value.Stage,value.Percent}));}
            Emit(new(DateTimeOffset.UtcNow,"RELOCATION_START","開始全欄位有界唯讀重新定位",new{MaximumMilliseconds=90000,MaximumReadBytes=256L*1024*1024,Fields=selected.EnabledKeys,RequestedDiscoveryFields=originalSelection.EnabledKeys,ChangesReadSwitches=false}));
            try
            {
                Notify(new("Identity","核對 LC 磁碟與執行中版本",1));
                using var memory=OpenRelocation(budget,out var identity);
                var profile=RelocationScanner.Discover(memory,identity,selected,Notify,Emit,budget,forceRootSearch);
                Notify(new("Samples","逐項驗證所有欄位的型別、來源與多輪讀值",85));
                var counts=FieldCatalog.All.ToDictionary(f=>f.Key,_=>0);
                var last=Empty(selected,"尚無成功的新版本樣本");SourceIdentity? source=null;long sampleBytes=0;
                bool Capture(ReadSelection fieldsToRead,string phase,int attempt)
                {
                    budget.Check();var result=ReadProfile(token,profile,fieldsToRead);memory.Verify();sampleBytes+=result.ReadBytes;
                    if(sampleBytes+memory.RequestedBytes>budget.MaximumReadBytes)throw new ProbeFailure("READ_BYTE_LIMIT","重定位映像與欄位採樣達總讀取上限。");
                    Emit(new(DateTimeOffset.UtcNow,"RELOCATION_SAMPLE","候選版本逐欄樣本",new{Phase=phase,Attempt=attempt+1,RequestedFields=fieldsToRead.EnabledKeys,Result=result}));
                    foreach(string key in fieldsToRead.EnabledKeys)if(result.Fields.TryGetValue(key,out var field))last[key]=field;
                    if(result.Source is null)return false;
                    if(result.Source.ProcessId!=memory.ProcessId||result.Source.CreationFileTime!=memory.CreationFileTime||result.Source.DiskSha256!=identity.Sha256)
                        throw new ProbeFailure("GROUP_SOURCE_CHANGED","候選採樣來自不同 LC 程序或版本");
                    if(source is not null&&result.Source!=source)throw new ProbeFailure("GROUP_SOURCE_CHANGED","定位期間角色或場景改變，請重新定位");
                    source=result.Source;
                    foreach(string key in fieldsToRead.EnabledKeys)if(result.Fields.TryGetValue(key,out var field)&&RelocationFieldPlan.ConfirmationValue(key,field))counts[key]++;
                    return true;
                }
                for(int attempt=0;attempt<3;attempt++)
                {
                    Capture(selected,"AllFields",attempt);
                    if(attempt>=1&&profile.AllowedFields.All(k=>counts[k]>=2&&RelocationFieldPlan.ConfirmationValue(k,last[k])))break;
                }
                // Short independent retries avoid extending an actor membership
                // window over inventory/HUD reads or discarding other fields.
                foreach(string key in profile.AllowedFields.Where(k=>counts[k]<2||!RelocationFieldPlan.ConfirmationValue(k,last[k])).ToArray())
                {
                    if(last[key].Detail.Contains("PROFILE_SYMBOL_MISSING",StringComparison.Ordinal))continue;
                    for(int attempt=0;attempt<4&&(counts[key]<2||!RelocationFieldPlan.ConfirmationValue(key,last[key]));attempt++)
                    {
                        if(budget.RemainingMilliseconds<5000)break;
                        Notify(new("FieldSamples","獨立重新驗證："+FieldCatalog.All.Single(f=>f.Key==key).Label,90));
                        Capture(new ReadSelection([key]),"IndependentField",attempt);
                    }
                }
                budget.Check();memory.Verify();
                if(source is null)throw new InvalidOperationException("已完成來源搜尋，但尚無通過來源核對的欄位樣本；詳見 LOG，需更新定位規則或登入後重試");
                var now=DateTimeOffset.UtcNow;
                var evidence=profile.RelocationEvidence.ToDictionary(e=>e.Key,StringComparer.Ordinal);
                var fields=FieldCatalog.All.Select(f=>RelocationFieldPlan.Report(f.Key,last[f.Key],counts[f.Key],evidence[f.Key])).ToArray();
                Emit(new(now,"RELOCATION_FIELD_REPORT","所有欄位定位結果；完整／部分／不可用與尚未支援分別保留",new{Total=fields.Length,CanConfirm=fields.Count(f=>f.CanConfirm),Fields=fields,SampleReadBytes=sampleBytes}));
                var candidate=new RelocationCandidate(Guid.NewGuid().ToString("N"),now,now.AddMinutes(5),source,Array.AsReadOnly(fields),
                    $"已列出全部 {fields.Length} 欄，{fields.Count(f=>f.CanConfirm)} 欄可人工確認；其餘保留具體缺項。這是原採樣時間的候選值，保存前再次核對，服務讀取開關保持不變。");
                token.ThrowIfCancellationRequested();pending=new(candidate,profile);
                Emit(new(now,"RELOCATION_READY","候選定位完成，等待使用者逐欄比對",new{candidate.Id,candidate.Source,candidate.ExpiresAt,Fields=fields,ElapsedMs=timer.ElapsedMilliseconds,memory.ReadCalls,memory.RequestedBytes}));
                return candidate;
            }
            catch(Exception error)
            {pending=null;Emit(new(DateTimeOffset.UtcNow,error is OperationCanceledException?"RELOCATION_CANCELLED":"RELOCATION_FAILED",error.Message,new{ElapsedMs=timer.ElapsedMilliseconds,Code=(error as ProbeFailure)?.Code,NativeError=(error as ProbeFailure)?.NativeError,
                Fields=FieldCatalog.All.Select(f=>new{f.Key,State=ReaderProfile.SupportedKeys.Contains(f.Key)?"Unavailable":"Unsupported",Reason=ReaderProfile.SupportedKeys.Contains(f.Key)?error.Message:"尚無可驗證讀取版型",CanConfirm=false}).ToArray()}));throw;}
        }
    }
    public Task<RelocationApplyResult> ApplyAsync(string candidateId,IReadOnlyCollection<string> confirmedFields,CancellationToken token)
        =>Task.Run(()=>Apply(candidateId,confirmedFields,token),token);
    private RelocationApplyResult Apply(string candidateId,IReadOnlyCollection<string> confirmedFields,CancellationToken token)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var candidate=pending??throw new InvalidOperationException("候選不存在或已作廢");
            if(candidate.Public.Id!=candidateId||DateTimeOffset.UtcNow>candidate.Public.ExpiresAt)throw new InvalidOperationException("候選不符或已到期，請重新定位");
            var keys=confirmedFields.ToArray();
            if(keys.Length==0||keys.Distinct().Count()!=keys.Length||keys.Any(k=>!candidate.Public.Fields.Any(f=>f.Key==k&&f.CanConfirm)))throw new InvalidOperationException("包含未確認或無有效樣本的欄位");
            // Save-time proof is field-specific: one unrelated collection must
            // not invalidate every selected field. Every key still needs two
            // fresh closed samples from the exact candidate source.
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(90));
            foreach(string key in keys)
            {
                int closed=0;string lastReason="尚未取得保存前樣本";
                string label=FieldCatalog.All.Single(f=>f.Key==key).Label;
                for(int attempt=0;attempt<5&&closed<2;attempt++)
                {
                    deadline.Token.ThrowIfCancellationRequested();var fresh=ReadProfile(deadline.Token,candidate.Profile,new ReadSelection([key]));
                    if(fresh.Source is not null&&fresh.Source!=candidate.Public.Source)
                        throw new InvalidOperationException($"GROUP_SOURCE_CHANGED：保存前「{label}」（{key}）角色來源已變更；沒有切換設定，請重新定位");
                    bool valid=fresh.Source==candidate.Public.Source&&fresh.Fields.TryGetValue(key,out var field)&&field.Fresh(DateTimeOffset.UtcNow)&&RelocationFieldPlan.ConfirmationValue(key,field);
                    lastReason=fresh.Fields.GetValueOrDefault(key)?.Detail??fresh.Detail;
                    Emit(new(DateTimeOffset.UtcNow,"RELOCATION_APPLY_FIELD_CHECK","保存前逐欄重新核對",new{Key=key,Attempt=attempt+1,Valid=valid,ClosedSamples=closed+(valid?1:0),fresh.Source,Field=fresh.Fields.GetValueOrDefault(key)}));
                    if(valid)closed++;else closed=0;
                }
                if(closed<2)
                    throw new InvalidOperationException($"RELOCATION_APPLY_FIELD_FAILED：保存前「{label}」（{key}）未取得連續 2 輪有效樣本：{lastReason}；沒有切換設定，所有已選項均未保存。");
            }
            deadline.Token.ThrowIfCancellationRequested();
            // Final root/self check closes the entire multi-field validation window.
            var finalSource=ReadProfile(deadline.Token,candidate.Profile,new ReadSelection([keys[0]]));
            if(finalSource.Source!=candidate.Public.Source)
                throw new InvalidOperationException("GROUP_SOURCE_CHANGED：保存前最後來源閉合失敗；沒有切換設定，請重新定位");
            var approved=new ReaderProfile("verified-"+Guid.NewGuid().ToString("N"),candidate.Profile.Identity,candidate.Profile.Rvas,keys,typeBindings:candidate.Profile.TypeBindings);
            string path=profiles.Save(approved,deadline.Token);
            // The active-file rename is the commit point; no cancellation is reported after it.
            activeProfile=approved;profileError=null;pending=null;hashKey=null;diskHash=null;
            var result=new RelocationApplyResult(approved.Id,path,Array.AsReadOnly(keys),$"已保存並套用 {keys.Length} 個已比對欄位；舊版設定保留");
            Emit(new(DateTimeOffset.UtcNow,"PROFILE_APPLIED",result.Detail,result));return result;
        }
    }
    public void DiscardCandidate(string candidateId){lock(gate){if(pending?.Public.Id==candidateId)pending=null;}}

    private ReadSession OpenRelocation(Budget budget,out LcBuildIdentity identity)
    {
        AdministratorExecution.Require();
        var found=new List<(int Id,long Created,ulong Image,int Size)>();
        foreach(var process in Process.GetProcessesByName("LC"))
        using(process)
        {
            budget.Check();try
            {
                var module=process.MainModule;
                if(module is not null&&string.Equals(module.FileName,Probe.ExePath,StringComparison.OrdinalIgnoreCase))
                    found.Add((process.Id,process.StartTime.ToUniversalTime().ToFileTimeUtc(),(ulong)module.BaseAddress,module.ModuleMemorySize));
            }
            catch(InvalidOperationException){}
            // Access denied must be surfaced; it cannot be discarded as a missing LC.
        }
        if(found.Count!=1)throw new InvalidOperationException(found.Count==0?"請先啟動 LC 並登入角色":"有多個相符的 LC，無法唯一選取來源");
        var p=found[0];
        if(p.Size is <4096 or >128*1024*1024||p.Image<0x10000||(p.Image&4095)!=0||p.Image>0x7FFFFFFFFFFFUL-(ulong)p.Size)throw new InvalidDataException("LC 主映像範圍不符");
        var memory=new ReadSession(p.Id,p.Created,budget,audit);
        try
        {
            memory.Bind(p.Image,p.Size);memory.Verify();
            var before=new FileInfo(Probe.ExePath);long length=before.Length;DateTime written=before.LastWriteTimeUtc;
            if(length is <264 or >512L*1024*1024)throw new InvalidDataException("LC 磁碟檔案超出 512 MiB 定位界限");
            if(written.ToFileTimeUtc()>p.Created)throw new InvalidDataException("LC 磁碟檔案在此程序啟動後更新；請重新開啟 LC 並登入後定位");
            using var file=new FileStream(Probe.ExePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            byte[] dos=new byte[64];file.ReadExactly(dos);
            uint peOffset=Probe.U32(dos,0x3C);
            if(Probe.U16(dos,0)!=0x5A4D||peOffset>(uint)p.Size-264||peOffset>length-264)throw new InvalidDataException("磁碟 PE header 範圍不符");
            file.Position=peOffset;byte[] pe=new byte[264];file.ReadExactly(pe);
            if(Probe.U32(pe,0)!=0x4550||Probe.U16(pe,4)!=0x8664||Probe.U16(pe,24)!=0x20B||Probe.U32(pe,80)!=(uint)p.Size)throw new InvalidDataException("磁碟 LC 架構／PE 大小不符");
            file.Position=0;using var hasher=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] hashBuffer=new byte[65536];long hashed=0;
            while(hashed<length){budget.Check();int read=file.Read(hashBuffer,0,(int)Math.Min(hashBuffer.Length,length-hashed));if(read==0)throw new EndOfStreamException("LC 檔案在雜湊期間縮短");hasher.AppendData(hashBuffer,0,read);hashed+=read;}
            string sha=Convert.ToHexString(hasher.GetHashAndReset());budget.Check();
            byte[] loadedDos=memory.Required(0,64),loadedPe=memory.Required(peOffset,264);
            if(Probe.U16(loadedDos,0)!=0x5A4D||Probe.U32(loadedDos,0x3C)!=peOffset||Probe.U32(loadedPe,0)!=0x4550||Probe.U16(loadedPe,4)!=0x8664||Probe.U16(loadedPe,24)!=0x20B||Probe.U32(loadedPe,8)!=Probe.U32(pe,8)||Probe.U32(loadedPe,80)!=Probe.U32(pe,80))
                throw new InvalidDataException("磁碟 LC 與執行中的 PE 版本不同；請完成更新並重新登入後定位");
            VerifyResources(file,pe,memory,budget,peOffset);
            var after=new FileInfo(Probe.ExePath);if(after.Length!=length||after.LastWriteTimeUtc!=written)throw new InvalidDataException("定位時磁碟 LC 發生變更");
            memory.Verify();identity=new(sha,length,peOffset,Probe.U32(pe,8),Probe.U32(pe,80),0x8664);
            Emit(new(DateTimeOffset.UtcNow,"RELOCATION_IDENTITY","定位來源已核對",new{ProcessId=p.Id,CreationFileTime=p.Created,ImageBase=Probe.Hex(p.Image),Identity=identity}));
            return memory;
        }
        catch{memory.Dispose();throw;}
    }
    private void VerifyResources(FileStream file,byte[] pe,ReadSession memory,Budget budget,uint peOffset)
    {
        uint resource=Probe.U32(pe,152),size=Probe.U32(pe,156);int sections=Probe.U16(pe,6),optionalSize=Probe.U16(pe,20);
        if(size is 0 or >8*1024*1024||!memory.InImage(resource,(int)size)||sections is <1 or >96||optionalSize<160)
            throw new InvalidDataException("版本資源無法完整對照，不能確認磁碟與載入來源");
        long table=(long)peOffset+24+optionalSize;
        if(table<0||table+sections*40>file.Length)throw new InvalidDataException("PE section 表超界");
        file.Position=table;byte[] rows=new byte[sections*40];file.ReadExactly(rows);long raw=-1;
        for(int i=0;i<sections;i++)
        {
            int at=i*40;uint va=Probe.U32(rows,at+12),rawSize=Probe.U32(rows,at+16),rawOffset=Probe.U32(rows,at+20);
            if(resource>=va&&(ulong)resource+size<=(ulong)va+rawSize)
            {if(raw!=-1)throw new InvalidDataException("版本資源 section 有歧義");raw=(long)rawOffset+resource-va;}
        }
        if(raw<0||raw+size>file.Length)throw new InvalidDataException("版本資源無法映射到完整磁碟資料");
        file.Position=raw;byte[] disk=new byte[4096];using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for(uint at=0;at<size;at+=4096)
        {
            budget.Check();int count=(int)Math.Min(4096,size-at);file.ReadExactly(disk.AsSpan(0,count));
            if(!memory.Required(resource+at,count).AsSpan().SequenceEqual(disk.AsSpan(0,count)))throw new InvalidDataException("磁碟與載入版本資源不同，請重新啟動更新後的 LC");
            hash.AppendData(disk,0,count);
        }
        Emit(new(DateTimeOffset.UtcNow,"RELOCATION_RESOURCE_MATCH","磁碟與載入版本資源完整一致",new{ResourceRva=Probe.Hex(resource),Bytes=size,Sha256=Convert.ToHexString(hash.GetHashAndReset())}));
    }
}
