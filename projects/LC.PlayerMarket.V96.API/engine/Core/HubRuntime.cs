using System.Diagnostics;
using System.Text.Json;
namespace MemoryHub;

public sealed partial class HubRuntime : IAsyncDisposable
{
    private readonly IMemorySource source;private readonly string directory,logDirectory;private readonly string instance=Guid.NewGuid().ToString("N");
    private readonly object logGate=new(),lifecycle=new(),selectionGate=new();
    private ReadSelection selection=new();private long configurationVersion;
    private BufferedDiagnosticLog? verboseLog;
    private StreamWriter? logWriter;private bool logging=true,disposed;private string? logError;
    private HubSnapshot latest=new();private CancellationTokenSource? stop;private Task? sampler,shutdown,disposal;private HubServer? server;
    private int interval=250;private long sequence,cycles,totalReads,totalBytes;
    public string PipeName {get;}
    public IReadOnlyList<string> CompatibilityPipeNames {get;}
    public bool TransportEnabled {get;}
    public bool IsRunning {get{lock(lifecycle)return sampler is not null&&stop?.IsCancellationRequested!=true;}}
    public ReadSelection ReadSelection=>Volatile.Read(ref selection);
    public bool LogEnabled {get {lock(logGate)return logging;}}
    public int IntervalMilliseconds {get=>Volatile.Read(ref interval);set{Volatile.Write(ref interval,Math.Clamp(value,100,5000));WakeScheduler();}}
    public event Action<HubSnapshot>? Changed;
    public HubRuntime(IMemorySource source,string? stateDirectory=null,string? pipeName=null,IEnumerable<string>? compatibilityPipeNames=null,bool enableTransport=true,string? logDirectory=null)
    {
        this.source=source;PipeName=pipeName??HubProtocol.PipeName;TransportEnabled=enableTransport;
        CompatibilityPipeNames=Array.AsReadOnly((compatibilityPipeNames??(pipeName is null?HubProtocol.CompatibilityPipeNames:Array.Empty<string>())).ToArray());
        directory=stateDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LCMemoryHub","v32");
        this.logDirectory=logDirectory is null?directory:Path.GetFullPath(logDirectory);
        try {var settings=JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(Path.Combine(directory,"settings.json")));if(settings is not null){RestoreSelectionSettings(settings);logging=settings.LogEnabled;IntervalMilliseconds=settings.IntervalMilliseconds;backgroundIntervals=NormalizeBackgroundIntervals(settings.BackgroundIntervalMilliseconds);}}
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        if(source is ISelectableMemorySource selectable)selectable.Configure(selection);
        latest=new(){Fields=MarkManualShopFields(selection.Empty("等待新採樣"))};
    }
    public HubSnapshot Snapshot
    {
        get {var raw=Volatile.Read(ref latest);lock(logGate)return raw with {Fields=new(raw.Fields),ConnectedClients=server?.ClientCount??0,LogEnabled=logging,LogError=logError};}
    }
    public void SetLogging(bool enabled)
    {
        lock(logGate)
        {
            if(!enabled&&logging)WriteLogLocked("LOG_DISABLED","使用者關閉 LOG",null);
            logging=enabled;if(!enabled){logWriter?.Dispose();logWriter=null;}
            else WriteLogLocked("LOG_ENABLED","使用者開啟 LOG",null);
            SaveSettingsLocked();
        }
        server?.SignalChanged();
    }
    public void SaveSettings() {lock(logGate)SaveSettingsLocked();}
    public void SetFieldEnabled(string key,bool enabled)
    {
        lock(selectionGate){RequireCustomSelection();ApplySelection(selection.With(key,enabled));}
    }
    public void SetAllFieldsEnabled(bool enabled)
    {
        lock(selectionGate){RequireCustomSelection();ApplySelection(new(enabled?FieldCatalog.All.Select(f=>f.Key):Array.Empty<string>()));}
    }
    private void ApplySelection(ReadSelection next,SelectionSettingsState? settingsState=null,bool force=false)
    {
        if(!force&&selection.EnabledKeys.SequenceEqual(next.EnabledKeys))return;
        // Configure once while the generation fence is held. If it rejects the
        // configuration, runtime selection, mode and prior settings stay intact.
        if(source is ISelectableMemorySource selectable)selectable.Configure(next);
        Volatile.Write(ref selection,next);configurationVersion++;
        Volatile.Write(ref selectionSettings,settingsState??new(false,next,null));
        var previous=Volatile.Read(ref latest);
        Volatile.Write(ref latest,previous with {ReadConfigurationVersion=configurationVersion,
            Fields=MarkManualShopFields(next.Empty("讀取選項已變更，等待新採樣")),Source=null,
            State=next.Any?"Waiting":"Disabled",Detail=next.Any?"讀取選項已變更，等待新採樣":"所有欄位已停用，未讀取 LC"});
        server?.SignalChanged();WakeScheduler();
        Log("FIELD_SELECTION","讀取欄位設定已更新",new{ConfigurationVersion=configurationVersion,EnabledFields=next.EnabledKeys});
        if(settingsState is not null)Log("SHOP_SEARCH_ONLY_MODE",settingsState.ShopSearchOnly?"商店搜尋專用模式已啟用":"商店搜尋專用模式已關閉，已還原原本勾選",
            new{Enabled=settingsState.ShopSearchOnly,ConfigurationVersion=configurationVersion,EnabledFields=next.EnabledKeys,PreviousEnabledFields=settingsState.PreviousSelection?.EnabledKeys});
        SaveSettings();
    }
    private void SaveSettingsLocked()
    {
        try {var saved=Volatile.Read(ref selectionSettings);Directory.CreateDirectory(directory);string temp=Path.Combine(directory,"settings."+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllText(temp,JsonSerializer.Serialize(new HubSettings(logging,interval,saved.Selection.EnabledKeys.ToArray(),new Dictionary<string,int>(Volatile.Read(ref backgroundIntervals),StringComparer.Ordinal),saved.ShopSearchOnly,saved.PreviousSelection?.EnabledKeys.ToArray())));File.Move(temp,Path.Combine(directory,"settings.json"),true);}
        catch(Exception ex) {logError="設定儲存失敗："+ex.Message;}
    }
    private void WriteLogLocked(string kind,string message,object? data)
    {
        if(!logging)return;
        try
        {
            Directory.CreateDirectory(logDirectory);
            if(logWriter is null||logWriter.BaseStream.Position>10*1024*1024)
            {logWriter?.Dispose();logWriter=new StreamWriter(new FileStream(Path.Combine(logDirectory,$"hub-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};}
            logWriter.WriteLine(JsonSerializer.Serialize(new HubLog(DateTimeOffset.UtcNow,kind,message,data),HubProtocol.Json));logError=null;
        }
        catch(Exception ex){logError="LOG 儲存失敗："+ex.Message;try{logWriter?.Dispose();}catch{}logWriter=null;}
    }
    private void Log(string kind,string message,object? data=null)
    {
        if (BufferedDiagnosticLog.OptionalKinds.Contains(kind))
        {
            BufferedDiagnosticLog? sink;
            lock(logGate)
            {
                if(!logging)return;
                sink=verboseLog??=new BufferedDiagnosticLog(logDirectory,error=>Volatile.Write(ref logError,error));
            }
            try { sink.Append(new HubLog(DateTimeOffset.UtcNow,kind,message,data)); }
            catch(Exception error){Volatile.Write(ref logError,"非關鍵 LOG 緩衝失敗："+error.Message);}
            return;
        }
        lock(logGate)WriteLogLocked(kind,message,data);
    }
    public void RecordLog(HubLog entry) => Log(entry.Kind,entry.Message,entry.Data);
    public void Start()
    {
        lock(lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(sampler is not null)throw new InvalidOperationException("服務已啟動");
            if(shutdown?.IsCompleted==false)throw new InvalidOperationException("讀取服務仍在停止，請稍候。");
            PrepareTransportLocked();
            shutdown=null;stop=new CancellationTokenSource();sampler=Task.Run(()=>LoopAsync(stop.Token));Log("START",TransportEnabled?"記憶體讀取服務啟動":"程式內記憶體讀取啟動",new{Version=HubProtocol.Version,TransportEnabled,PipeName=TransportEnabled?PipeName:null,CompatibilityPipeNames=TransportEnabled?CompatibilityPipeNames:Array.Empty<string>()});
        }
    }
    // Reserve all data endpoints before a service enables its shop commands.
    // This publishes only the waiting snapshot; no memory sampler is started.
    public void PrepareTransport()
    {
        lock(lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(shutdown?.IsCompleted==false)throw new InvalidOperationException("讀取服務仍在停止，請稍候。");
            PrepareTransportLocked();
        }
    }
    private void PrepareTransportLocked()
    {
        if(!TransportEnabled||server is not null)return;
        server=new HubServer(PipeName,()=>Snapshot,(k,m)=>Log(k,m),CompatibilityPipeNames);
        try {server.Start();shutdown=null;}
        catch(Exception ex)
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();server=null;
            throw new InvalidOperationException("無法取得新舊資料介面；可能已有讀取服務在執行。請先關閉前一版服務。",ex);
        }
    }
    private async Task LoopAsync(CancellationToken token)
    {
        if(source is ISlicedMemorySource sliced){await LoopSlicedAsync(sliced,token).ConfigureAwait(false);return;}
        while(!token.IsCancellationRequested)
        {
            var elapsed=Stopwatch.StartNew();ReadResult result;ReadSelection selected;long generation;
            lock(selectionGate){selected=selection;generation=configurationVersion;}
            try {result=selected.Any?source.Read(token):new(null,selected.Empty("所有欄位已停用"),"Disabled","所有欄位已停用，未讀取 LC");if(result is null||result.Fields is null||result.Fields.Values.Any(f=>f is null))throw new InvalidDataException("讀取來源回傳不合法的資料，已清除此輪");}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception ex){result=new(null,[],"Error",ex.Message);Log("READ_EXCEPTION",ex.Message,new{Type=ex.GetType().FullName});}
            if(token.IsCancellationRequested)break;
            HubSnapshot current;
            lock(selectionGate)
            {
                if(selected.Any)cycles++;totalReads+=result.Reads;totalBytes+=result.ReadBytes;
                bool changed=generation!=configurationVersion;
                current=new HubSnapshot{InstanceId=instance,Sequence=++sequence,ReadConfigurationVersion=configurationVersion,PublishedAt=DateTimeOffset.UtcNow,
                    Source=changed?null:result.Source,Fields=changed?selection.Empty("舊設定採樣已捨棄，等待新採樣"):selected.Filter(result.Fields),
                    State=changed?(selection.Any?"Waiting":"Disabled"):result.State,Detail=changed?"本輪設定已變更，舊採樣未發布":result.Detail,
                    ReadCycles=cycles,MemoryReads=totalReads,MemoryBytes=totalBytes};
                Volatile.Write(ref latest,current);
            }
            server?.SignalChanged();
            Log("SNAPSHOT",current.Detail,new{Snapshot=current,ReadDurationMs=elapsed.Elapsed.TotalMilliseconds});
            try{Changed?.Invoke(Snapshot);}catch(Exception ex){Log("SUBSCRIBER_ERROR",ex.Message);}
            int delay=Math.Max(10,IntervalMilliseconds-(int)elapsed.ElapsedMilliseconds);
            try {await Task.Delay(delay,token);}catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
    public Task StopAsync()
    {
        lock(lifecycle)
        {
            if(shutdown is not null)return shutdown;
            if(sampler is null&&server is null)return Task.CompletedTask;
            stop?.Cancel();shutdown=StopCoreAsync(sampler??Task.CompletedTask,server);return shutdown;
        }
    }
    private async Task StopCoreAsync(Task worker,HubServer? transport)
    {
        try
        {
            await worker.ConfigureAwait(false);
            lock(selectionGate)Volatile.Write(ref latest,new HubSnapshot{InstanceId=instance,Sequence=++sequence,ReadConfigurationVersion=configurationVersion,Fields=selection.Empty("讀取服務已停止"),State="Stopped",PublishedAt=DateTimeOffset.UtcNow,Detail="讀取服務已停止",ReadCycles=cycles,MemoryReads=totalReads,MemoryBytes=totalBytes});
            server?.SignalChanged();
        }
        finally
        {
            if(transport is not null)await transport.DisposeAsync().ConfigureAwait(false);
            lock(lifecycle){sampler=null;server=null;stop?.Dispose();stop=null;}
            Log("STOP","讀取服務已停止");SaveSettings();
        }
    }
    public ValueTask DisposeAsync()
    {
        lock(lifecycle){disposed=true;disposal??=DisposeCoreAsync();return new(disposal);}
    }
    private async Task DisposeCoreAsync()
    {
        try {await StopAsync().ConfigureAwait(false);}
        finally
        {
            try {source.Dispose();}
            finally
            {
                BufferedDiagnosticLog? optional;
                lock(logGate){optional=verboseLog;verboseLog=null;logWriter?.Dispose();logWriter=null;}
                optional?.Dispose();
            }
        }
    }
    private sealed record HubSettings(bool LogEnabled,int IntervalMilliseconds,string[]? EnabledFields=null,Dictionary<string,int>? BackgroundIntervalMilliseconds=null,bool ShopSearchOnly=false,string[]? PreviousEnabledFields=null);
}
