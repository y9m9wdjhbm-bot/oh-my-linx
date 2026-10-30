namespace MemoryHub;

/// <summary>Version-scoped item-state semantics. Current-build values need exact closed accessor code evidence.</summary>
public static class PersonalShopItemKindRules
{
    public const string VerifiedBuild = "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF";
    public const string VerifiedState = "CurrentItemEntityUserCorroborated";
    public const string CurrentBuild = "FEDFC89D776E011C0F1D57FD2235413BA2A857AD1E51CADA08BB9209253A7B6A";
    public const string CurrentState = "CurrentBuildItemKindAccessorVerified";

    public static (string? Kind, string State) Decode(int bankIndex, int? raw, byte? detailFlag,
        string? sourceBuildSha256 = null, bool currentBuildCodeVerified = false, PersonalShopItemStateEvidence? evidence = null)
    {
        if (bankIndex is not (0 or 1) || detailFlag != 1 || raw is not (0 or 1 or 2)) return (null, "Unknown");
        string state;
        if (string.Equals(sourceBuildSha256, VerifiedBuild, StringComparison.OrdinalIgnoreCase)) state = VerifiedState;
        else if (PersonalShopItemCodeContracts.ForBuild(sourceBuildSha256) is not null)
        {
            if (!currentBuildCodeVerified)
            {
                if (sourceBuildSha256 == PersonalShopBuildCertificate.BuildSha256 && PersonalShopItemEvidenceRules.IsBuildCertified(evidence))
                    return (raw switch { 0 => "Blessed", 1 => "Normal", 2 => "Cursed", _ => throw new InvalidOperationException() }, PersonalShopBuildCertificate.BuildCertifiedKindState);
                return (null, "BuildSemanticsUnverified");
            }
            state = CurrentState;
        }
        else return (null, "BuildSemanticsUnverified");
        return (raw switch { 0 => "Blessed", 1 => "Normal", 2 => "Cursed", _ => throw new InvalidOperationException() }, state);
    }
}
