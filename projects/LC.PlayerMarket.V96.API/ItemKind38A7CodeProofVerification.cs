using System.Buffers.Binary;
using MemoryHub.Reader;

namespace LcPlayerMarket;

internal static partial class ItemKind38A7Verification
{
    // Independent fixed fixtures: current capture-v2 SHA CBA6D583D2165498013AF75181C8C4E96FA5015450E20FA36CED36093D66BFD2,
    // retained FEDFC full formatter/PDATA/descriptor contract. Never generate expected bytes from the production contract.
    private static partial void AddCodeProofChecks(List<OfflineCheck> checks)
    {
        foreach (string sha in new[] { Build, FedBuild })
        {
            string buildName = sha[..8];
            foreach (ulong imageBase in new[] { 0x140000000UL, 0x7FF7D8DF0000UL })
                Check(checks, $"code_{buildName}_complete_relocated_contract_{imageBase:X}", () =>
                {
                    var fixture = CodeFixture(sha, imageBase); var seen = new HashSet<ulong>();
                    Need(PersonalShopItemKindCodeVerifier.Verify(sha, imageBase, (address, size) =>
                    {
                        Need(fixture.TryGetValue(address, out var bytes) && bytes.Length == size, "Verifier requested outside fixed complete proof reads.");
                        Need(seen.Add(address), "Verifier repeated a read instead of keeping bounded independent segments.");
                        return bytes!.ToArray();
                    }), "Complete exact formatter/PDATA/descriptors/strings were rejected.");
                    Need(seen.SetEquals(fixture.Keys), "Verifier skipped one of the complete code or data proofs.");
                    Need(PersonalShopItemKindCodeVerifier.Verify(sha.ToLowerInvariant(), imageBase,
                        (address, size) => fixture[address].ToArray()), "Correct build casing is rejected by code verifier.");
                });
            Check(checks, $"code_{buildName}_every_proof_byte_is_required", () =>
            {
                const ulong imageBase = 0x140000000;
                var fixture = CodeFixture(sha, imageBase);
                foreach (var segment in fixture)
                    for (int index = 0; index < segment.Value.Length; index++)
                    {
                        int changed = index;
                        bool accepted = PersonalShopItemKindCodeVerifier.Verify(sha, imageBase, (address, size) =>
                        {
                            var bytes = fixture[address].ToArray();
                            if (address == segment.Key) bytes[changed] ^= 1;
                            return bytes;
                        });
                        Need(!accepted, $"Changed byte at RVA 0x{segment.Key - imageBase:X}+{index} was accepted.");
                    }
            });
            Check(checks, $"code_{buildName}_missing_short_and_overlong_read_rejected", () =>
            {
                const ulong imageBase = 0x140000000;
                var fixture = CodeFixture(sha, imageBase);
                foreach (ulong missing in fixture.Keys)
                    foreach (int mode in new[] { 0, 1, 2 })
                    {
                        int calls = 0;
                        bool accepted = PersonalShopItemKindCodeVerifier.Verify(sha, imageBase, (address, size) =>
                        {
                            calls++; var bytes = fixture[address];
                            if (address != missing) return bytes.ToArray();
                            return mode switch { 0 => null!, 1 => bytes[..^1], _ => bytes.Concat(new byte[] { 0 }).ToArray() };
                        });
                        Need(!accepted && calls <= fixture.Count, "Missing/truncated/overlong proof accepted or reads became unbounded.");
                    }
            });
            Check(checks, $"code_{buildName}_descriptor_must_match_current_base_and_length3", () =>
            {
                const ulong imageBase = 0x140000000;
                var fixture = CodeFixture(sha, imageBase);
                foreach (ulong descriptor in fixture.Where(pair => pair.Value.Length == 16).Select(pair => pair.Key))
                {
                    foreach (ulong length in new[] { 0UL, 2UL, 4UL, ulong.MaxValue })
                        Need(!PersonalShopItemKindCodeVerifier.Verify(sha, imageBase, (address, size) =>
                        {
                            var bytes = fixture[address].ToArray();
                            if (address == descriptor) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), length);
                            return bytes;
                        }), "Formatter descriptor ignored its exact three-byte length.");
                    Need(!PersonalShopItemKindCodeVerifier.Verify(sha, imageBase, (address, size) =>
                    {
                        var bytes = fixture[address].ToArray();
                        if (address == descriptor) BinaryPrimitives.WriteUInt64LittleEndian(bytes,
                            BinaryPrimitives.ReadUInt64LittleEndian(bytes) + 0x100000);
                        return bytes;
                    }), "Descriptor from another image base was accepted.");
                }
            });
        }
        Check(checks, "code_result_drives_slot_row_and_presentation_without_boolean_bypass", () =>
        {
            const ulong imageBase = 0x140000000;
            var fixture = CodeFixture(Build, imageBase);
            foreach (bool corrupt in new[] { false, true })
            {
                bool proof = PersonalShopItemKindCodeVerifier.Verify(Build, imageBase, (address, size) =>
                {
                    var bytes = fixture[address].ToArray();
                    if (corrupt && address == imageBase + 0x51C2F0) bytes[0] ^= 1;
                    return bytes;
                });
                foreach (int raw in new[] { 0, 1, 2 })
                {
                    var row = MemoryHub.PersonalShopBankRules.Match(Listing(1, raw, 1), Build, proof, proof).Rows.Single();
                    if (corrupt)
                        Need(row.ItemKind is null && row.ItemIdentificationState == "Unknown" && MarketItemStatePresentation.EffectiveKind(row) == "Unknown",
                            "Rejected actual code fixture was bypassed by decoder, bank join or display.");
                    else
                        Need(row.ItemKind == (raw switch { 0 => "Blessed", 1 => "Normal", _ => "Cursed" }) && row.ItemIdentificationState == "Identified"
                            && MarketItemStatePresentation.Display(row).EndsWith("（已鑑定）", StringComparison.Ordinal),
                            "Verified actual code fixture did not reach slot, row and display.");
                }
            }
        });
        Check(checks, "code_unknown_build_and_overflow_do_not_read", () =>
        {
            foreach (string? sha in new[] { null, "", OldBuild, new string('A', 64), Build[..63] })
            {
                int reads = 0;
                Need(!PersonalShopItemKindCodeVerifier.Verify(sha!, 0x140000000, (_, _) => { reads++; return []; }) && reads == 0,
                    "Unknown build caused code reads or was authorized.");
            }
            foreach (string sha in new[] { Build, FedBuild })
            {
                int reads = 0;
                Need(!PersonalShopItemKindCodeVerifier.Verify(sha, ulong.MaxValue, (_, _) => { reads++; return []; }) && reads == 0,
                    "Address overflow caused reads or authorization.");
            }
        });
        Check(checks, "code_delegate_cancellation_and_read_failure_are_not_swallowed", () =>
        {
            foreach (Exception expected in new Exception[] { new OperationCanceledException("synthetic-cancel"), new IOException("synthetic-read-budget") })
            {
                Exception? actual = null;
                try { PersonalShopItemKindCodeVerifier.Verify(Build, 0x140000000, (_, _) => throw expected); }
                catch (Exception error) { actual = error; }
                Need(ReferenceEquals(actual, expected), "Code verifier swallowed/wrapped cancellation or reader failure.");
            }
        });
    }

    private static Dictionary<ulong, byte[]> CodeFixture(string sha, ulong imageBase)
    {
        bool current = sha == Build;
        uint body = current ? 0x51C2F0U : 0x512E40U, pdata = current ? 0x3EA7484U : 0x3E65DE8U;
        uint firstDescriptor = current ? 0x121BB50U : 0x11FCDA8U, secondDescriptor = current ? 0x1226DF0U : 0x1207CF8U;
        uint firstText = current ? 0x121BB48U : 0x11FCDA0U, secondText = current ? 0x1226E00U : 0x1207D18U;
        uint raw0Text = current ? 0x121BB4CU : 0x11FCDA4U, raw2Text = current ? 0x1226DE8U : 0x1207D1CU;
        const string currentHex = "48895c24084889542410574883ec30488bda488bf933c089442420c5f9efc0c5f811024889421048c742180f0000008802c744242001000000488d0520f8cf00488d15b9aad00080794c00480f45d04c8b4208488b12488bcbe8b2f9b2ff807f4c00742f8b475483f801742785c07509488d15e5f7cf00eb0c83f8027515488d1573aad00041b803000000488bcbe87df9b2ff488bc3488b5c24404883c4305fc3cc";
        const string fedHex = "48895c24084889542410574883ec30488bda488bf933c089442420c5f9efc0c5f811024889421048c742180f0000008802c744242001000000488d05289fce00488d15714ecf0080794c00480f45d04c8b4208488b12488bcbe8428db3ff807f4c00742f8b475483f801742785c07509488d15ed9ece00eb0c83f8027515488d15574ecf0041b803000000488bcbe80d8db3ff488bc3488b5c24404883c4305fc3cc";
        byte[] Descriptor(uint text)
        {
            var bytes = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, imageBase + text);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 3); return bytes;
        }
        return new() {
            [imageBase + body] = Convert.FromHexString(current ? currentHex : fedHex),
            [imageBase + pdata] = Convert.FromHexString(current ? "f0c2510092c35100047a4f01" : "402e5100e22e5100f4324d01"),
            [imageBase + firstDescriptor] = Descriptor(firstText), [imageBase + secondDescriptor] = Descriptor(secondText),
            [imageBase + firstText] = Convert.FromHexString("5c663e"), [imageBase + secondText] = Convert.FromHexString("5c6637"),
            [imageBase + raw0Text] = Convert.FromHexString("5c6648"), [imageBase + raw2Text] = Convert.FromHexString("5c6641") };
    }
}
