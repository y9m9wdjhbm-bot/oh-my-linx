using System.Text.Json;
using System.Text.Json.Serialization;
namespace MemoryHub;

public static class HubProtocol
{
    public const int Schema = 1;
    public const string Version = "31.0.0";
    public const int MaxMessageBytes = 1024 * 1024;
    public const int FreshMilliseconds = 2000;
    public static string IntervalFreshnessNotice(int milliseconds)=>milliseconds>=FreshMilliseconds
        ?$"間隔 {milliseconds} 毫秒已達有效期限 {FreshMilliseconds} 毫秒；下一筆採樣前可能顯示 Stale（已過期）。"
        :$"有效期限 {FreshMilliseconds} 毫秒；目前間隔 {milliseconds} 毫秒，讀取或排程延後仍可能過期。";
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive=true };
    public static string PipeName => PipeForMajor(32);
    // Schema 1 remains wire-compatible with the published Market V10 SDK.
    // The alias transmits the real V32 ServiceVersion; it never impersonates V16.
    public static IReadOnlyList<string> CompatibilityPipeNames => new[] { PipeForMajor(31), PipeForMajor(28), PipeForMajor(27), PipeForMajor(26), PipeForMajor(25), PipeForMajor(24), PipeForMajor(23), PipeForMajor(22), PipeForMajor(21), PipeForMajor(20), PipeForMajor(19), PipeForMajor(18), PipeForMajor(17), PipeForMajor(16) };
    // Both transports derive their bounded endpoint count from the supported
    // protocol list so a release cannot reject its own default configuration.
    public static int MaximumPipeNames => 1 + CompatibilityPipeNames.Count;
    private static string PipeForMajor(int major) => "LC.MemoryHub.v" + major + "." +
        System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value.Replace('-','_');
}
public sealed record SourceIdentity(int ProcessId,long CreationFileTime,string ImagePath,string ImageBase,uint PeTimestamp,uint ImageSize,string DiskSha256,string SessionKey);
public sealed record FieldReading(string State, DateTimeOffset? SampledAt, JsonElement? Value, string Detail, string Evidence)
{
    public static FieldReading Observed<T>(T value,DateTimeOffset at,string evidence="StructureValidated") => new("Observed",at,JsonSerializer.SerializeToElement(value,HubProtocol.Json),"",evidence);
    public static FieldReading Unknown(string detail) => new("Unknown",null,null,detail,"Unverified");
    public static FieldReading Unsupported(string detail) => new("Unsupported",null,null,detail,"Unsupported");
    public static FieldReading Disabled() => new("Disabled",null,null,"已停用本欄位的實際讀取","Disabled");
    public bool Fresh(DateTimeOffset now) => State=="Observed" && SampledAt is {} at && at<=now && now-at<=TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds);
    internal string ExpirationDetail(DateTimeOffset now)=>SampledAt is not {} at
        ?"缺少採樣時間，無法判斷資料新鮮度，值已清除"
        :at>now?"採樣時間晚於目前時鐘，資料暫列 Stale，值已清除"
        :$"Stale（已過期）：距採樣 {(now-at).TotalMilliseconds:0} 毫秒，有效期限 {HubProtocol.FreshMilliseconds} 毫秒。間隔較長、排程延後或未取得新樣本都可能造成過期；原採樣時間保留，值已清除。";
}
public sealed record ReadResult(SourceIdentity? Source,Dictionary<string,FieldReading> Fields,string State,string Detail,long Reads=0,long ReadBytes=0)
{
    // Only an explicit zero-read, value-free result may leave the prior source alone.
    public bool NoSourceRead {get;init;}
    public ReadOperationMetrics? ReadMetrics {get;init;}
    // Set only after the reader closes the SAME current source successfully.
    // Identifies a failed collection attempt, not a replacement observation.
    public string[] RetryableFields {get;init;}=[];
    // Diagnostic evidence from a failed reader cycle, never authority to use its values.
    // Consumers must still reacquire and verify the complete expected source.
    public string? ReadFailureCode {get;init;}
    public int? ReadFailureNativeError {get;init;}
}
public interface IMemorySource : IDisposable { ReadResult Read(CancellationToken cancellationToken); }
public sealed record HubSnapshot
{
    public int SchemaVersion {get;init;}=HubProtocol.Schema;
    public string ServiceVersion {get;init;}=HubProtocol.Version;
    public string InstanceId {get;init;}="";
    public long Sequence {get;init;}
    public long ReadConfigurationVersion {get;init;}
    public DateTimeOffset PublishedAt {get;init;}
    public SourceIdentity? Source {get;init;}
    public string State {get;init;}="Waiting";
    public string Detail {get;init;}="等待讀取服務";
    public Dictionary<string,FieldReading> Fields {get;init;}=[];
    public long ReadCycles {get;init;}
    public long MemoryReads {get;init;}
    public long MemoryBytes {get;init;}
    public int ConnectedClients {get;init;}
    public bool LogEnabled {get;init;}
    public string? LogError {get;init;}
    public HubSnapshot AtTime(DateTimeOffset now)
    {
        var fields=Fields.ToDictionary(p=>p.Key,p=>p.Value.State=="Observed"&&!p.Value.Fresh(now)
            ?p.Value with {State="Stale",Value=null,Detail=p.Value.ExpirationDetail(now)}:p.Value);
        bool allExpired=State=="Reading"&&!fields.Values.Any(f=>f.State=="Observed");
        return this with {Fields=fields,State=allExpired?"Stale":State,
            Detail=allExpired?"目前沒有仍有效的欄位；先前讀值已過期，原因與採樣時間請查看各欄位。":Detail};
    }
}
public sealed record HubLog(DateTimeOffset At,string Kind,string Message,object? Data=null);

