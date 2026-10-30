namespace MemoryHub;

public sealed record ItemKindCodeSpan(uint Rva, string Hex);
public sealed record ItemKindPrefixDescriptor(uint DescriptorRva, uint TextRva, ulong Length);
public sealed record PersonalShopItemCodeContract(string Sha256, uint FormatterRva, uint PdataRva,
    IReadOnlyList<ItemKindCodeSpan> CodeSpans, IReadOnlyList<ItemKindPrefixDescriptor> PrefixDescriptors,
    string CodeEvidenceSha256, string HistoricalSemanticEvidenceSha256, string EvidenceDescription,
    bool IdentificationSemanticsVerified)
{
    public string SemanticEvidenceMode => Sha256 switch
    {
        PersonalShopItemCodeContracts.CurrentBuild => "CurrentCodeEquivalenceWithHistoricalHumanCalibration",
        PersonalShopItemCodeContracts.Current79D1 => "CurrentCodeEquivalenceWithHistoricalHumanCalibration",
        PersonalShopItemCodeContracts.CurrentB47C => "CurrentCodeEquivalenceWithHistoricalHumanCalibration",
        "FEDFC89D776E011C0F1D57FD2235413BA2A857AD1E51CADA08BB9209253A7B6A" => "RetainedSameBuildHumanCalibration",
        _ => "Unverified"
    };
    public string HumanCalibrationBuild => "FEDFC89D776E011C0F1D57FD2235413BA2A857AD1E51CADA08BB9209253A7B6A";
    public bool CurrentHumanCorroboration => false;
    public string SemanticBridgeSha256 => Sha256 switch
    {
        PersonalShopItemCodeContracts.CurrentBuild => "D8B235BE67A79F442868C9D849E2DE76BF2CBD464387549F1ED455135100A2C6",
        PersonalShopItemCodeContracts.Current79D1 => PersonalShopItemCodeContracts.Current79D1BridgeSha256,
        PersonalShopItemCodeContracts.CurrentB47C => PersonalShopItemCodeContracts.CurrentB47CBridgeSha256,
        _ => ""
    };
}

/// <summary>Exact build-specific proofs. Historical corroboration is never relabeled as a new human observation.</summary>
public static class PersonalShopItemCodeContracts
{
    public const string CurrentBuild = "38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1";
    public const string Current79D1 = "79D1F5C1A05BFA05C2B394B8F981E1AEC3DB1BBDC540A945D838F1ED2BCC27E9";
    public const string Current79D1BridgeSha256 = "1CC0258442E24E5FCFD8B00063E795B25D3163742230B185CE1C7C7ACD00B6B1";
    public const string CurrentB47C = "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC";
    public const string CurrentB47CBridgeSha256 = "DCBEA8854306CA0CB5FCF8DB938B31005DF36E41D1297A9B0416B2EEEACFFE55";
    public const string HistoricalSemanticEvidenceSha256 = "A40D6F4735A88240A3641FC83FEE925BD81977F292D7067987AD0FB5B6A191BF";

    private static readonly PersonalShopItemCodeContract Fed = new(
        "FEDFC89D776E011C0F1D57FD2235413BA2A857AD1E51CADA08BB9209253A7B6A", 0x512E40, 0x3E65DE8,
        Array.AsReadOnly(new[] {
            new ItemKindCodeSpan(0x512E40, "48895c24084889542410574883ec30488bda488bf933c089442420c5f9efc0c5f811024889421048c742180f0000008802c744242001000000488d05289fce00488d15714ecf0080794c00480f45d04c8b4208488b12488bcbe8428db3ff807f4c00742f8b475483f801742785c07509488d15ed9ece00eb0c83f8027515488d15574ecf0041b803000000488bcbe80d8db3ff488bc3488b5c24404883c4305fc3cc"),
            new ItemKindCodeSpan(0x3E65DE8, "402e5100e22e5100f4324d01"),
            new ItemKindCodeSpan(0x11FCDA0, "5c663e"), new ItemKindCodeSpan(0x1207D18, "5c6637"),
            new ItemKindCodeSpan(0x11FCDA4, "5c6648"), new ItemKindCodeSpan(0x1207D1C, "5c6641") }),
        Array.AsReadOnly(new[] { new ItemKindPrefixDescriptor(0x11FCDA8, 0x11FCDA0, 3), new ItemKindPrefixDescriptor(0x1207CF8, 0x1207D18, 3) }),
        "", HistoricalSemanticEvidenceSha256,
        "FED current code and retained human inventory corroboration", true);

    private static readonly PersonalShopItemCodeContract Retained38A7 = new(
        CurrentBuild, 0x51C2F0, 0x3EA7484,
        Array.AsReadOnly(new[] {
            new ItemKindCodeSpan(0x51C2F0, "48895c24084889542410574883ec30488bda488bf933c089442420c5f9efc0c5f811024889421048c742180f0000008802c744242001000000488d0520f8cf00488d15b9aad00080794c00480f45d04c8b4208488b12488bcbe8b2f9b2ff807f4c00742f8b475483f801742785c07509488d15e5f7cf00eb0c83f8027515488d1573aad00041b803000000488bcbe87df9b2ff488bc3488b5c24404883c4305fc3cc"),
            new ItemKindCodeSpan(0x3EA7484, "f0c2510092c35100047a4f01"),
            new ItemKindCodeSpan(0x121BB48, "5c663e"), new ItemKindCodeSpan(0x1226E00, "5c6637"),
            new ItemKindCodeSpan(0x121BB4C, "5c6648"), new ItemKindCodeSpan(0x1226DE8, "5c6641") }),
        Array.AsReadOnly(new[] { new ItemKindPrefixDescriptor(0x121BB50, 0x121BB48, 3), new ItemKindPrefixDescriptor(0x1226DF0, 0x1226E00, 3) }),
        "CBA6D583D2165498013AF75181C8C4E96FA5015450E20FA36CED36093D66BFD2", HistoricalSemanticEvidenceSha256,
        "38A7 exact current formatter and relocated descriptors; retained FED semantics through code equivalence; no new human observation", true);

    private static readonly PersonalShopItemCodeContract Current79D1Contract = new(
        Current79D1, 0x51D140, 0x3EBC58C,
        Array.AsReadOnly(new[] {
            new ItemKindCodeSpan(0x51D140, "48895c24084889542410574883ec30488bda488bf933c089442420c5f9efc0c5f811024889421048c742180f0000008802c744242001000000488d0500a0d000488d152151d10080794c00480f45d04c8b4208488b12488bcbe852edb2ff807f4c00742f8b475483f801742785c07509488d15c19fd000eb0c83f8027515488d15b750d10041b803000000488bcbe81dedb2ff488bc3488b5c24404883c4305fc3cc"),
            new ItemKindCodeSpan(0x3EBC58C, "40d15100e2d15100c84f5001"),
            new ItemKindCodeSpan(0x122717C, "5c663e"), new ItemKindCodeSpan(0x12322A0, "5c6637"),
            new ItemKindCodeSpan(0x1227178, "5c6648"), new ItemKindCodeSpan(0x123227C, "5c6641") }),
        Array.AsReadOnly(new[] { new ItemKindPrefixDescriptor(0x1227180, 0x122717C, 3), new ItemKindPrefixDescriptor(0x12322A8, 0x12322A0, 3) }),
        "13AF94FEB4A969E765D7B3D30D767A43D59FB57C1CC844077F05FE6047394734", HistoricalSemanticEvidenceSha256,
        "79D1 exact complete formatter, PDATA and role literals; retained FED semantics through the reviewed three-build bridge; no current human observation", true);

    // B47C is an independent exact-readable contract. The retained 38A7
    // NOACCESS certificate and all historical contracts remain unchanged.
    private static readonly PersonalShopItemCodeContract CurrentB47CContract = new(
        CurrentB47C, 0x524130, 0x3ECDB2C,
        Array.AsReadOnly(new[] {
            new ItemKindCodeSpan(0x524130, "48895c24084889542410574883ec30488bda488bf933c089442420c5f9efc0c5f811024889421048c742180f0000008802c744242001000000488d05d8c2d000488d159975d10080794c00480f45d04c8b4208488b12488bcbe8e27db2ff807f4c00742f8b475483f801742785c07509488d15b1c2d000eb0c83f8027515488d152b75d10041b803000000488bcbe8ad7db2ff488bc3488b5c24404883c4305fc3cc"),
            new ItemKindCodeSpan(0x3ECDB2C, "30415200d241520064fc5001"),
            new ItemKindCodeSpan(0x123045C, "5c663e"),
            new ItemKindCodeSpan(0x123B708, "5c6637"),
            new ItemKindCodeSpan(0x1230458, "5c6648"),
            new ItemKindCodeSpan(0x123B6E0, "5c6641") }),
        Array.AsReadOnly(new[] { new ItemKindPrefixDescriptor(0x1230448, 0x123045C, 3), new ItemKindPrefixDescriptor(0x123B710, 0x123B708, 3) }),
        "C9A63D1288EC22BF484A0AA733E99ECCC7683A9C38B9EF349F9E51A16D745F93", HistoricalSemanticEvidenceSha256,
        "B47C complete current formatter, PDATA and four role literals; retained FED calibration through independently reviewed FED/79D1/B47C code equivalence; no current human corroboration", true);

    public static PersonalShopItemCodeContract? ForBuild(string? sha256) =>
        string.Equals(sha256, Fed.Sha256, StringComparison.OrdinalIgnoreCase) ? Fed
        : string.Equals(sha256, Retained38A7.Sha256, StringComparison.OrdinalIgnoreCase) ? Retained38A7
        : string.Equals(sha256, Current79D1Contract.Sha256, StringComparison.OrdinalIgnoreCase) ? Current79D1Contract
        : string.Equals(sha256, CurrentB47CContract.Sha256, StringComparison.OrdinalIgnoreCase) ? CurrentB47CContract : null;
}
