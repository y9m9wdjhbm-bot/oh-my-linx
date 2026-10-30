using System.Security.Cryptography;

namespace MemoryHub;

/// <summary>Provenance for fresh typed data; never an authorization for native invocation.</summary>
public sealed record PersonalShopItemStateEvidence
{
    public string Mode { get; init; } = "Unknown";
    public string SourceBuildSha256 { get; init; } = "";
    public string CertificateSha256 { get; init; } = "";
    public bool CurrentRuntimeCodeVerified { get; init; }
    public bool CurrentMetadataVerified { get; init; }
}

public static class PersonalShopBuildCertificate
{
    public const string CertificateSha256 = "28093DBDB34096F744B10980E804195D720B5E2BB7958CD7C5D1D8B02BD4BCC8";
    public const string BuildSha256 = PersonalShopItemCodeContracts.CurrentBuild;
    public const string BuildCertifiedKindState = "BuildCertifiedItemKind";
    private static readonly Lazy<bool> valid = new(() =>
    {
        using var resource = typeof(PersonalShopBuildCertificate).Assembly.GetManifestResourceStream("MemoryHub.ItemStateCertificate38A7V1");
        if (resource is null || resource.Length is <= 0 or > 65536) return false;
        using var bytes = new MemoryStream(); resource.CopyTo(bytes); return VerifyBytes(bytes.ToArray());
    });
    public static bool IsValid => valid.Value;
    public static bool VerifyBytes(byte[] bytes) => bytes is { Length: > 0 and <= 65536 }
        && Convert.ToHexString(SHA256.HashData(bytes)) == CertificateSha256;
    public static bool Supports(string? build) => IsValid && string.Equals(build, BuildSha256, StringComparison.OrdinalIgnoreCase);
    public static PersonalShopItemStateEvidence Evidence() => new()
    { Mode = "BuildCertified", SourceBuildSha256 = BuildSha256, CertificateSha256 = CertificateSha256,
      CurrentRuntimeCodeVerified = false, CurrentMetadataVerified = true };
}

public static class PersonalShopItemEvidenceRules
{
    public static bool IsBuildCertified(PersonalShopRow row) => IsBuildCertified(row.ItemStateEvidence)
        && row.ItemKindRaw is 0 or 1 or 2 && row.ItemKindDetailFlag is 0 or 1;
    public static bool IsBuildCertified(PersonalShopItemStateEvidence? evidence) => evidence is
        { Mode: "BuildCertified", CurrentRuntimeCodeVerified: false, CurrentMetadataVerified: true }
        && evidence.SourceBuildSha256 == PersonalShopBuildCertificate.BuildSha256
        && evidence.CertificateSha256 == PersonalShopBuildCertificate.CertificateSha256
        && PersonalShopBuildCertificate.IsValid;
}
