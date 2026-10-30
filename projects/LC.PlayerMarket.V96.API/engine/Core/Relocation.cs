namespace MemoryHub;

public sealed record RelocationProgress(string Stage,string Message,int Percent);
public sealed record RelocationField(string Key,FieldReading Reading,bool CanConfirm,string Detail);
public sealed record RelocationCandidate(string Id,DateTimeOffset CapturedAt,DateTimeOffset ExpiresAt,SourceIdentity Source,IReadOnlyList<RelocationField> Fields,string Detail);
public sealed record RelocationApplyResult(string ProfileId,string Path,IReadOnlyList<string> ConfirmedFields,string Detail);
public interface IRelocationSource
{
    Task<RelocationCandidate> DiscoverAsync(ReadSelection selection,IProgress<RelocationProgress>? progress,CancellationToken token);
    Task<RelocationApplyResult> ApplyAsync(string candidateId,IReadOnlyCollection<string> confirmedFields,CancellationToken token);
    void DiscardCandidate(string candidateId);
}

/// <summary>Owns the stopped-service interval while a candidate is being reviewed.</summary>
public sealed class RelocationController(HubRuntime runtime,IRelocationSource source):IAsyncDisposable
{
    private readonly SemaphoreSlim serial=new(1,1);
    private readonly object state=new();
    private CancellationTokenSource? operation;
    private bool busy,disposed,restoreRunning;
    private RelocationCandidate? candidate;
    private string[] selection=[];
    public bool Busy {get{lock(state)return busy;}}
    public RelocationCandidate? Candidate {get{lock(state)return candidate;}}
    public event Action<RelocationProgress>? Progress;
    private sealed class Forward(Action<RelocationProgress> send):IProgress<RelocationProgress>{public void Report(RelocationProgress value)=>send(value);}
    private void Emit(RelocationProgress value)
    {runtime.RecordLog(new(DateTimeOffset.UtcNow,"RELOCATION_PROGRESS",value.Message,new{value.Stage,value.Percent}));try{Progress?.Invoke(value);}catch{}}
    public async Task<RelocationCandidate> BeginAsync(CancellationToken token=default)
    {
        if(!await serial.WaitAsync(0,token))throw new InvalidOperationException("已有重新定位工作進行中");
        try
        {
            lock(state)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                if(candidate is not null)throw new InvalidOperationException("請先取消或保存目前候選結果");
                busy=true;operation=CancellationTokenSource.CreateLinkedTokenSource(token);
                restoreRunning=runtime.IsRunning;selection=runtime.ReadSelection.EnabledKeys.ToArray();
            }
            try
            {
                Emit(new("Stopping","停止採樣後開始唯讀重新定位",0));
                await runtime.StopAsync();operation.Token.ThrowIfCancellationRequested();
                // Discovery has its own complete field scope. Runtime switches are
                // preserved for monitoring and never restrict relocation evidence.
                var found=await source.DiscoverAsync(new ReadSelection(),new Forward(Emit),operation.Token);
                lock(state)candidate=found;
                operation.Token.ThrowIfCancellationRequested();
                Emit(new("Review","候選資料已準備，請比對後勾選需要保存的欄位",100));
                return found;
            }
            catch {ClearCandidate();Restore();throw;}
            finally {lock(state){busy=false;operation?.Dispose();operation=null;}}
        }
        finally{serial.Release();}
    }
    public async Task<RelocationApplyResult> ApplyAsync(IReadOnlyCollection<string> confirmed,CancellationToken token=default)
    {
        if(!await serial.WaitAsync(0,token))throw new InvalidOperationException("已有重新定位工作進行中");
        try
        {
            RelocationCandidate current;
            lock(state)
            {
                ObjectDisposedException.ThrowIf(disposed,this);current=candidate??throw new InvalidOperationException("尚無可保存候選");
                if(confirmed.Count==0||confirmed.Distinct(StringComparer.Ordinal).Count()!=confirmed.Count||confirmed.Any(k=>!current.Fields.Any(f=>f.Key==k&&f.CanConfirm&&f.Reading.State=="Observed"&&f.Reading.Value is not null&&f.Reading.SampledAt is not null)))
                    throw new InvalidOperationException("請只選取已成功讀得且已比對的欄位");
                if(DateTimeOffset.UtcNow>current.ExpiresAt)throw new InvalidOperationException("候選結果已到期，請取消後重新定位");
                busy=true;operation=CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            try
            {
                Emit(new("Validating","保存前重新核對目前來源與所選欄位",95));
                var result=await source.ApplyAsync(current.Id,confirmed,operation.Token);
                ClearCandidate();string? restoreError=Restore();
                if(restoreError is not null)result=result with {Detail=result.Detail+"；服務恢復失敗："+restoreError};
                Emit(new("Applied",result.Detail,100));return result;
            }
            catch {ClearCandidate();Restore();throw;}
            finally {lock(state){busy=false;operation?.Dispose();operation=null;}}
        }
        finally{serial.Release();}
    }
    public async Task CancelAsync()
    {
        lock(state)operation?.Cancel();
        await serial.WaitAsync();
        try{ClearCandidate();Restore();Emit(new("Cancelled","定位工作已結束；尚未套用的候選已作廢",0));}
        finally{serial.Release();}
    }
    private void ClearCandidate()
    {
        RelocationCandidate? old;lock(state){old=candidate;candidate=null;}
        if(old is not null)source.DiscardCandidate(old.Id);
    }
    private string? Restore()
    {
        try
        {
            lock(state)
            {
                bool start=restoreRunning&&!disposed;restoreRunning=false;
                if(start&&!runtime.IsRunning)runtime.Start();
            }
            return null;
        }
        catch(Exception error){Emit(new("ResumeFailed","版本處理已結束，但服務恢復失敗："+error.Message,100));return error.Message;}
    }
    public async ValueTask DisposeAsync()
    {
        lock(state){disposed=true;operation?.Cancel();}
        await serial.WaitAsync();try{ClearCandidate();lock(state)restoreRunning=false;}finally{serial.Release();}
    }
}
