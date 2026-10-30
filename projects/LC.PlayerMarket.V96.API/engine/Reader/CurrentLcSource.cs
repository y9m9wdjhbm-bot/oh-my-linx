using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Reader;

/// <summary>A single, serialized, read-only LC sampler. Consumers receive data, never addresses or process handles.</summary>
public sealed partial class CurrentLcSource : ISlicedMemorySource,IRelocationSource
{
    private readonly object gate=new();
    private readonly Action<HubLog>? log;
    private readonly Action<ulong,int>? audit;
    private bool disposed;
    private string? hashKey;
    private string? diskHash;
    private ReadSelection selection=new();
    private readonly ProfileStore profiles;
    private ReaderProfile? activeProfile;
    private string? profileError;
    private readonly ModuleMetadataCache metadataCache=new();
    private readonly bool metadataReuse;
    private readonly ItemStateContradictionStore itemStateContradictions;
    public CurrentLcSource(Action<HubLog>? log=null,Action<ulong,int>? readRangeAudit=null,string? profileDirectory=null,bool reuseVerifiedMetadata=true,string? itemStateContradictionDirectory=null)
    {
        this.log=log;audit=readRangeAudit;metadataReuse=reuseVerifiedMetadata;
        itemStateContradictions=new(itemStateContradictionDirectory);
        profiles=new(profileDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LCMemoryHub","v32","profiles"));
        try{activeProfile=profiles.Load();}catch(Exception error){profileError="版本設定載入失敗："+error.Message;Emit(new(DateTimeOffset.UtcNow,"PROFILE_LOAD_FAILED",profileError));}
    }
    public void Configure(ReadSelection value){InvalidateClosedShopRead();Volatile.Write(ref selection,value??throw new ArgumentNullException(nameof(value)));}

    public ReadResult Read(CancellationToken cancellationToken)
    {
        var admission=Stopwatch.StartNew();
        using var readAdmission=ReadAdmission.Enter(gate,cancellationToken);
        {
            cancellationToken.ThrowIfCancellationRequested();
            double admissionWaitMs=admission.Elapsed.TotalMilliseconds;
            Emit(new(DateTimeOffset.UtcNow,"READ_ADMISSION","來源序列化等待",new{Operation="Read",WaitMs=admissionWaitMs}));
            if(profileError is not null&&selection.Any){InvalidateClosedShopRead();return new(null,Empty(selection,profileError),"Unsupported",profileError);}
            var body=Stopwatch.StartNew();var result=ReadProfile(cancellationToken,activeProfile??ReaderProfile.Builtin,reuseMetadata:metadataReuse);
            return result with {ReadMetrics=new(admissionWaitMs,body.Elapsed.TotalMilliseconds)};
        }
    }
    public ReadResult ReadSlice(ReadSelection fields,CancellationToken cancellationToken)
    {
        var admission=Stopwatch.StartNew();
        using var readAdmission=ReadAdmission.Enter(gate,cancellationToken);
        {
            cancellationToken.ThrowIfCancellationRequested();
            double admissionWaitMs=admission.Elapsed.TotalMilliseconds;
            Emit(new(DateTimeOffset.UtcNow,"READ_ADMISSION","來源序列化等待",new{Operation="ReadSlice",WaitMs=admissionWaitMs}));
            var configured=Volatile.Read(ref selection);
            var requested=new ReadSelection(fields.EnabledKeys.Where(configured.Enabled));
            if(profileError is not null&&requested.Any){InvalidateClosedShopRead();return new(null,Empty(requested,profileError),"Unsupported",profileError);}
            var body=Stopwatch.StartNew();var result=ReadProfile(cancellationToken,activeProfile??ReaderProfile.Builtin,requested,configured,reuseMetadata:metadataReuse);
            return result with {ReadMetrics=new(admissionWaitMs,body.Elapsed.TotalMilliseconds)};
        }
    }
    private ReadResult ReadProfile(CancellationToken cancellationToken,ReaderProfile profile,ReadSelection? requestedSelection=null,ReadSelection? configurationFence=null,bool reuseMetadata=false,
        Action<ShopActionSourceBinding>? observedShopActionSource=null,
        Action<ReadSession,Journal,Budget>? readNavigation=null, ulong? dispatchTargetOwnerId=null, bool shopBindingOnly=false)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var selected=requestedSelection??Volatile.Read(ref selection);
            if(!selected.Any){InvalidateClosedShopRead();return new(null,selected.Empty("所有欄位已停用"),"Disabled","所有欄位已停用，未開啟 LC"){NoSourceRead=true};}
            var timer=Stopwatch.StartNew();var budget=new Budget(timer,cancellationToken,()=>configurationFence is not null?ReferenceEquals(configurationFence,Volatile.Read(ref selection)):requestedSelection is not null||ReferenceEquals(selected,Volatile.Read(ref selection)));
            ReadSession? memory=null;
            var fields=Empty(selected,"等待本輪採樣");
            var retryableFields=new HashSet<string>(StringComparer.Ordinal);
            var effective=new ReadSelection(selected.EnabledKeys.Where(profile.Allows));
            foreach(string key in selected.EnabledKeys.Where(k=>!profile.Allows(k)))fields[key]=FieldReading.Unknown("此版本設定尚未確認該欄，請重新定位並比對後保存");
            if(!effective.Any){InvalidateClosedShopRead();return new(null,fields,"Unsupported","所選欄位尚無已確認的版本設定，未開啟 LC"){NoSourceRead=true};}
            try
            {
                budget.Check();AdministratorExecution.Require();var candidates=new List<(int Pid,long Created)>();
                Exception? accessFailure=null;
                foreach(var process in Process.GetProcessesByName("LC"))
                {
                    using(process)
                    {
                        try
                        {
                            budget.Check();
                            var module=process.MainModule;
                            if(module is not null&&string.Equals(module.FileName,Probe.ExePath,StringComparison.OrdinalIgnoreCase))
                                candidates.Add((process.Id,process.StartTime.ToUniversalTime().ToFileTimeUtc()));
                        }
                        catch(InvalidOperationException){/* Process exited during enumeration. */}
                        catch(Win32Exception error){accessFailure=error;}
                    }
                }
                // An inaccessible LC cannot be silently excluded when choosing a unique source.
                if(accessFailure is not null)throw accessFailure;
                if(candidates.Count==0){InvalidateClosedShopRead();if(reuseMetadata)metadataCache.Invalidate("目前沒有 LC.exe");return new(null,Empty(selected,"等待 LC 啟動及登入"),"Waiting","目前沒有 LC.exe");}
                if(candidates.Count!=1){InvalidateClosedShopRead();if(reuseMetadata)metadataCache.Invalidate("來源不唯一");return new(null,Empty(selected,"有多個相符 LC 程序"),"Ambiguous","無法唯一選取 LC.exe");}
                var candidate=candidates[0];
                memory=new ReadSession(candidate.Pid,candidate.Created,budget,audit);memory.Verify();
                using(var process=Process.GetProcessById(candidate.Pid))
                {
                    var module=process.MainModule??throw new ProbeFailure("UNSUPPORTED_BUILD","主模組未知。");
                    if(!string.Equals(module.FileName,Probe.ExePath,StringComparison.OrdinalIgnoreCase)||module.ModuleMemorySize!=profile.Identity.ImageSize)
                        throw new ProbeFailure("UNSUPPORTED_BUILD","LC 主模組路徑或大小不符已驗版型。");
                    ulong image=(ulong)module.BaseAddress;
                    if(image<0x10000||(image&4095)!=0||image>0x7FFFFFFFFFFFUL-profile.Identity.ImageSize)
                        throw new ProbeFailure("UNSUPPORTED_BUILD","LC 模組基址或範圍無效。");
                    memory.Bind(image,module.ModuleMemorySize);
                    memory.UseProfile(profile);
                }
                var beforeFile=new FileInfo(Probe.ExePath);long length=beforeFile.Length;DateTime modified=beforeFile.LastWriteTimeUtc;
                if(length is <264 or >512L*1024*1024)throw new ProbeFailure("UNSUPPORTED_BUILD","LC 磁碟檔案大小超出讀取界限。");
                if(modified.ToFileTimeUtc()>candidate.Created)throw new ProbeFailure("UNSUPPORTED_BUILD","磁碟 LC 在此程序啟動後更新，請重新啟動 LC 後讀取。");
                string key=$"{candidate.Pid}:{candidate.Created}:{length}:{modified.Ticks}";
                if(hashKey!=key)
                {
                    using var file=new FileStream(Probe.ExePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    using var hasher=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] block=new byte[65536];long hashed=0;
                    while(hashed<length)
                    {
                        budget.Check();int read=file.Read(block,0,(int)Math.Min(block.Length,length-hashed));
                        if(read==0)throw new ProbeFailure("GROUP_SOURCE_CHANGED","磁碟 LC 在雜湊期间縮短。");
                        hasher.AppendData(block,0,read);hashed+=read;
                    }
                    budget.Check();diskHash=Convert.ToHexString(hasher.GetHashAndReset());hashKey=key;
                }
                budget.Check();
                if(diskHash!=profile.Sha256)throw new ProbeFailure("UNSUPPORTED_BUILD","LC 磁碟 SHA256 尚無相符的已驗讀取版型，請使用重新定位。");
                var dos=memory.Required(0,64);
                if(Probe.U16(dos,0)!=0x5A4D||Probe.U32(dos,0x3C)!=profile.Identity.PeOffset)throw new ProbeFailure("UNSUPPORTED_BUILD","載入 DOS header 不符。");
                var pe=memory.Required(profile.Identity.PeOffset,0x108);
                if(Probe.U32(pe,0)!=0x4550||Probe.U16(pe,4)!=profile.Identity.Machine||Probe.U16(pe,24)!=0x20B||Probe.U32(pe,8)!=profile.Identity.PeTimestamp||Probe.U32(pe,80)!=profile.Identity.ImageSize)
                    throw new ProbeFailure("UNSUPPORTED_BUILD","載入 PE 身分不符已驗版型。");
                memory.EnableRoundMetadata();
                var scene=Scene(memory);
                var journal=new Journal(fields,scene.Director,scene.Scene,Emit,effective) { DispatchTargetOwnerId=dispatchTargetOwnerId };
                // Keep the start of the fresh identity read as a conservative
                // sample time; closing the source later must not renew its age.
                var identitySampledAt=DateTimeOffset.UtcNow;
                CharacterSourceFence.Bind(memory,journal,budget);
                if(reuseMetadata&&journal.SelfId is {} currentOwn)
                    memory.UseMetadataCache(metadataCache,new(candidate.Pid,candidate.Created,memory.Image,memory.ImageSize,profile.Sha256,profile.CacheIdentity,scene.Director,scene.Scene,currentOwn));
                bool groupsComplete=true;
                ShopActionSourceCandidate? shopActionCandidate=null;
                // Identity comes from CharacterSourceFence, independently of
                // actor discovery. Navigation supplies its own selected lookup.
                if(readNavigation is null)
                    CaptureGroup("自身座標",()=>SelfSpatialFieldReader.Run(memory,journal,budget),["position"]);
                CaptureGroup("HPMP",()=>HudProbe.Run(memory,journal,budget),["hp","mp"]);
                CaptureGroup("HUD 資訊",()=>{HudFieldsProbe.Run(memory,journal,budget);return 0;},["level","experiencePercent","weightPercent","mapName"]);
                foreach(string statKey in new[]{"foodPercent","armorClass","magicResistancePercent","alignment"})
                    CaptureGroup("角色屬性 "+statKey,()=>{SkillsStatsFieldReader.Capture(memory,journal,budget,new ReadSelection([statKey]));return 0;},[statKey]);
                CaptureGroup("BUFF",()=>{BuffFieldReader.Capture(memory,journal,budget,effective);return 0;},["buffIds"]);
                CaptureGroup("快捷欄",()=>{HotbarFieldReader.Capture(memory,journal,budget,effective);return 0;},["hotbarPage","hotbarFocus","hotbarSlots","atsEnabled"]);
                CaptureGroup("背包與裝備候選",()=>{InventoryFieldReader.Capture(memory,journal,budget,effective);return 0;},["inventory","equippedItems","weapon"]);
                CaptureGroup("自身名稱",()=>{SelfNameFieldReader.Capture(memory,journal,budget,effective);return 0;},["characterName"]);
                CaptureGroup("附近角色物件",()=>{ActorFieldsReader.Capture(memory,journal,budget,
                    new ReadSelection(effective.EnabledKeys.Where(k=>k is "actors" or "actorNames" or "actorPositions" or "nearbyMonsters" or "nearbyNpcs" or "nearbyPlayers" or "playerShopStates" or "nearbyUnknown")));return 0;},["actors","actorNames","actorPositions","nearbyMonsters","nearbyNpcs","nearbyPlayers","playerShopStates","nearbyUnknown"]);
                CaptureGroup("安全區",()=>{MapFieldReader.Capture(memory,journal,budget,effective);return 0;},["safeZone"]);
                CaptureGroup("地圖格候選",()=>{MapCursorExtraReader.Capture(memory,journal,budget,new ReadSelection(effective.EnabledKeys.Where(k=>k is "mapTile" or "mapNeighborhood")));return 0;},["mapTile","mapNeighborhood"]);
                CaptureGroup("游標原始狀態",()=>{MapCursorExtraReader.Capture(memory,journal,budget,new ReadSelection(effective.EnabledKeys.Where(k=>k=="cursorInputState")));return 0;},["cursorInputState"]);
                CaptureGroup("隊伍",()=>{PartyFieldReader.Capture(memory,journal,budget,effective);return 0;},["party"]);
                CaptureGroup("游標與畫面候選",()=>{ViewportFieldReader.Capture(memory,journal,budget,effective);return 0;},["cursorPosition","actorViewport"]);
                CaptureGroup("玩家商店目前頁面",()=>{shopActionCandidate=PersonalShopFieldReader.Capture(memory,journal,budget,itemStateContradictions,shopBindingOnly);return 0;},["personalShopListing"]);
                // Service-only selected-ID lookup. It does not publish a nearby
                // collection or refresh the manually retained target snapshot.
                readNavigation?.Invoke(memory,journal,budget);
                journal.SourceClosure?.Invoke();
                var closed=Scene(memory);
                if(scene!=closed)throw new ProbeFailure("GROUP_SOURCE_CHANGED","跨資料組的 Director／Scene 已變更。");
                memory.CloseRoundMetadata();
                Emit(new(DateTimeOffset.UtcNow,"ROUND_METADATA_CLOSURE","當次模組中繼資料首次A與唯一區間最終C閉合；heap及角色值不快取",memory.RoundMetadataMetrics));
                memory.Verify();
                if(!memory.Required(profile.Identity.PeOffset,0x108).AsSpan().SequenceEqual(pe))throw new ProbeFailure("GROUP_SOURCE_CHANGED","載入 PE 來源已變更。");
                var finalFile=new FileInfo(Probe.ExePath);
                if(finalFile.Length!=length||finalFile.LastWriteTimeUtc!=modified)throw new ProbeFailure("GROUP_SOURCE_CHANGED","磁碟 LC 在採樣中已變更。");
                budget.Check();
                if(reuseMetadata)
                {
                    var cacheMetrics=memory.MetadataMetrics;var cacheEvidence=memory.MetadataEvidence;bool committed=false;
                    // A rejected group rolls back its own new metadata below. Other
                    // closed groups and earlier unexpired entries remain useful.
                    // Native/source/metadata failures still invalidate at their origin.
                    committed=memory.CommitMetadataCache();
                    Emit(new(DateTimeOffset.UtcNow,"METADATA_CACHE","模組型別核對快取",new{MetadataMetrics=cacheMetrics,MetadataEvidence=cacheEvidence,Committed=committed,AllGroupsComplete=groupsComplete}));
                }
                string own=journal.SelfId?.ToString(CultureInfo.InvariantCulture)??"unknown";
                string session=$"{candidate.Pid}:{candidate.Created}:{memory.Image:X}:{scene.Director:X}:{scene.Scene:X}:{own}";
                var source=new SourceIdentity(candidate.Pid,candidate.Created,Probe.ExePath,Probe.Hex(memory.Image),profile.Identity.PeTimestamp,profile.Identity.ImageSize,profile.Sha256,session);
                journal.SourceCommit?.Invoke(source);
                // Publish only after the character/source closures, Scene,
                // process, PE and disk identity checks above all succeed.
                if(effective.Enabled("selfId")&&journal.SelfId is {} verifiedSelf)
                    journal.Set("selfId",verifiedSelf,identitySampledAt,
                        "CharacterSourceFence;CurrentTypesAndOwnershipClosed;ValueABStable;FinalSourceVerified");
                ObserveClosedShopRead(source,shopActionCandidate,fields,configurationFence??selected,profile,
                    journal.SelfId,identitySampledAt,length,modified,shopBindingOnly,effective.Enabled("personalShopListing"));
                if(observedShopActionSource is not null && shopActionCandidate is {} action && action.SourceSessionKey==session &&
                    fields.TryGetValue("personalShopListing",out var shopField) && shopField.SampledAt==action.SampledAt && shopField.Fresh(DateTimeOffset.UtcNow))
                {
                    budget.Check();
                    observedShopActionSource(new(source,action.SampledAt,action.PlayerShopSubsystem,action.CurrentOwnerIdRaw,
                        action.ServiceRoot,action.UISubsystem,action.Director,action.Scene) { BindingOnly=action.BindingOnly });
                }
                bool any=fields.Values.Any(f=>f.State=="Observed");
                Emit(new(DateTimeOffset.UtcNow,"READ_CYCLE","唯讀採樣完成",new{ElapsedMs=timer.Elapsed.TotalMilliseconds,memory.ReadCalls,memory.RequestedBytes,memory.ReadFailures,KnownFields=fields.Where(p=>p.Value.State=="Observed").Select(p=>p.Key).ToArray()}));
                return new(source,fields,any?"Reading":"Unknown",any?"資料已做結構與來源核對；畫面變化及未驗功能另行確認":"本輪尚無有效欄位",memory.ReadCalls,memory.RequestedBytes)
                    {RetryableFields=retryableFields.ToArray()};

                void CaptureGroup(string group,Func<int> capture,string[] keys)
                {
                    if(!keys.Any(effective.Enabled))return;
                    var groupTimer=Stopwatch.StartNew();var groupStarted=DateTimeOffset.UtcNow;
                    memory.Verify();long readsBefore=memory.ReadCalls,bytesBefore=memory.RequestedBytes;
                    int failuresBefore=memory.ReadFailures;double remainingBefore=budget.RemainingMilliseconds;
                    int groupCheckpoint=memory.MetadataCheckpoint();
                    journal.SampleAt=DateTimeOffset.UtcNow;
                    try{capture();}
                    catch(ProbeFailure error)when(!Fatal(error))
                    {
                        groupsComplete=false;
                        memory.RollbackMetadataCandidate(groupCheckpoint);
                        foreach(string field in keys.Where(effective.Enabled))fields[field]=FieldReading.Unknown(error.Code+"："+error.Message+NativeText(error.NativeError));
                        if(error.Code=="ACTOR_COLLECTION_CHANGED"&&error.NativeError==0)
                            foreach(string field in keys.Where(effective.Enabled))retryableFields.Add(field);
                        foreach(string field in keys.Where(effective.Enabled))
                            if(SelfSpatialDiscoveryRetry.RetryableField(group,field,error.Code,error.NativeError))
                                retryableFields.Add(field);
                        Emit(new(DateTimeOffset.UtcNow,"READ_GROUP_UNKNOWN",group,new{error.Code,error.Message,error.NativeError}));
                    }
                    finally{Emit(new(DateTimeOffset.UtcNow,"READ_GROUP",group,new{StartedAt=groupStarted,ElapsedMs=groupTimer.Elapsed.TotalMilliseconds,EnabledFields=keys.Where(effective.Enabled).ToArray(),ReadCalls=memory.ReadCalls-readsBefore,ReadBytes=memory.RequestedBytes-bytesBefore,ReadFailures=memory.ReadFailures-failuresBefore,RemainingBeforeMs=remainingBefore,RemainingAfterMs=budget.RemainingMilliseconds}));}
                }
            }
            catch(OperationCanceledException){InvalidateClosedShopRead();memory?.DiscardMetadataCache("採樣已取消");if(reuseMetadata)metadataCache.Invalidate("採樣已取消");throw;}
            catch(Exception error)
            {
                InvalidateClosedShopRead();
                memory?.DiscardMetadataCache("採樣失敗："+error.Message);
                if(reuseMetadata)metadataCache.Invalidate("採樣失敗："+error.Message);
                string code=error is ProbeFailure p?p.Code:error.GetType().Name;
                int native=error is ProbeFailure pf?pf.NativeError:error is Win32Exception w?w.NativeErrorCode:0;
                string state=code=="UNSUPPORTED_BUILD"?"Unsupported":"Unknown";
                Emit(new(DateTimeOffset.UtcNow,"READ_FAILED",error.Message,new{Code=code,NativeError=native,ElapsedMs=timer.Elapsed.TotalMilliseconds,RemainingMs=budget.RemainingMilliseconds,ReadCalls=memory?.ReadCalls,ReadBytes=memory?.RequestedBytes,ReadFailures=memory?.ReadFailures}));
                if(code is "GROUP_SOURCE_CHANGED" or "PROCESS_CHANGED" or "PROCESS_EXITED" or "PROCESS_PATH_CHANGED")hashKey=null;
                return new(null,Empty(selected,code+"："+error.Message+NativeText(native)),state,error.Message,memory?.ReadCalls??0,memory?.RequestedBytes??0)
                    {ReadFailureCode=code,ReadFailureNativeError=native};
            }
            finally
            {
                if(memory is not null&&memory.PageAccessObservations>0)
                {
                    // Protected code-page queries are expected and frequent. Keep
                    // their per-cycle counters without flooding the service LOG.
                    // Actual data-page skips or failed RPM calls retain the full
                    // bounded evidence set for diagnosis.
                    bool dataReadIssue=memory.PageAccessEvidence.Any(e=>e.Stage is
                        "PointedReadSkipped" or "ModuleReadSkipped" or "PointedReadFailed" or "ModuleReadFailed");
                    var evidence=dataReadIssue?memory.PageAccessEvidence:
                        memory.PageAccessEvidence.Take(4).ToArray();
                    Emit(new(DateTimeOffset.UtcNow,"PAGE_ACCESS_DIAGNOSTICS","本輪頁面保護觀察；各頁只保留首筆樣本，沒有修改保護",new{
                        memory.ProcessId,memory.CreationFileTime,memory.PageAccessObservations,Evidence=evidence,
                        CapturedEvidenceCount=memory.PageAccessEvidence.Count,DataReadIssue=dataReadIssue,
                        UniqueEvidenceLimit=64,memory.ReadFailures,ProtectionChangesByReader=0}));
                }
                memory?.Dispose();
            }
        }
    }
    private static bool Fatal(ProbeFailure p)=>p.Code is "CONFIGURATION_CHANGED" or "TIME_LIMIT" or "READ_BYTE_LIMIT" or "GROUP_SOURCE_CHANGED" or "PROCESS_CHANGED" or "PROCESS_EXITED" or "PROCESS_PATH_CHANGED" or "IDENTITY_API_FAILED" or "PID_CHANGED" or "HUD_TYPE_LINK" or "HUD_TYPE_NAME";
    private static string NativeText(int error)=>error==0?"":$"（Win32 {error}）";
    private static Dictionary<string,FieldReading> Empty(ReadSelection selected,string detail)
    {
        return selected.Empty(detail);
    }
    private void Emit(HubLog entry){try{log?.Invoke(entry);}catch{/* Consumer logging cannot replace the sampling outcome. */}}
    public void Dispose(){lock(gate){disposed=true;InvalidateClosedShopRead();hashKey=null;diskHash=null;}}
    private static (ulong Director,ulong Scene) Scene(ReadSession memory)
    {
        byte[] Read(ulong at,int count)=>memory.Pointed(at,count,out var bytes)?bytes:throw new ProbeFailure("GROUP_SOURCE_CHANGED","目前場景來源無法完整讀取。",memory.LastNativeError);
        ulong director=Probe.U64(memory.Required(memory.Rva(0x17E00D8),8),0);
        if(!Probe.Pointer(director)||Probe.U64(Read(director,8),0)!=memory.Address(0x121AC40))throw new ProbeFailure("GROUP_SOURCE_CHANGED","Director 精確型別不符。");
        ulong scene=Probe.U64(Read(director+0x150,8),0);
        if(!Probe.Pointer(scene)||Probe.U64(Read(scene,8),0)!=memory.Address(0x121D980))throw new ProbeFailure("GROUP_SOURCE_CHANGED","Scene 精確型別不符。");
        var owner=Read(scene+0x190,16);
        if(Probe.U64(owner,0)!=0||Probe.U64(owner,8)!=director)throw new ProbeFailure("GROUP_SOURCE_CHANGED","Scene 所屬關聯不符。");
        return(director,scene);
    }
}

internal static class Probe
{
    internal const string ExePath=@"C:\Program Files (x86)\NCSOFT\Lineage Classic\LC.exe";
    internal const int MaximumMilliseconds=2000,MaximumReadBytes=2*1024*1024,ChunkBytes=4096;
    internal const uint ImageSize=0x3E5C000,PeStamp=0x6A9FE464;
    internal const string NewSha256="8CD4542EF90D75A89F9CC53BC280F89BB5BEDABB52E3E879CD6B9262337AE666";
    internal static readonly (string Label,string Name,uint Rva)[] Types=
    [("Director",".?AVDirector@cocos2d@@",0x176A2A0),("Scene",".?AVScene@cocos2d@@",0x1660718),("Node",".?AVNode@cocos2d@@",0x1649050),("Text",".?AVText@ui@cocos2d@@",0x1660DC8),("Label",".?AVLabel@cocos2d@@",0x1660F88)];
    internal static string Hex(ulong v)=>$"0x{v:X}";
    internal static ushort U16(byte[] b,int at)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at,2));
    internal static uint U32(byte[] b,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at,4));
    internal static ulong U64(byte[] b,int at)=>BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at,8));
    internal static bool Pointer(ulong v)=>v>=0x10000&&v<=0x7FFFFFFFFFFFUL&&(v&7)==0;
}

internal sealed class Journal(Dictionary<string,FieldReading> fields,ulong director,ulong scene,Action<HubLog> log,ReadSelection selection)
{
    internal DateTimeOffset SampleAt{get;set;}
    // Set only by the service-only target dispatch overload.
    internal ulong? DispatchTargetOwnerId { get; init; }
    internal ulong? SelfId{get;private set;}
    internal void BindSelf(ulong id)
    {if(id is 0 or ulong.MaxValue||SelfId is {} prior&&prior!=id)throw new ProbeFailure("GROUP_SOURCE_CHANGED","各欄位的自身 ID 不一致。");SelfId=id;}
    internal Action? SourceClosure{get;set;}
    internal Action<SourceIdentity>? SourceCommit{get;set;}
    internal bool Enabled(string key)=>selection.Enabled(key);
    internal void Set<T>(string key,T value,DateTimeOffset at,string evidence="StructureValidated")
    {if(Enabled(key))fields[key]=FieldReading.Observed(value,at,evidence);}
    internal void Unknown(string key,string detail){if(Enabled(key))fields[key]=FieldReading.Unknown(detail);}
    internal void Unsupported(string key,string detail){if(Enabled(key))fields[key]=FieldReading.Unsupported(detail);}
    internal void RequireScene(ulong d,ulong s){if(d!=director||s!=scene)throw new ProbeFailure("GROUP_SOURCE_CHANGED","各資料組場景不符。");}
    internal void Write(string phase,object data)
    {
        var json=JsonSerializer.SerializeToElement(data);
        if(phase is "HUD_VITAL_SAMPLE" or "SERVICE_SELF_SAMPLE")CheckScene(json.GetProperty("Director").GetString(),json.GetProperty("Scene").GetString());
        if(phase=="HUD_INFORMATION_SAMPLE")CheckScene(json.GetProperty("SourceDirector").GetString(),json.GetProperty("SourceScene").GetString());
        if(phase=="HUD_VITAL_SAMPLE")
        {
            var at=json.GetProperty("StartedAt").GetDateTimeOffset();
            foreach(string key in new[]{"Hp","Mp"})
            {
                if(!Enabled(key.ToLowerInvariant()))continue;
                var v=json.GetProperty(key);
                Set(key.ToLowerInvariant(),new{Current=v.GetProperty("Current").GetInt32(),Maximum=v.GetProperty("Maximum").GetInt32()},at);
            }
        }
        else if(phase=="SERVICE_SELF_SAMPLE")
        {
            ulong ownId=json.GetProperty("OwnId").GetUInt64();BindSelf(ownId);
            if(Enabled("position"))Set("position",new{X=json.GetProperty("X").GetInt32(),Y=json.GetProperty("Y").GetInt32()},SampleAt);
        }
        else if(phase=="HUD_INFORMATION_SAMPLE")
        {
            string key=Key(json.GetProperty("Key").GetString()!);string value=json.GetProperty("Value").GetString()!;
            var at=json.GetProperty("StartedAt").GetDateTimeOffset();
            if(key=="mapName")fields[key]=FieldReading.Observed(value,at);
            else if(key=="level"&&int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int level)&&level is >0 and <=1000)fields[key]=FieldReading.Observed(level,at);
            else if(key!="level"&&double.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double number)&&double.IsFinite(number)&&number is >=0 and <=100)fields[key]=FieldReading.Observed(number,at);
            else fields[key]=FieldReading.Unknown("資訊值超出語義範圍");
        }
        else if(phase=="HUD_INFORMATION_REJECTED")Unknown(Key(json.GetProperty("Key").GetString()!),json.GetProperty("Code").GetString()+"："+json.GetProperty("Reason").GetString());
        if(phase is "ITEM_KIND_DATA_PUBLISHED" or "ITEM_KIND_CURRENT_CODE_PROOF" or "HUD_VITAL_SAMPLE" or "SERVICE_SELF_SAMPLE" or "HUD_INFORMATION_SAMPLE" or "HUD_INFORMATION_REJECTED" or "HUD_NODE_REJECTED" or "MAP_CURRENT_TILE_SAMPLE" or "PARTY_CURRENT_SAMPLE" or "INVENTORY_ITEM_SAMPLE" or "INVENTORY_CURRENT_SAMPLE" or
            "ACTOR_CAPTURE_COST" or "ACTOR_PROOF_CHANGED" or "ACTOR_MEMBERSHIP_EARLY_CHANGED" or "ACTOR_COLLECTION_RETRY" or "ACTOR_COLLECTION_RECOVERED" or "ACTOR_COLLECTION_RETRY_SKIPPED" or "ACTOR_COLLECTION_RETRY_EXHAUSTED" or "ACTOR_VECTOR_LAYOUT" or "SOURCE_MEMBERSHIP_RECOVERED" or "NEARBY_COLLECTION_SAMPLE" or
            "SELF_SPATIAL_DISCOVERY_RETRY" or "SELF_SPATIAL_DISCOVERY_RECOVERED" or "SELF_SPATIAL_DISCOVERY_FAILED")log(new(DateTimeOffset.UtcNow,phase,"唯讀資料組證據",json));
    }
    private void CheckScene(string? d,string? s)
    {if(d!=Probe.Hex(director)||s!=Probe.Hex(scene))throw new ProbeFailure("GROUP_SOURCE_CHANGED","各資料組來自不同場景，本輪全部捨棄。");}
    private static string Key(string key)=>key switch{"Level"=>"level","Experience"=>"experiencePercent","Weight"=>"weightPercent","MapName"=>"mapName",_=>throw new InvalidOperationException("Unknown field")};
}
