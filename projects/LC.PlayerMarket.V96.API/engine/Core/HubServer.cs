using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
namespace MemoryHub;

internal sealed class HubServer : IAsyncDisposable
{
    private readonly string[] names;
    private readonly Func<HubSnapshot> snapshot;
    private readonly Action<string,string> log;
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<int,Task> clients=[];
    private readonly SemaphoreSlim capacity=new(8,8);
    private readonly object changeGate=new();
    private readonly object lifecycle=new();
    private readonly Dictionary<string,NamedPipeServerStream> listeners=new(StringComparer.Ordinal);
    private long changeVersion;
    private TaskCompletionSource<long> changed=NewSignal();
    private Task? accept,disposal;private bool started;private int active,nextId;
    public int ClientCount=>Volatile.Read(ref active);
    internal HubServer(string name,Func<HubSnapshot> snapshot,Action<string,string> log,IEnumerable<string>? aliases=null)
    {
        names=new[]{name}.Concat(aliases??[]).ToArray();
        if(names.Length<1||names.Length>HubProtocol.MaximumPipeNames||names.Any(string.IsNullOrWhiteSpace)||names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length)
            throw new ArgumentException("資料管道名稱重複或超過相容範圍。",nameof(aliases));
        this.snapshot=snapshot;this.log=log;
    }
    private static TaskCompletionSource<long> NewSignal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Every client tracks its own observed version. Signals only wake clients;
    // the immutable current snapshot is captured when that client can write.
    public void SignalChanged()
    {
        TaskCompletionSource<long> previous;long version;
        lock(changeGate){version=++changeVersion;previous=changed;changed=NewSignal();}
        previous.TrySetResult(version);
    }
    private long CurrentVersion {get{lock(changeGate)return changeVersion;}}
    private async Task WaitForChangeAsync(long observed,CancellationToken token)
    {
        Task<long> notification;
        lock(changeGate)
        {
            // The version check and signal capture must be atomic: a publish
            // between snapshot capture and this wait must never be lost.
            if(changeVersion!=observed)return;
            notification=changed.Task;
        }
        // Keep the existing 200 ms heartbeat for connection and stale-state
        // compatibility; a new snapshot wakes the client immediately.
        try{await notification.WaitAsync(TimeSpan.FromMilliseconds(200),token).ConfigureAwait(false);}
        catch(TimeoutException) { }
    }
    private NamedPipeServerStream Create(string name,bool first)
    {
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,PipeAccessRights.FullControl,AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name,PipeDirection.Out,9,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|(first?PipeOptions.FirstPipeInstance:PipeOptions.None),0,65536,security);
    }
    public void Start()
    {
        lock(lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposal is not null,this);
            if(started)throw new InvalidOperationException("資料管道已啟動。");
            // Reserve both versioned names before accepting any client.
            try {foreach(string name in names)listeners.Add(name,Create(name,true));}
            catch {foreach(var pipe in listeners.Values)pipe.Dispose();listeners.Clear();throw;}
            started=true;
            accept=Task.WhenAll(listeners.Select(pair=>Task.Run(()=>AcceptAsync(pair.Key,pair.Value))).ToArray());
        }
    }
    private async Task AcceptAsync(string name,NamedPipeServerStream first)
    {
        NamedPipeServerStream? pending=first;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                // Idle compatibility listeners do not consume active client quota.
                await pending!.WaitForConnectionAsync(stop.Token);
                await capacity.WaitAsync(stop.Token);bool transferred=false;
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var connected=pending;
                    // Always keep one named instance before a client may close,
                    // including an idle compatibility endpoint.
                    try {lock(lifecycle){stop.Token.ThrowIfCancellationRequested();pending=Create(name,false);listeners[name]=pending;}}
                    catch {connected.Dispose();throw;}
                    int id=Interlocked.Increment(ref nextId);
                    Interlocked.Increment(ref active);log("CLIENT_CONNECTED",$"連線 {id}");
                    var task=PumpAsync(connected,id);clients[id]=task;
                    _=task.ContinueWith(_=>clients.TryRemove(id,out var ignored),TaskScheduler.Default);
                    transferred=true;
                }
                finally {if(!transferred)capacity.Release();}
            }
        }
        catch(Exception ex) when(stop.IsCancellationRequested&&(ex is OperationCanceledException or ObjectDisposedException or IOException)) { }
        catch(Exception ex) {log("PIPE_ERROR",ex.Message);stop.Cancel();}
        finally {pending?.Dispose();lock(lifecycle){if(listeners.GetValueOrDefault(name)==pending)listeners.Remove(name);}}
    }
    private async Task PumpAsync(NamedPipeServerStream pipe,int id)
    {
        using(pipe)
        try
        {
            while(!stop.IsCancellationRequested)
            {
                long observed=CurrentVersion;
                var current=snapshot().AtTime(DateTimeOffset.UtcNow);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(2500);
                await HubWire.WriteAsync(pipe,current,deadline.Token);
                await WaitForChangeAsync(observed,stop.Token).ConfigureAwait(false);
            }
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException)
        {if(!stop.IsCancellationRequested)log("CLIENT_DISCONNECTED",$"連線 {id}：{ex.Message}");}
        finally {Interlocked.Decrement(ref active);capacity.Release();}
    }
    public ValueTask DisposeAsync()
    {lock(lifecycle){disposal??=Task.Run(DisposeCoreAsync);return new(disposal);}}
    private async Task DisposeCoreAsync()
    {
        stop.Cancel();NamedPipeServerStream[] pending;Task? accepting;
        lock(lifecycle){pending=listeners.Values.ToArray();listeners.Clear();accepting=accept;}
        foreach(var pipe in pending)pipe.Dispose();
        if(accepting is not null)await accepting.ConfigureAwait(false);
        await Task.WhenAll(clients.Values.ToArray());stop.Dispose();capacity.Dispose();
    }
}
