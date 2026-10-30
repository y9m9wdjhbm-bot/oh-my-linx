using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
namespace LcPlayerMarket;
internal sealed record CatalogSyncResult(int SourceFiles,int Copied,int Reused,long BytesCopied,double ElapsedMs);
internal sealed class MarketMobileCatalogPublisher
{
 internal const string CanonicalSource=@"D:\lineage\LC_PlayerMarket_UserData\V38";
 internal const string CanonicalDestination=@"D:\lineage\LC_PlayerMarket_MobileCatalog\V1";
 private sealed class WriterGate { internal int PriorityWriters; internal bool UncommittedClear; }
 private static readonly ConcurrentDictionary<string,WriterGate> Gates=new(StringComparer.OrdinalIgnoreCase);
 private readonly string source,destination;private readonly WriterGate gate;
 internal string Destination=>destination;
#if CATALOG_PUBLISHER_TEST
 internal Action<string>? TestAfterHistoryRead;
 internal int TestPriorityWriters=>Volatile.Read(ref gate.PriorityWriters);
#endif
 internal MarketMobileCatalogPublisher(string source,string destination)
 {
  this.source=Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));this.destination=Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
  static string Prefix(string path)=>Path.EndsInDirectorySeparator(path)?path:path+Path.DirectorySeparatorChar;
  if(this.source.Equals(this.destination,StringComparison.OrdinalIgnoreCase)||this.destination.StartsWith(Prefix(this.source),StringComparison.OrdinalIgnoreCase)||this.source.StartsWith(Prefix(this.destination),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("商品公開副本必須與私人資料區分開。");
  gate=Gates.GetOrAdd(this.destination,_=>new());
 }
 internal static MarketMobileCatalogPublisher? ForSource(string source)=>Path.GetFullPath(source).Equals(CanonicalSource,StringComparison.OrdinalIgnoreCase)?new(source,CanonicalDestination):null;
 private FileStream Lease()
 {
  RejectLinks(source);RejectLinks(Path.GetDirectoryName(destination)!);RejectLinks(destination);Directory.CreateDirectory(destination);
  RejectLinks(Path.Combine(source,"list-state.json"));RejectLinks(Path.Combine(destination,"list-state.json"));
  foreach(var entry in Directory.EnumerateFileSystemEntries(destination))if(Path.GetFileName(entry) is not("history" or "list-state.json"))throw new InvalidDataException("專用商品目錄含未授權項目，未發布。");
  RejectLinks(Path.Combine(destination,"history"));Directory.CreateDirectory(Path.Combine(destination,"history"));
  string lockPath=Path.Combine(Path.GetDirectoryName(destination)!,"catalog-publisher.lock");RejectLinks(lockPath);
  return new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
 }
 internal CatalogSyncResult Synchronize(CancellationToken cancellation=default)
 {
  var watch=System.Diagnostics.Stopwatch.StartNew();int copied=0,reused=0;long bytes=0;
  cancellation.ThrowIfCancellationRequested();
  string history=Path.Combine(source,"history");RejectLinks(history);
  var files=Directory.GetFiles(history,"*.json",SearchOption.TopDirectoryOnly);if(files.Length>100000)throw new InvalidDataException("商品檔案超過同步上限。");
  BulkWrite(()=>{SyncMarker();return 0L;},cancellation);
  foreach(string file in files)
  {
   var result=BulkWrite(()=>CopyHistory(file),cancellation);
   if(result>0){copied++;bytes+=result;}else reused++;
  }
  BulkWrite(()=>{SyncMarker();return 0L;},cancellation);
  return new(files.Length,copied,reused,bytes,watch.Elapsed.TotalMilliseconds);
 }
 private long BulkWrite(Func<long> action,CancellationToken cancellation)
 {
  while(true)
  {
   cancellation.ThrowIfCancellationRequested();
   if(!Monitor.TryEnter(gate,50))continue;
   try
   {
    if(Volatile.Read(ref gate.PriorityWriters)>0){Monitor.Wait(gate,50);continue;}
    cancellation.ThrowIfCancellationRequested();
    using(Lease()){cancellation.ThrowIfCancellationRequested();return action();}
   }
   finally{Monitor.Exit(gate);}
  }
 }
 private void PriorityWrite(Action action)
 {
  Interlocked.Increment(ref gate.PriorityWriters);
  try{lock(gate)using(Lease()){action();}}
  finally{lock(gate){Interlocked.Decrement(ref gate.PriorityWriters);Monitor.PulseAll(gate);}}
 }
 internal void PublishHistory(string file)
 {PriorityWrite(()=>{SyncMarker();CopyHistory(file);});}
 internal MarketOwnedDirectory[] BeginFreshHistory(byte[] clear,byte[] resume,Action clearSource,Func<MarketOwnedDirectory[]> retireSource,Action resumeSource)
 {
  ValidateMarker(clear);ValidateMarker(resume);var retired=new List<MarketOwnedDirectory>();
  PriorityWrite(()=>
  {
   // The exported hide marker is visible before either immutable history
   // directory changes. A failure leaves both readers closed to old pages.
   AtomicWrite(Path.Combine(destination,"list-state.json"),clear);gate.UncommittedClear=true;
   clearSource();
   retired.AddRange(retireSource());
   retired.AddRange(MarketSessionLifecycle.RetireIfPresent(Path.Combine(destination,"history"),Path.GetDirectoryName(destination)!,"catalog-history"));
   Directory.CreateDirectory(Path.Combine(destination,"history"));
   resumeSource();AtomicWrite(Path.Combine(destination,"list-state.json"),resume);gate.UncommittedClear=false;
  });
  return retired.ToArray();
 }
 // The clear marker is committed to the exported catalog before local success.
 // Resume only follows the local commit. Failure leaves the catalog hidden/old,
 // never fabricates new capture times or silently reports an exported clear.
 internal void CommitMarker(byte[] bytes,bool clearing,Action commitSource)
 {
  ValidateMarker(bytes);
  PriorityWrite(()=>
  {
   if(clearing){AtomicWrite(Path.Combine(destination,"list-state.json"),bytes);gate.UncommittedClear=true;}
   commitSource();
   gate.UncommittedClear=false;
   if(!clearing)AtomicWrite(Path.Combine(destination,"list-state.json"),bytes);
  });
 }
 private long CopyHistory(string file)
 {
  if(Path.GetDirectoryName(Path.GetFullPath(file))!=Path.Combine(source,"history"))throw new InvalidDataException("僅允許原資料區頂層商品 JSON。");
  string name=Path.GetFileName(file);if(name.Length!=69||!name.EndsWith(".json",StringComparison.OrdinalIgnoreCase)||!name[..64].All(Uri.IsHexDigit))throw new InvalidDataException("商品檔名不符已支援格式。");
  var info=new FileInfo(file);if((info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length is <=0 or >8*1024*1024)throw new InvalidDataException("商品資料路徑或大小不符。");
  byte[] bytes=File.ReadAllBytes(file);
#if CATALOG_PUBLISHER_TEST
  TestAfterHistoryRead?.Invoke(file);
#endif
  using(var doc=JsonDocument.Parse(bytes)){var r=doc.RootElement;if(!r.TryGetProperty("rows",out var rows)||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>256||!r.TryGetProperty("capturedAt",out var at)||!at.TryGetDateTimeOffset(out _)||!r.TryGetProperty("viewerDirection",out var direction)||direction.GetString() is not("ViewerBuy" or "ViewerSell")||!r.TryGetProperty("owner",out var owner)||owner.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("不是可匯出的商品歷史格式。");}
  string target=Path.Combine(destination,"history",name);
  if(File.Exists(target)){RejectFileLink(target);if(!SHA256.HashData(bytes).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))))throw new InvalidDataException("既有商品副本內容不同，未覆寫。");return 0;}
  AtomicWrite(target,bytes);return bytes.Length;
 }
 private void SyncMarker()
 {
  // An exported clear must survive a failed local commit, including a bulk
  // scan already in progress. Only an explicit successful marker commit releases it.
  if(gate.UncommittedClear)throw new IOException("清除尚未在本機完成，商品備份保持隱藏。");
  string path=Path.Combine(source,"list-state.json"),target=Path.Combine(destination,"list-state.json");
  if(!File.Exists(path)){if(File.Exists(target))throw new InvalidDataException("原始清除標記缺失，未恢復舊商品。");return;}
  RejectFileLink(path);if(new FileInfo(path).Length>4096)throw new InvalidDataException("清除標記過大。");
  byte[] bytes=File.ReadAllBytes(path);ValidateMarker(bytes);
  if(File.Exists(target))
  {
   RejectFileLink(target);byte[] exported=File.ReadAllBytes(target);if(exported.AsSpan().SequenceEqual(bytes))return;
   ValidateMarker(exported);
   using var oldState=JsonDocument.Parse(exported);using var localState=JsonDocument.Parse(bytes);
   if(oldState.RootElement.GetProperty("clearedAt").GetDateTimeOffset()>localState.RootElement.GetProperty("clearedAt").GetDateTimeOffset())
    throw new IOException("商品備份已有較新的清除標記，拒絕恢復舊清單。");
  }
  AtomicWrite(target,bytes);
 }
 private static void ValidateMarker(byte[] bytes)
 {using var doc=JsonDocument.Parse(bytes);var r=doc.RootElement;if(r.GetProperty("version").GetInt32()!=1||!r.GetProperty("clearedAt").TryGetDateTimeOffset(out var cleared)||cleared==default)throw new InvalidDataException("清除標記無效。");if(r.TryGetProperty("resumeAfter",out var resume)&&resume.ValueKind!=JsonValueKind.Null&&(!resume.TryGetDateTimeOffset(out var resumed)||resumed<cleared))throw new InvalidDataException("恢復收錄時間無效。");}
 private static void RejectFileLink(string path){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("不接受連結資料。");}
 private static void RejectLinks(string path)
 {
  for(string? current=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));current is not null;current=Path.GetDirectoryName(current))
  {
   try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("不接受連結資料路徑。");}
   catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
  }
 }
 private void AtomicWrite(string path,byte[] bytes)
 {
  if(File.Exists(path))RejectFileLink(path);
  // Temporary bytes stay outside the SMB export, on the same volume.
  string temp=Path.Combine(Path.GetDirectoryName(destination)!,"catalog-write-"+Guid.NewGuid().ToString("N")+".tmp");
  try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.Write(bytes);file.Flush(true);}File.Move(temp,path,true);}
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
}
