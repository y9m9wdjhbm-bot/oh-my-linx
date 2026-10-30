namespace MemoryHub;

/// <summary>A source-bound readonly observation, never a lease authorizing a native request.</summary>
public static class ShopNativeColdPolicy
{
    public const string Id = "38A7-CreateCopy-NOACCESS-ColdCertified-v17";
    public const string CertificateSha256 = "6FBE2B8DEB6AB0082BCB5A5D3162E22DC0E2B141088D2C5957A6637965458FCE";
    public const uint MaximumMask = 3;
    public const string EvidenceMode = "BuildCertifiedColdPolicy";
}

public sealed record ShopNativeReadiness
{
    public bool BuildCompatible { get; init; }
    public bool Ready { get; init; }
    public string Code { get; init; } = "Unknown";
    public string Detail { get; init; } = "";
    public string? FailureRva { get; init; }
    public SourceIdentity? Source { get; init; }
    public DateTimeOffset SampledAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string ProbeId { get; init; } = "";
    public bool ReadOnly { get; init; } = true;
    public int NativeRequests { get; init; }
    public uint PendingMask { get; init; }
    public string EvidenceMode { get; init; } = "";
    public bool CurrentRuntimeCodeVerified { get; init; }
    public string ColdCodePolicyId { get; init; } = "";
    public string ColdCodeCertificateSha256 { get; init; } = "";
    private bool EvidenceValid => Source?.DiskSha256 == "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC"
        ? Source.PeTimestamp == 1793101316 && Source.ImageSize == 66609152 && Code == "NativeReady" && PendingMask == 0
            && CurrentRuntimeCodeVerified && EvidenceMode == "CurrentRuntimeCode"
            && ColdCodePolicyId == ShopNativeCurrentB47CPolicy.Id && ColdCodeCertificateSha256 == ShopNativeCurrentB47CPolicy.CertificateSha256
        : Source?.DiskSha256 == "79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9"
        ? Source.PeTimestamp == 1792498244 && Source.ImageSize == 66535424 && Code == "NativeReady" && PendingMask == 0
            && CurrentRuntimeCodeVerified && EvidenceMode == "CurrentRuntimeCode"
            && ColdCodePolicyId == ShopNativeCurrent79D1Policy.Id && ColdCodeCertificateSha256 == ShopNativeCurrent79D1Policy.CertificateSha256
        : ( Code == "NativeReady" && PendingMask == 0
        || Code == "NativeColdPolicyReady" && PendingMask is > 0 and <= ShopNativeColdPolicy.MaximumMask
            && !CurrentRuntimeCodeVerified && EvidenceMode == ShopNativeColdPolicy.EvidenceMode
            && ColdCodePolicyId == ShopNativeColdPolicy.Id && ColdCodeCertificateSha256 == ShopNativeColdPolicy.CertificateSha256
            && Source is { PeTimestamp: 0x6ACEDB9A, ImageSize: 0x3F5F000 }
            && string.Equals(Source.DiskSha256, "38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1", StringComparison.OrdinalIgnoreCase));
    public bool Fresh(DateTimeOffset now) => Ready && BuildCompatible && EvidenceValid
        && ReadOnly && NativeRequests == 0 && Source is not null && SampledAt != default
        && SampledAt <= now && now <= ExpiresAt
        && ExpiresAt - SampledAt == TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds);
}

/// <summary>Current build certificate; zero deferred entry, no cold invocation authority.</summary>
public static class ShopNativeCurrent79D1Policy
{
 public const string Id="79D1-CurrentReadableOnly-v17";
 public const string CertificateSha256="168C72F964C901EF31CEC2F156AF2F823E27B98C9BDB4B96502E12860186CBAC";
 public const uint MaximumMask=0;
 public const string EvidenceMode="CurrentRuntimeCode";
}

/// <summary>B47C strict policy; historical 79D1/38A7 evidence remains separate.</summary>
public static class ShopNativeCurrentB47CPolicy
{
 public const string Id="B47C-CurrentReadableOnly-v17";
 public const string CertificateSha256="80C9B9D2A7D31BAAD0F48FCCA5CC890241EE8742CFAA0090385B55E8D9B1C1E6";
 public const uint MaximumMask=0;
 public const string EvidenceMode="CurrentRuntimeCode";
}
