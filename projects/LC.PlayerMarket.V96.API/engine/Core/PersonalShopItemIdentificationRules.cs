namespace MemoryHub;

/// <summary>Identification requires closed current code and retained same-build calibration or an explicit current-code/historical-calibration bridge.</summary>
public static class PersonalShopItemIdentificationRules
{
    public static (string State, string Evidence) Decode(int? raw, byte? detailFlag, string? sourceBuildSha256,
        bool currentBuildEvidenceVerified = false, PersonalShopItemStateEvidence? evidence = null)
    {
        if (!currentBuildEvidenceVerified && sourceBuildSha256 == PersonalShopBuildCertificate.BuildSha256
            && raw is 0 or 1 or 2 && detailFlag is 0 or 1 && PersonalShopItemEvidenceRules.IsBuildCertified(evidence))
            return (detailFlag == 1 ? "Identified" : "Unidentified", "BuildCertified");
        if (!currentBuildEvidenceVerified || raw is not (0 or 1 or 2) || detailFlag is not (0 or 1)
            || PersonalShopItemCodeContracts.ForBuild(sourceBuildSha256) is not { IdentificationSemanticsVerified: true })
            return ("Unknown", "Unverified");
        return (detailFlag == 1 ? "Identified" : "Unidentified", "Verified");
    }
}
