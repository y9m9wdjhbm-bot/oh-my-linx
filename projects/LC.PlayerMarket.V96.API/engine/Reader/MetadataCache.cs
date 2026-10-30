namespace MemoryHub.Reader;

// Keys contain actual loaded addresses. Callers resolve profile symbols exactly once.
internal sealed record ModuleMetadataCacheKey(int ProcessId,long CreationFileTime,ulong Image,int ImageSize,
    string ImageSha256,string ProfileIdentity,ulong Director,ulong Scene,ulong SelfId);

internal sealed record ModuleMetadataCacheMetrics(bool Enabled,bool FullVerification,long Hits,long Misses,
    int Entries,int Bytes,int PendingEntries,int PendingBytes,int LifetimeMilliseconds,string Mode,string? InvalidatedReason,
    long CandidateRollbacks=0,long CandidateEntriesRolledBack=0);

// Pure, bounded, transaction-owned cache: no process handles, heap reads, or game operations.
// Pending reads are deliberately NEVER hits. The attach/periodic full cycle must really read
// each metadata A/B observation before its caller may commit the successfully closed cycle.
internal sealed class ModuleMetadataCache
{
    internal readonly record struct EntryKey(ulong Address,int Count,bool Executable=false);
    private readonly Dictionary<EntryKey,byte[]> entries=[];
    private readonly Func<long> now;
    private readonly int maximumEntries,maximumBytes;
    private ModuleMetadataCacheKey? identity;
    private long generation,verifiedAt;
    private bool verified;
    private int bytes;
    internal int LifetimeMilliseconds{get;}
    internal int EntryCount=>entries.Count;
    internal int ByteCount=>bytes;
    internal string? InvalidatedReason{get;private set;}

    internal ModuleMetadataCache(int maximumEntries=2048,int maximumBytes=512*1024,
        int lifetimeMilliseconds=5000,Func<long>? clockMilliseconds=null)
    {
        if(maximumEntries is <=0 or >2048||maximumBytes is <=0 or >512*1024||lifetimeMilliseconds is <=0 or >5000)throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.maximumEntries=maximumEntries;this.maximumBytes=maximumBytes;LifetimeMilliseconds=lifetimeMilliseconds;
        now=clockMilliseconds??(()=>Environment.TickCount64);
    }

    internal Transaction Begin(ModuleMetadataCacheKey key,bool forceFullVerification=false)
    {
        if(key.ProcessId<=0||key.CreationFileTime<=0||key.Image<0x10000||key.ImageSize<=0||
            string.IsNullOrWhiteSpace(key.ImageSha256)||string.IsNullOrWhiteSpace(key.ProfileIdentity)||
            key.Director<0x10000||key.Scene<0x10000||key.SelfId is 0 or ulong.MaxValue)
        {Invalidate("IncompleteSourceNamespace");throw new ArgumentException("完整程序、映像、profile、Scene 與自身來源必須先經實讀核對。",nameof(key));}
        if(identity!=key){Invalidate("SourceNamespaceChanged");identity=key;}
        long started=now();
        if(forceFullVerification)Invalidate("ForcedFullVerification");
        else if(verified&&(started<verifiedAt||started-verifiedAt>=LifetimeMilliseconds))Invalidate("PeriodicFullVerificationDue");
        // Only the newest transaction can publish, even if a caller replaces an unfinished cycle.
        generation++;return new(this,generation,!verified,started);
    }

    internal void Invalidate(string reason)
    {
        entries.Clear();bytes=0;verified=false;generation++;InvalidatedReason=reason;
    }

    internal sealed class Transaction(ModuleMetadataCache owner,long epoch,bool fullVerification,long started)
    {
        private readonly Dictionary<EntryKey,byte[]> pending=[];
        private readonly List<EntryKey> pendingOrder=[];
        private bool ended,committed;
        private int pendingBytes;
        internal bool FullVerification=>fullVerification;
        internal long Hits{get;private set;}
        internal long Misses{get;private set;}
        internal long CandidateRollbacks{get;private set;}
        internal long CandidateEntriesRolledBack{get;private set;}
        internal bool Active=>!ended&&epoch==owner.generation;
        internal ModuleMetadataCacheMetrics Metrics=>new(Active||committed&&epoch==owner.generation,fullVerification,Hits,Misses,
            owner.EntryCount,owner.ByteCount,pending.Count,pendingBytes,owner.LifetimeMilliseconds,
            committed&&epoch==owner.generation?fullVerification?"FreshFullVerificationCommitted":"BoundedVerifiedMetadataReuseCommitted":
                !Active?"DisabledOrDiscarded":fullVerification?"FreshFullVerification":"BoundedVerifiedMetadataReuse",owner.InvalidatedReason,
            CandidateRollbacks,CandidateEntriesRolledBack);

        private void Validate(EntryKey key)
        {
            var source=owner.identity!;
            if(key.Count is <=0 or >4096||key.Executable&&key.Count!=1||key.Address<source.Image||
                key.Count>source.ImageSize||key.Address-source.Image>(ulong)(source.ImageSize-key.Count))
            {Discard("RejectedNonModuleMetadataRange");throw new ArgumentOutOfRangeException(nameof(key),"Only bounded actual module metadata ranges are accepted.");}
        }

        private bool Current()
        {
            if(!Active)return false;
            long current=owner.now();long anchor=fullVerification?started:owner.verifiedAt;
            if(current<anchor||current-anchor>=owner.LifetimeMilliseconds){Discard("MetadataLifetimeExpiredDuringCycle");return false;}
            return true;
        }
        internal bool TryGet(EntryKey key,out byte[] value)
        {
            bool current=Current();if(current)Validate(key);
            if(current&&!fullVerification&&owner.entries.TryGetValue(key,out var cached))
            {Hits++;value=(byte[])cached.Clone();return true;}
            Misses++;value=[];return false;
        }
        // Index-only savepoint. A rejected discovery candidate can remove its own new
        // observations while retaining committed data and earlier verified pending keys.
        internal int Checkpoint()=>Current()?pendingOrder.Count:-1;
        internal void Rollback(int checkpoint)
        {
            if(!Current()||checkpoint<0)return;
            if(checkpoint>pendingOrder.Count)
            {Discard("InvalidMetadataCheckpoint");throw new ArgumentOutOfRangeException(nameof(checkpoint));}
            CandidateRollbacks++;CandidateEntriesRolledBack+=pendingOrder.Count-checkpoint;
            for(int i=pendingOrder.Count-1;i>=checkpoint;i--)
            {
                var key=pendingOrder[i];pendingBytes-=pending[key].Length;
                pending.Remove(key);pendingOrder.RemoveAt(i);
            }
        }
        // False means a second REAL read disagreed with its staged first observation.
        internal bool Stage(EntryKey key,byte[] value)
        {
            if(!Current())return true;
            Validate(key);
            if(value.Length!=key.Count||key.Executable&&value[0]!=1)
            {Discard("InvalidMetadataObservation");throw new ArgumentException("Metadata length or executable validation differs from its exact key.",nameof(value));}
            if(pending.TryGetValue(key,out var prior))
            {
                if(prior.AsSpan().SequenceEqual(value))return true;
                Discard("MetadataChangedWithinCycle");return false;
            }
            if(owner.entries.Count+pending.Count>=owner.maximumEntries||value.Length>owner.maximumBytes-owner.bytes-pendingBytes)return true;
            pending.Add(key,(byte[])value.Clone());pendingOrder.Add(key);pendingBytes+=value.Length;return true;
        }
        internal bool Commit()
        {
            if(!Current()){ended=true;pending.Clear();pendingOrder.Clear();pendingBytes=0;return false;}
            foreach(var item in pending)
            {
                if(owner.entries.ContainsKey(item.Key))continue;
                if(owner.entries.Count>=owner.maximumEntries||item.Value.Length>owner.maximumBytes-owner.bytes)continue;
                owner.entries.Add(item.Key,item.Value);owner.bytes+=item.Value.Length;
            }
            if(fullVerification){owner.verifiedAt=started;owner.verified=true;}
            owner.InvalidatedReason=null;committed=true;ended=true;pending.Clear();pendingOrder.Clear();pendingBytes=0;return true;
        }
        internal void Discard(string reason)
        {
            if(!ended&&epoch==owner.generation)owner.Invalidate(reason);
            ended=true;pending.Clear();pendingOrder.Clear();pendingBytes=0;
        }
    }
}

internal sealed partial class ReadSession
{
    private ModuleMetadataCache.Transaction? metadataTransaction;
    internal ModuleMetadataCacheMetrics MetadataMetrics=>metadataTransaction?.Metrics??
        new(false,true,0,0,0,0,0,0,5000,"FreshUncached",null);
    // Metadata reuse is never labelled a fresh metadata A/B observation.
    internal string MetadataEvidence=>roundMetadata is not null?"CurrentReadModuleMetadataFirstA_FinalFreshC;DynamicSourcesAndValuesFreshAB":MetadataMetrics.Hits==0?"ModuleMetadataFreshReads":
        "ModuleMetadataVerifiedCacheWithin5Seconds_NotFreshAB;DynamicSourcesAndValuesFreshReads";

    internal void UseMetadataCache(ModuleMetadataCache cache,ModuleMetadataCacheKey key,bool forceFullVerification=false)
    {
        Boundary();
        if(key.ProcessId!=pid||key.CreationFileTime!=creation||key.Image!=Image||key.ImageSize!=ImageSize||
            !string.Equals(key.ImageSha256,Profile.Sha256,StringComparison.OrdinalIgnoreCase)||key.ProfileIdentity!=Profile.CacheIdentity)
            throw new ProbeFailure("GROUP_SOURCE_CHANGED","快取命名空間與目前程序／映像／profile 不符。");
        metadataTransaction?.Discard("SessionCacheRebound");
        metadataTransaction=cache.Begin(key,forceFullVerification);
    }
    internal bool CommitMetadataCache()
    {
        Boundary();if(ReadFailures!=0){DiscardMetadataCache("CycleReadFailure");return false;}
        return metadataTransaction?.Commit()??false;
    }
    internal void DiscardMetadataCache(string reason)=>metadataTransaction?.Discard(reason);
    internal void DisableMetadataCache(string reason="ExplicitFullUncached")=>DiscardMetadataCache(reason);
    internal int MetadataCheckpoint(){Boundary();return metadataTransaction?.Checkpoint()??-1;}
    internal void RollbackMetadataCandidate(int checkpoint){Boundary();metadataTransaction?.Rollback(checkpoint);}

    // actualRva is already relocated. This method intentionally does NOT call Rva().
    internal byte[] MetaRequired(uint actualRva,int count)
    {
        Boundary();
        if(!InImage(actualRva,count)||count>4096)throw new ProbeFailure("META_RANGE","明確模組中繼資料超出有界映像範圍。");
        if(roundMetadata is not null)return ReadRoundMetadata(actualRva,count);
        var key=new ModuleMetadataCache.EntryKey(Image+actualRva,count);
        if(metadataTransaction?.TryGet(key,out var cached)==true){LastNativeError=0;Boundary();return cached;}
        try
        {
            var bytes=Required(actualRva,count);
            if(metadataTransaction?.Stage(key,bytes)==false)throw new ProbeFailure("GROUP_SOURCE_CHANGED","模組中繼資料在完整核對中改變。");
            return bytes;
        }
        catch{DiscardMetadataCache("MetadataReadOrValidationFailure");throw;}
    }
    internal byte[] MetaPointed(ulong actualAddress,int count)
    {
        Boundary();
        if(actualAddress<Image||actualAddress-Image>uint.MaxValue||!InImage((uint)(actualAddress-Image),count))
            throw new ProbeFailure("META_RANGE","明確模組中繼資料位址不在目前映像。");
        return MetaRequired((uint)(actualAddress-Image),count);
    }
}
