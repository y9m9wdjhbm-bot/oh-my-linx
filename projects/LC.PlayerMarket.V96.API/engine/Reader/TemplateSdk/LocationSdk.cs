using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace LcLocationTemplate;

public sealed class LocationContractException : Exception
{
    public string Code { get; }
    public LocationContractException(string code, string message) : base(message) => Code = code;
}

public sealed record BuildIdentity(string Sha256, long FileLength, int PeOffset,
    uint PeTimestamp, uint ImageSize, ushort Machine);

public enum LocationKind { ModuleRva, FieldOffset, NativeFunctionRva }
public enum CheckState { Verified, Unknown }

public sealed record SymbolDefinition(string Name, LocationKind Kind, int? Value,
    string Status, IReadOnlyList<string> RequiredGates)
{
    public bool Callable => false;
}
public sealed record FieldDefinition(string Name, string Status,
    IReadOnlyList<string> RequiredSymbols, IReadOnlyList<string> RequiredGates);
public sealed class LocationProfile
{
    public string Id { get; }
    public string Status { get; }
    public BuildIdentity Identity { get; }
    public string Sha256 { get; }
    public IReadOnlyDictionary<string, SymbolDefinition> Symbols { get; }
    public IReadOnlyDictionary<string, FieldDefinition> Fields { get; }
    internal LocationProfile(string id, string status, BuildIdentity identity, string sha256,
        IReadOnlyDictionary<string, SymbolDefinition> symbols,
        IReadOnlyDictionary<string, FieldDefinition> fields)
    {
        Id = id; Status = status; Identity = identity; Sha256 = sha256;
        Symbols = symbols; Fields = fields;
    }
}
public sealed record CatalogEntry(string File, string Sha256, string Id, BuildIdentity Identity);
public sealed record LocationCatalog(IReadOnlyList<CatalogEntry> Profiles);

public sealed record SourceIdentity(int Pid, long CreationTimeFileTime, string ProcessPath,
    ulong ModuleBase, uint ImageSize, uint PeTimestamp, ushort Machine,
    string DiskSha256, long DiskFileLength, int DiskPeOffset,
    string DiskRelation, string LoadedFingerprint);

// Constructed by the consumer's trusted in-process validator; no JSON deserializer is provided.
public sealed class RuntimeCheck
{
    public CheckState State { get; }
    public IReadOnlyList<string> Gates { get; }
    public RuntimeCheck(CheckState state, IEnumerable<string> gates)
    {
        if (!Enum.IsDefined(state)) Contract.Fail("CHECK_STATE", "Unknown check state.");
        State = state;
        Gates = Contract.CopyNames(gates, "runtime gates");
    }
}

public sealed class RuntimeValidationReport
{
    public string Schema => "lc-runtime-validation-v1";
    public string ProfileSha256 { get; }
    public SourceIdentity SourceBefore { get; }
    public SourceIdentity SourceAfter { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public IReadOnlyDictionary<string, RuntimeCheck> Checks { get; }

    private RuntimeValidationReport(string profileSha256, SourceIdentity before,
        SourceIdentity after, DateTimeOffset completedAtUtc,
        IReadOnlyDictionary<string, RuntimeCheck> checks)
    {
        ProfileSha256 = Contract.Hash(profileSha256);
        Contract.ValidateSource(before);
        Contract.ValidateSource(after);
        if (before != after) Contract.Fail("SOURCE_CHANGED", "Validation source changed.");
        if (completedAtUtc.Offset != TimeSpan.Zero)
            Contract.Fail("REPORT_TIME", "Report timestamp must be UTC.");
        SourceBefore = before;
        SourceAfter = after;
        CompletedAtUtc = completedAtUtc;
        if (checks is null || checks.Count > Contract.MaxItems)
            Contract.Fail("CHECKS", "Invalid checks collection.");
        var copy = new Dictionary<string, RuntimeCheck>(StringComparer.Ordinal);
        foreach (var pair in checks!)
        {
            Contract.Name(pair.Key, "check key");
            if (pair.Value is null || !copy.TryAdd(pair.Key, pair.Value))
                Contract.Fail("CHECKS", "Invalid or duplicate check.");
        }
        Checks = new ReadOnlyDictionary<string, RuntimeCheck>(copy);
    }

    public static RuntimeValidationReport Create(string profileSha256, SourceIdentity before,
        SourceIdentity after, DateTimeOffset completedAtUtc,
        IReadOnlyDictionary<string, RuntimeCheck> checks) =>
        new(profileSha256, before, after, completedAtUtc, checks);
}

public sealed record ResolvedReadLocation(string Symbol, LocationKind Kind, ulong Value,
    int ByteLength, bool IsOffset);

public sealed class LocationResolver
{
    private readonly LocationProfile profile;
    private readonly RuntimeValidationReport report;
    private readonly Func<SourceIdentity> sourceGetter;
    private readonly TimeSpan maxReportAge;

    public LocationResolver(LocationProfile profile, RuntimeValidationReport report,
        Func<SourceIdentity> sourceGetter, TimeSpan? maxReportAge = null)
    {
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        this.report = report ?? throw new ArgumentNullException(nameof(report));
        this.sourceGetter = sourceGetter ?? throw new ArgumentNullException(nameof(sourceGetter));
        this.maxReportAge = maxReportAge ?? TimeSpan.FromSeconds(30);
        if (this.maxReportAge <= TimeSpan.Zero || this.maxReportAge > TimeSpan.FromSeconds(300))
            Contract.Fail("REPORT_AGE", "Report lifetime must be greater than zero and at most 300 seconds.");
    }

    // Inspect only exposes the profile definition. It grants neither read validation nor invocation.
    public SymbolDefinition InspectSymbol(string name) => GetSymbol(name);

    public ResolvedReadLocation ResolveRead(string name, int byteLength = 1)
    {
        if (byteLength <= 0 || byteLength > 16 * 1024 * 1024)
            Contract.Fail("READ_SIZE", "Read size is outside the allowed range.");
        var symbol = GetSymbol(name);
        if (symbol.Kind == LocationKind.NativeFunctionRva)
            Contract.Fail("NATIVE_INSPECT_ONLY", "Native function RVAs can only be inspected.");
        var before = ValidateRuntime();
        ValidateSymbol(symbol);
        var value = checked((ulong)symbol.Value!.Value);
        ulong resolved = value;
        if (symbol.Kind == LocationKind.ModuleRva)
        {
            try
            {
                ulong end = checked(value + (ulong)byteLength);
                if (end > before.ImageSize) Contract.Fail("RVA_RANGE", "Read extends outside the module image.");
                _ = checked(before.ModuleBase + before.ImageSize);
                resolved = checked(before.ModuleBase + value);
                _ = checked(resolved + (ulong)byteLength);
            }
            catch (OverflowException) { Contract.Fail("ADDRESS_OVERFLOW", "Address arithmetic overflowed."); }
        }
        // field_offset stays an offset: callers must independently validate their object and range.
        var after = ValidateRuntime();
        if (before != after) Contract.Fail("SOURCE_CHANGED", "Source changed during resolution.");
        return new(name, symbol.Kind, resolved, byteLength, symbol.Kind == LocationKind.FieldOffset);
    }

    public void ValidateRequiredFields(IEnumerable<string> requiredFields)
    {
        var names = Contract.CopyNames(requiredFields, "required fields");
        var before = ValidateRuntime();
        foreach (string name in names)
        {
            if (!profile.Fields.TryGetValue(name, out var field))
                Contract.Fail("FIELD_MISSING", $"Required field is absent: {name}");
            if (field!.Status != "located") Contract.Fail("FIELD_UNKNOWN", $"Required field is unknown: {name}");
            ValidateCheck(field.Name, field.RequiredGates);
            foreach (var requiredSymbol in field.RequiredSymbols) ValidateSymbol(GetSymbol(requiredSymbol));
        }
        var after = ValidateRuntime();
        if (before != after) Contract.Fail("SOURCE_CHANGED", "Source changed during field validation.");
    }

    private SymbolDefinition GetSymbol(string name)
    {
        Contract.Name(name, "symbol name");
        if (!profile.Symbols.TryGetValue(name, out var symbol))
            Contract.Fail("SYMBOL_MISSING", $"Symbol is absent: {name}");
        return symbol!;
    }

    private void ValidateSymbol(SymbolDefinition symbol)
    {
        if (symbol.Status != "located" || symbol.Value is null)
            Contract.Fail("SYMBOL_UNKNOWN", $"Symbol is unknown: {symbol.Name}");
        ValidateCheck(symbol.Name, symbol.RequiredGates);
    }

    private void ValidateCheck(string key, IReadOnlyList<string> gates)
    {
        if (!report.Checks.TryGetValue(key, out var check) || check.State != CheckState.Verified)
            Contract.Fail("CHECK_UNKNOWN", $"Runtime check is not Verified: {key}");
        var actual = new HashSet<string>(check!.Gates, StringComparer.Ordinal);
        if (gates.Any(g => !actual.Contains(g)))
            Contract.Fail("GATE_MISSING", $"Required runtime gates are missing: {key}");
    }

    private SourceIdentity ValidateRuntime()
    {
        var now = DateTimeOffset.UtcNow;
        if (report.CompletedAtUtc > now || now - report.CompletedAtUtc > maxReportAge)
            Contract.Fail("REPORT_EXPIRED", "Runtime report is expired or has a future timestamp.");
        if (!Contract.SameHash(profile.Sha256, report.ProfileSha256))
            Contract.Fail("PROFILE_REPORT_HASH", "Runtime report belongs to a different profile.");
        if (report.SourceBefore != report.SourceAfter)
            Contract.Fail("SOURCE_CHANGED", "Validation source was not stable.");
        SourceIdentity current = sourceGetter();
        Contract.ValidateSource(current);
        if (current != report.SourceAfter)
            Contract.Fail("SOURCE_CHANGED", "Current process/module identity differs from the report.");
        var expected = profile.Identity;
        if (!Contract.SameHash(expected.Sha256, current.DiskSha256)
            || expected.FileLength != current.DiskFileLength || expected.PeOffset != current.DiskPeOffset
            || expected.PeTimestamp != current.PeTimestamp || expected.ImageSize != current.ImageSize
            || expected.Machine != current.Machine)
            Contract.Fail("BUILD_IDENTITY", "Current LC build differs from the profile identity.");
        return current;
    }
}

public static class ProfileLoader
{
    public const int MaximumJsonBytes = 2 * 1024 * 1024;

    public static LocationProfile LoadVerifiedHash(string path, string expectedSha256)
    {
        var data = Contract.ReadBounded(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!Contract.SameHash(actualHash, expectedSha256))
            Contract.Fail("PROFILE_HASH", "Profile file SHA256 does not match the catalog.");
        using var doc = Contract.Parse(data);
        var root = Contract.Object(doc.RootElement, "profile");
        Contract.Exact(Contract.String(root, "schema"), "lc-location-profile-v1", "profile schema");
        if (Contract.Int(root, "revision") != 1) Contract.Fail("REVISION", "Unsupported profile revision.");
        string id = Contract.Name(Contract.String(root, "id"), "profile id");
        string status = Contract.Choice(Contract.String(root, "status"), "candidate", "exported");
        var identity = Contract.Identity(Contract.Property(root, "identity"));
        var rawSymbols = Contract.Object(Contract.Property(root, "symbols"), "symbols");
        var symbols = new Dictionary<string, SymbolDefinition>(StringComparer.Ordinal);
        foreach (var item in rawSymbols.EnumerateObject())
        {
            if (symbols.Count >= Contract.MaxItems) Contract.Fail("ITEM_LIMIT", "Too many symbols.");
            string name = Contract.Name(item.Name, "symbol name");
            var value = Contract.Object(item.Value, "symbol");
            var kind = Contract.String(value, "kind") switch
            {
                "module_rva" => LocationKind.ModuleRva,
                "field_offset" => LocationKind.FieldOffset,
                "native_function_rva" => LocationKind.NativeFunctionRva,
                _ => throw new LocationContractException("LOCATION_KIND", "Unsupported location kind.")
            };
            string symbolStatus = Contract.Choice(Contract.String(value, "status"), "located", "unknown");
            if (kind == LocationKind.NativeFunctionRva &&
                (!value.TryGetProperty("callable", out var callable) || callable.ValueKind != JsonValueKind.False))
                Contract.Fail("NATIVE_CALLABLE", "Native function definitions must explicitly declare callable=false.");
            var rawValue = Contract.Property(value, "value");
            int? position = rawValue.ValueKind == JsonValueKind.Null ? null : Contract.NonnegativeInt(rawValue, "symbol value");
            if ((symbolStatus == "located") != position.HasValue)
                Contract.Fail("SYMBOL_STATUS", "Located symbols need a value; unknown symbols must use null.");
            if (kind != LocationKind.FieldOffset && position.HasValue && (uint)position.Value >= identity.ImageSize)
                Contract.Fail("RVA_RANGE", "Symbol RVA is outside the declared image.");
            symbols.Add(name, new(name, kind, position, symbolStatus, Contract.Names(value, "requiredGates")));
        }
        var rawFields = Contract.Object(Contract.Property(root, "fields"), "fields");
        var fields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var item in rawFields.EnumerateObject())
        {
            if (fields.Count >= Contract.MaxItems) Contract.Fail("ITEM_LIMIT", "Too many fields.");
            string name = Contract.Name(item.Name, "field name");
            if (symbols.ContainsKey(name)) Contract.Fail("CHECK_KEY_COLLISION", "Symbol and field check keys must be distinct.");
            var value = Contract.Object(item.Value, "field");
            string fieldStatus = Contract.Choice(Contract.String(value, "status"), "located", "unknown");
            var required = Contract.Names(value, "requiredSymbols");
            if (required.Any(s => !symbols.ContainsKey(s)))
                Contract.Fail("SYMBOL_REFERENCE", "Field references an absent symbol.");
            if (fieldStatus == "located" && required.Any(s => symbols[s].Status != "located"))
                Contract.Fail("FIELD_STATUS", "Located field depends on an unknown symbol.");
            fields.Add(name, new(name, fieldStatus, required, Contract.Names(value, "requiredGates")));
        }
        return new(id, status, identity, actualHash,
            new ReadOnlyDictionary<string, SymbolDefinition>(symbols),
            new ReadOnlyDictionary<string, FieldDefinition>(fields));
    }

    public static LocationProfile LoadFromCatalog(string catalogPath, string profileId,
        string? trustedCatalogSha256 = null)
    {
        var catalog = LoadCatalog(catalogPath, trustedCatalogSha256);
        var entry = catalog.Profiles.SingleOrDefault(p => p.Id == profileId);
        if (entry is null) Contract.Fail("PROFILE_MISSING", "Profile id is absent from the catalog.");
        var root = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!);
        var file = Path.GetFullPath(Path.Combine(root, entry!.File.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            Contract.Fail("CATALOG_PATH", "Profile path escapes the catalog directory.");
        var profile = LoadVerifiedHash(file, entry.Sha256);
        if (profile.Id != entry.Id || profile.Identity != entry.Identity)
            Contract.Fail("CATALOG_IDENTITY", "Profile identity differs from its catalog entry.");
        return profile;
    }

    public static LocationCatalog LoadCatalog(string catalogPath, string? trustedCatalogSha256 = null)
    {
        var data = Contract.ReadBounded(catalogPath);
        if (trustedCatalogSha256 is not null &&
            !Contract.SameHash(Convert.ToHexString(SHA256.HashData(data)), trustedCatalogSha256))
            Contract.Fail("CATALOG_HASH", "Catalog SHA256 differs from the trusted hash.");
        using var doc = Contract.Parse(data);
        var root = Contract.Object(doc.RootElement, "catalog");
        Contract.Exact(Contract.String(root, "schema"), "lc-location-catalog-v1", "catalog schema");
        var array = Contract.Property(root, "profiles");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > Contract.MaxItems)
            Contract.Fail("CATALOG_PROFILES", "Invalid catalog profile collection.");
        var entries = new List<CatalogEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            var value = Contract.Object(item, "catalog entry");
            string id = Contract.Name(Contract.String(value, "id"), "profile id");
            string file = Contract.String(value, "file");
            var parts = file.Split('/');
            if (parts.Length < 2 || parts[0] != "profiles" || file.Contains('\\')
                || file.Contains(':') || parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or "..")
                || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || !ids.Add(id) || !files.Add(file))
                Contract.Fail("CATALOG_PATH", "Catalog profile paths must be unique relative profiles/*.json paths.");
            entries.Add(new(file, Contract.Hash(Contract.String(value, "sha256")), id,
                Contract.Identity(Contract.Property(value, "identity"))));
        }
        return new(entries.AsReadOnly());
    }
}

internal static class Contract
{
    internal const int MaxItems = 4096;
    internal static void Fail(string code, string message) => throw new LocationContractException(code, message);
    internal static string Name(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            Fail("NAME", $"Invalid {label}.");
        return value;
    }
    internal static string Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            Fail("HASH_FORMAT", "SHA256 must contain exactly 64 hexadecimal characters.");
        return value!.ToLowerInvariant();
    }
    internal static bool SameHash(string a, string b) => CryptographicOperations.FixedTimeEquals(
        Convert.FromHexString(Hash(a)), Convert.FromHexString(Hash(b)));
    internal static IReadOnlyList<string> CopyNames(IEnumerable<string> names, string label)
    {
        if (names is null) Fail("NAMES", $"Missing {label}.");
        var copy = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names!)
        {
            if (copy.Count >= MaxItems || !unique.Add(Name(name, label)))
                Fail("NAMES", $"Too many or duplicate {label}.");
            copy.Add(name);
        }
        return copy.AsReadOnly();
    }
    internal static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > ProfileLoader.MaximumJsonBytes)
            Fail("JSON_SIZE", "JSON must be nonempty and no larger than 2 MiB.");
        byte[] data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        if (stream.ReadByte() != -1) Fail("JSON_SIZE", "JSON grew during loading.");
        return data;
    }
    internal static JsonDocument Parse(byte[] data)
    {
        try
        {
            var reader = new Utf8JsonReader(data, new JsonReaderOptions { MaxDepth = 48 });
            var objects = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.StartArray) objects.Push(null);
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string key = reader.GetString()!;
                    if (key.Length > 256 || objects.Count == 0 || objects.Peek() is null
                        || !objects.Peek()!.Add(key)) Fail("DUPLICATE_KEY", "Invalid or duplicate JSON property.");
                }
            }
            return JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 48 });
        }
        catch (JsonException ex) { throw new LocationContractException("JSON_FORMAT", ex.Message); }
    }
    internal static JsonElement Property(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var value)) Fail("PROPERTY_MISSING", $"Missing JSON property: {key}");
        return value;
    }
    internal static JsonElement Object(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object) Fail("JSON_TYPE", $"{label} must be an object.");
        return element;
    }
    internal static string String(JsonElement parent, string key)
    {
        var value = Property(parent, key);
        if (value.ValueKind != JsonValueKind.String) Fail("JSON_TYPE", $"{key} must be a string.");
        return value.GetString()!;
    }
    internal static int Int(JsonElement parent, string key) => NonnegativeInt(Property(parent, key), key);
    internal static int NonnegativeInt(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 0)
            Fail("INTEGER", $"{label} must be a nonnegative Int32 integer.");
        return value.GetInt32();
    }
    internal static uint UInt(JsonElement parent, string key)
    {
        var value = Property(parent, key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out uint number))
            Fail("INTEGER", $"{key} must be a UInt32 integer.");
        return value.GetUInt32();
    }
    internal static string Choice(string value, string a, string b)
    {
        if (value != a && value != b) Fail("STATUS", $"Expected {a} or {b}.");
        return value;
    }
    internal static void Exact(string value, string expected, string label)
    {
        if (value != expected) Fail("SCHEMA", $"Unsupported {label}.");
    }
    internal static IReadOnlyList<string> Names(JsonElement parent, string key)
    {
        var value = Property(parent, key);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxItems)
            Fail("NAMES", $"{key} must be a bounded array.");
        var names = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) Fail("NAMES", $"{key} items must be strings.");
            names.Add(item.GetString()!);
        }
        return CopyNames(names, key);
    }
    internal static BuildIdentity Identity(JsonElement value)
    {
        Object(value, "identity");
        var length = Property(value, "fileLength");
        if (length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out long fileLength) || fileLength <= 0)
            Fail("IDENTITY", "fileLength must be a positive Int64 integer.");
        int peOffset = Int(value, "peOffset");
        uint imageSize = UInt(value, "imageSize");
        uint machine = UInt(value, "machine");
        if (machine != 34404 || imageSize == 0 || peOffset < 64 || peOffset >= length.GetInt64())
            Fail("IDENTITY", "Invalid x64 PE identity.");
        return new(Hash(String(value, "sha256")), length.GetInt64(), peOffset,
            UInt(value, "peTimestamp"), imageSize, (ushort)machine);
    }
    internal static void ValidateSource(SourceIdentity source)
    {
        if (source is null || source.Pid <= 0 || source.CreationTimeFileTime <= 0
            || source.ModuleBase == 0 || source.ImageSize == 0 || source.Machine != 34404
            || source.DiskFileLength <= 0 || source.DiskPeOffset < 64
            || source.DiskPeOffset >= source.DiskFileLength || source.DiskRelation != "Matched")
            Fail("SOURCE_IDENTITY", "Runtime source must contain a complete matched x64 identity.");
        Name(source!.ProcessPath, "process path");
        if (!Path.IsPathFullyQualified(source.ProcessPath)) Fail("SOURCE_PATH", "Process path must be absolute.");
        Hash(source.DiskSha256);
        Hash(source.LoadedFingerprint);
    }
}
