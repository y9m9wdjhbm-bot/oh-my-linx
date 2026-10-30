using System.Diagnostics;

namespace MemoryHub;

/// <summary>
/// A slice is an independent, closed read transaction. Configure continues to
/// receive the complete user selection and is never called for scheduler slices.
/// </summary>
public interface ISlicedMemorySource : ISelectableMemorySource
{
    ReadResult ReadSlice(ReadSelection fields,CancellationToken token);
}

internal sealed class ScheduledReadSlice(string name,ReadSelection fields,bool priority)
{
    public string Name {get;}=name;
    public ReadSelection Fields {get;}=fields;
    public bool Priority {get;}=priority;
    public double NextDue {get;set;}
    public double? LastStarted {get;set;}
    public double? LastCompleted {get;set;}
    public double? RetryAt {get;set;}
    public int ConsecutiveTransientFailures {get;set;}
}

internal static class PriorityReadSchedule
{
    internal static ScheduledReadSlice? PickDue(IReadOnlyList<ScheduledReadSlice> schedule,double now,bool previousBackgroundWasActors)
    {
        var priority=schedule.FirstOrDefault(s=>s.Priority&&s.NextDue<=now);
        if(priority is not null)return priority;
        var due=schedule.Where(s=>!s.Priority&&s.NextDue<=now).OrderBy(s=>s.NextDue).ToArray();
        var actors=due.FirstOrDefault(s=>s.Name=="actors");
        // Keep actor consumers within their short freshness window. If a very
        // short actor interval stays overdue, let another background group run.
        if(actors is not null&&(!previousBackgroundWasActors||due.Length==1))return actors;
        return previousBackgroundWasActors?due.FirstOrDefault(s=>s.Name!="actors")??actors:due.FirstOrDefault();
    }

    internal static bool ScheduleRecovery(ScheduledReadSlice slice,ReadResult result,double completed)
    {
        bool transient=slice.Name=="actors"&&result.Fields.Any(p=>slice.Fields.Enabled(p.Key)&&TransientCollectionRecovery.Eligible(p.Key,result,p.Value));
        slice.ConsecutiveTransientFailures=transient?Math.Min(3,slice.ConsecutiveTransientFailures+1):0;
        // At most two prompt follow-up transactions; no unbounded retry storm.
        slice.RetryAt=transient&&slice.ConsecutiveTransientFailures<=2?completed+50:null;
        return slice.RetryAt is not null;
    }

    public static List<ScheduledReadSlice> Create(ReadSelection selected,bool shopSearchOnly=false)
    {
        var schedule=new List<ScheduledReadSlice>();
        var assigned=new HashSet<string>(StringComparer.Ordinal);
        Add("priority",true,"hp","mp","position","selfId");
        Add("hud",false,"level","experiencePercent","weightPercent","foodPercent","armorClass","magicResistancePercent","alignment","mapName");
        Add("buff",false,"buffIds");
        Add("hotbar",false,"hotbarPage","hotbarFocus","hotbarSlots","atsEnabled");
        Add("inventory",false,"inventory","equippedItems","weapon");
        Add("actors",false,"actors","characterName","actorNames","actorPositions","nearbyMonsters","nearbyNpcs","nearbyPlayers","nearbyUnknown","playerShopStates");
        Add("safeZone",false,"safeZone","mapTile","mapNeighborhood");
        Add("party",false,"party");
        Add("viewport",false,"actorViewport","cursorPosition","cursorInputState");
        Add("personalShop",false,"personalShopListing");
        // Remaining catalog keys form one slice. In particular atsEnabled is
        // already owned by hotbar and must not be scheduled a second time.
        Add("unsupported",false,FieldCatalog.All.Select(f=>f.Key).ToArray());
        return schedule;

        void Add(string name,bool priority,params string[] keys)
        {
            var enabled=keys.Where(key=>selected.Enabled(key)&&(!shopSearchOnly||!ShopSearchReadMode.ManualFields.Contains(key))&&assigned.Add(key)).ToArray();
            if(enabled.Length>0)schedule.Add(new(name,new ReadSelection(enabled),priority));
        }
    }
}

public sealed partial class HubRuntime
{
    private readonly SemaphoreSlim schedulerWake=new(0,1);

    private void WakeScheduler()
    {
        try{schedulerWake.Release();}catch(SemaphoreFullException) { }
    }

    private async Task WaitSchedulerAsync(double milliseconds,CancellationToken token)
    {
        await schedulerWake.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1,milliseconds)),token).ConfigureAwait(false);
    }

    private async Task LoopSlicedAsync(ISlicedMemorySource sliced,CancellationToken token)
    {
        var clock=Stopwatch.StartNew();
        long scheduledGeneration=-1;
        var schedule=new List<ScheduledReadSlice>();
        bool previousBackgroundWasActors=false;
        while(!token.IsCancellationRequested)
        {
            ReadSelection selected;long generation;bool shopSearchOnly;
            lock(selectionGate){selected=selection;generation=configurationVersion;shopSearchOnly=ShopSearchOnlyEnabled;}
            if(generation!=scheduledGeneration)
            {
                schedule=PriorityReadSchedule.Create(selected,shopSearchOnly);scheduledGeneration=generation;previousBackgroundWasActors=false;
                double firstDue=clock.Elapsed.TotalMilliseconds;
                foreach(var pending in schedule)pending.NextDue=firstDue;
            }
            if(!selected.Any)
            {
                var current=PublishSliceResult(new(null,selected.Empty("所有欄位已停用"),"Disabled","所有欄位已停用，未讀取 LC"),selected,selected,generation,false);
                SignalSlicePublished();
                Log("SNAPSHOT",current.Detail,new{Snapshot=current,ReadDurationMs=0});
                try{await WaitSchedulerAsync(IntervalMilliseconds,token).ConfigureAwait(false);}
                catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                continue;
            }

            double now=clock.Elapsed.TotalMilliseconds;
            var priority=schedule.FirstOrDefault(s=>s.Priority);
            var configuredBackgroundIntervals=Volatile.Read(ref backgroundIntervals);
            // Recalculate from the actual previous start so changing the UI
            // interval takes effect without assigning a shorter hidden period.
            if(priority?.LastStarted is {} lastStart)priority.NextDue=Math.Max(lastStart+IntervalMilliseconds,(priority.LastCompleted??lastStart)+10);
            foreach(var pending in schedule.Where(s=>!s.Priority))
                if(pending.LastStarted is {} backgroundStart)
                    pending.NextDue=pending.RetryAt??backgroundStart+configuredBackgroundIntervals[pending.Name];
            // Pick the earliest due background group, not the first catalog
            // group. A slow group with a short interval advances its deadline
            // after every start, allowing older due groups to take their turn.
            // Keep deferred shop work out of selection/deadline calculation.
            // Its original schedule state and field timestamps remain untouched.
            var eligible=backgroundPersonalShopDeferral.Eligible(schedule);
            var slice=PriorityReadSchedule.PickDue(eligible,now,previousBackgroundWasActors);
            if(slice is null)
            {
                double due=eligible.Count==0?now+IntervalMilliseconds:eligible.Min(s=>s.NextDue);
                try{await WaitSchedulerAsync(due-now,token).ConfigureAwait(false);}
                catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                continue;
            }

            // A lease may have arrived after PickDue. Recheck admission at
            // the same lock used by lease acquisition; an earlier admission may finish.
            if(!backgroundPersonalShopDeferral.TryAdmit(slice))continue;
            double startedMilliseconds=clock.Elapsed.TotalMilliseconds;
            double dueMilliseconds=slice.NextDue;
            double? priorityDueBeforeRead=priority?.NextDue;
            DateTimeOffset startedAt=DateTimeOffset.UtcNow;
            int targetMilliseconds=slice.Priority?IntervalMilliseconds:configuredBackgroundIntervals[slice.Name];
            slice.LastStarted=startedMilliseconds;slice.NextDue=startedMilliseconds+targetMilliseconds;
            ReadResult result;Exception? readException=null;
            try
            {
                // This is the only source call in this loop. It completes and
                // closes before any later priority or background slice begins.
                result=sliced.ReadSlice(slice.Fields,token);
                if(result is null||result.Fields is null||result.Fields.Values.Any(f=>f is null))
                    throw new InvalidDataException("讀取來源回傳不合法的切片資料，已清除此輪");
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                result=new(null,[],"Error",ex.Message);readException=ex;
            }
            double completedMilliseconds=clock.Elapsed.TotalMilliseconds;
            DateTimeOffset completedAt=DateTimeOffset.UtcNow;
            if(token.IsCancellationRequested)break;
            var published=PublishSliceResult(result,selected,slice.Fields,generation,true);
            if(!slice.Priority)previousBackgroundWasActors=slice.Name=="actors";
            bool recoveryScheduled=PriorityReadSchedule.ScheduleRecovery(slice,result,completedMilliseconds);
            if(recoveryScheduled)Log("ACTOR_RECOVERY_SCHEDULED","集合暫時變動，已安排有界快速重新採樣",new{Attempt=slice.ConsecutiveTransientFailures,DelayMs=50,Fields=result.RetryableFields});
            SignalSlicePublished();
            if(readException is not null)Log("READ_EXCEPTION",readException.Message,new{Type=readException.GetType().FullName,Slice=slice.Name});
            double priorityDelay=slice.Priority?Math.Max(0,startedMilliseconds-dueMilliseconds):
                priorityDueBeforeRead is {} priorityDue?Math.Max(0,completedMilliseconds-priorityDue):0;
            Log("SLICE",slice.Priority?"優先欄位切片已發布":"背景欄位切片已發布",new
            {
                Name=slice.Name,Priority=slice.Priority,Fields=slice.Fields.EnabledKeys,
                StartedAt=startedAt,CompletedAt=completedAt,PublishedAt=published.PublishedAt,
                DurationMs=completedMilliseconds-startedMilliseconds,TargetIntervalMs=targetMilliseconds,
                FreshnessLimitMs=HubProtocol.FreshMilliseconds,TargetAtOrBeyondFreshnessLimit=targetMilliseconds>=HubProtocol.FreshMilliseconds,
                FreshnessNotice=HubProtocol.IntervalFreshnessNotice(targetMilliseconds),
                StartDelayMs=Math.Max(0,startedMilliseconds-dueMilliseconds),PriorityBudgetDelayMs=priorityDelay,
                ConfigurationVersion=generation,PublishedConfigurationVersion=published.ReadConfigurationVersion,
                published.Sequence,published.State,published.Source,SliceReads=result.Reads,SliceReadBytes=result.ReadBytes,
                result.NoSourceRead,result.RetryableFields,RecoveryScheduled=recoveryScheduled,published.ReadCycles,published.MemoryReads,published.MemoryBytes,
                Readings=published.Fields.Where(p=>slice.Fields.Enabled(p.Key)).ToDictionary(p=>p.Key,p=>p.Value)
            });
            // Scheduling completion includes synchronous publication callbacks
            // and LOG I/O. Otherwise they can consume the entire 10 ms gap and
            // make an over-budget priority read starve every background slice.
            // The logged CompletedAt/DurationMs above remain source-read timing.
            slice.LastCompleted=clock.Elapsed.TotalMilliseconds;
            // Do not pre-queue a background batch. The next loop checks the
            // priority deadline again before choosing exactly one more slice.
        }
    }

    private HubSnapshot PublishSliceResult(ReadResult result,ReadSelection selected,ReadSelection requested,long generation,bool sampled)
    {
        lock(selectionGate)
        {
            if(sampled)cycles++;totalReads+=result.Reads;totalBytes+=result.ReadBytes;
            bool configurationChanged=generation!=configurationVersion;
            var previous=Volatile.Read(ref latest);
            bool error=result.State=="Error";
            // This explicit source contract is only for a slice that never
            // attempted process access and has no observed/value-bearing data.
            // An ordinary Source=null result still clears every old field.
            bool noSourceRead=result.NoSourceRead&&result.Source is null&&result.Reads==0&&result.ReadBytes==0&&!error&&
                result.Fields.Values.All(f=>f.Value is null&&f.State!="Observed");
            SourceIdentity? identity=configurationChanged||error?null:noSourceRead?previous.Source:result.Source;
            bool canMerge=!configurationChanged&&!error&&previous.ReadConfigurationVersion==generation&&
                (noSourceRead||(identity is not null&&previous.Source==identity));
            var fields=configurationChanged?selection.Empty("舊設定採樣已捨棄，等待新採樣"):
                canMerge?selected.Filter(previous.Fields):selected.Empty(error?result.Detail:"等待此來源的本欄位新採樣");
            DateTimeOffset publishedAt=DateTimeOffset.UtcNow;
            if(!configurationChanged&&!error)
            {
                foreach(string key in requested.EnabledKeys)
                {
                    if(!selected.Enabled(key))continue;
                    var reading=result.Fields.GetValueOrDefault(key)??FieldReading.Unknown("本切片未取得欄位資料");
                    if(canMerge&&identity is not null&&identity==previous.Source&&
                        TransientCollectionRecovery.TryRetain(key,result,reading,previous.Fields.GetValueOrDefault(key),publishedAt,out var retained))
                    {
                        fields[key]=retained;
                        Log("FIELD_TRANSIENT_RETAINED","集合暫時失敗；原時間與有效期限不變",new{Key=key,retained.SampledAt,AgeMs=(publishedAt-retained.SampledAt!.Value).TotalMilliseconds,Reason=reading.Detail});
                        continue;
                    }
                    // Source-less statuses may describe unsupported/unconfirmed
                    // fields, but they can never introduce an observed value.
                    fields[key]=identity is null&&!noSourceRead
                        ?reading.State=="Observed"?FieldReading.Unknown("來源無效，已清除此欄位"):
                            reading with {Value=null}
                        :reading;
                }
            }
            var current=new HubSnapshot
            {
                InstanceId=instance,Sequence=++sequence,ReadConfigurationVersion=configurationVersion,PublishedAt=publishedAt,
                Source=identity,Fields=MarkManualShopFields(fields),
                State=configurationChanged?(selection.Any?"Waiting":"Disabled"):
                    noSourceRead&&previous.Source is not null?previous.State:result.State,
                Detail=configurationChanged?"本輪設定已變更，舊採樣未發布":
                    noSourceRead&&previous.Source is not null&&(result.State is "Unsupported" or "Unknown")
                        ?"本切片欄位尚未支援或未確認，未執行讀取；其他欄位保留各自的狀態與有效期限。":result.Detail,
                ReadCycles=cycles,MemoryReads=totalReads,MemoryBytes=totalBytes
            }.AtTime(publishedAt);
            // AtTime creates a new dictionary and nulls expired values without
            // changing each field's original SampledAt or any earlier snapshot.
            // A closed slice with the same source may fail locally while other
            // groups remain fresh. Summarize the merged, expiry-checked fields;
            // the last slice alone does not describe the whole snapshot.
            // Explicit lifecycle/source states retain their existing authority.
            if(identity is not null&&(current.State is "Reading" or "Unknown" or "Stale"))
            {
                string aggregateState=current.Fields.Values.Any(f=>f.State=="Observed")?"Reading":
                    current.Fields.Values.Any(f=>f.State=="Stale")?"Stale":
                    current.Fields.Values.Any(f=>f.State=="Unknown")?"Unknown":current.State;
                string aggregateDetail=aggregateState=="Reading"&&aggregateState!=current.State
                    ?"部分欄位未取得有效值，其他資料仍有效；原因請查看各欄位。":current.Detail;
                current=current with {State=aggregateState,Detail=aggregateDetail};
            }
            Volatile.Write(ref latest,current);return current;
        }
    }

    private void SignalSlicePublished()
    {
        server?.SignalChanged();
        try{Changed?.Invoke(Snapshot);}catch(Exception ex){Log("SUBSCRIBER_ERROR",ex.Message);}
    }
}
