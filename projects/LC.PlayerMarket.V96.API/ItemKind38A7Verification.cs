using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Independent synthetic decoder, closed slot-row join, presentation and immutable-history regressions. Never reads LC.</summary>
internal static partial class ItemKind38A7Verification
{
    private const string Build = "38A7C2246901EA1A68849B0C000A23AC0D4B3DB599DA3B79F658B5258ADAD9D1";
    private const string FedBuild = "FEDFC89D776E011C0F1D57FD2235413BA2A857AD1E51CADA08BB9209253A7B6A";
    private const string OldBuild = "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF";
    private const string CurrentState = "CurrentBuildItemKindAccessorVerified";

    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        foreach (int bank in new[] { 0, 1 }) foreach (var (raw, kind, label) in new[] {
            (0, "Blessed", "受祝福"), (1, "Normal", "一般"), (2, "Cursed", "受詛咒") })
        {
            Check(checks, $"bank{bank}_raw{raw}_requires_code_proof_and_reaches_display", () =>
            {
                var noProof = PersonalShopItemKindRules.Decode(bank, raw, 1, Build, false);
                Need(noProof.Kind is null && noProof.State == "BuildSemanticsUnverified", "38A7 without closed code proof was promoted.");
                var proved = PersonalShopItemKindRules.Decode(bank, raw, 1, Build, true);
                Need(proved.Kind == kind && proved.State == CurrentState, "38A7 proved raw kind is unavailable or has wrong marker.");
                var source = Listing(bank, raw, 1);
                var unproved = PersonalShopBankRules.Match(source, Build, false, true).Rows.Single();
                Need(unproved.ItemKind is null && unproved.ItemIdentificationState == "Unknown", "Identification bool bypassed missing kind proof.");
                var row = PersonalShopBankRules.Match(source, Build, true, true).Rows.Single();
                Need(row.ItemKind == kind && row.ItemKindState == CurrentState && row.ItemKindRaw == raw && row.ItemKindDetailFlag == 1,
                    "Closed slot to UI row join lost or rewrote kind/raw/flag.");
                Need(row.ItemIdentificationState == "Identified" && row.ItemIdentificationEvidence == "Verified", "Separately proved flag1 was not identified.");
                Need(MarketItemStatePresentation.Display(row) == label + "（已鑑定）", "38A7 status was lost in display.");
                Need(MarketItemStatePresentation.MatchesFilter(row, "kind=" + kind), "38A7 status was lost in exact filter.");
                Need(source.SlotRows[bank * 8].ItemKind == "Normal" && source.Rows.Single().ItemKindRaw is null,
                    "Join mutated source evidence instead of returning new rows.");
            });
        }
        Check(checks, "historical_fed_and_9ffc_contracts_remain", () =>
        {
            foreach (int bank in new[] { 0, 1 }) foreach (int raw in new[] { 0, 1, 2 })
            {
                string kind = raw switch { 0 => "Blessed", 1 => "Normal", _ => "Cursed" };
                var fed = PersonalShopItemKindRules.Decode(bank, raw, 1, FedBuild, true);
                Need(fed.Kind == kind && fed.State == CurrentState, "FEDFC existing evidence contract changed.");
                Need(PersonalShopItemKindRules.Decode(bank, raw, 1, FedBuild, false).Kind is null, "FEDFC proof requirement was relaxed.");
                var old = PersonalShopItemKindRules.Decode(bank, raw, 1, OldBuild, false);
                Need(old.Kind == kind && old.State == "CurrentItemEntityUserCorroborated", "9FFC user-correlated historical contract changed.");
                Need(PersonalShopItemIdentificationRules.Decode(raw, 0, FedBuild, true) == ("Unidentified", "Verified"), "FEDFC identification compatibility changed.");
                Need(PersonalShopItemIdentificationRules.Decode(raw, 1, OldBuild, true) == ("Unknown", "Unverified"), "9FFC gained unverified identification semantics.");
            }
        });
        Check(checks, "unsupported_build_bank_flag_raw_rejected_even_with_proof", () =>
        {
            foreach (var (bank, raw, flag, sha) in new (int, int?, byte?, string?)[] {
                (-1, 1, 1, Build), (2, 1, 1, Build), (int.MaxValue, 1, 1, Build),
                (0, null, 1, Build), (0, -1, 1, Build), (1, 3, 1, Build), (1, int.MaxValue, 1, Build),
                (0, 1, null, Build), (1, 1, 0, Build), (0, 1, 2, Build), (1, 1, 255, Build),
                (0, 1, 1, null), (0, 1, 1, ""), (0, 1, 1, new string('A', 64)), (0, 1, 1, Build[..63]) })
                Need(PersonalShopItemKindRules.Decode(bank, raw, flag, sha, true).Kind is null, "Unsupported data was classified using proof bool alone.");
            Need(PersonalShopItemKindRules.Decode(0, 0, 1, Build.ToLowerInvariant(), true).Kind == "Blessed", "Valid SHA casing is rejected.");
        });
        Check(checks, "identification_requires_its_separate_evidence_and_preserves_raw", () =>
        {
            foreach (int bank in new[] { 0, 1 }) foreach (int raw in new[] { 0, 1, 2 }) foreach (byte flag in new byte[] { 0, 1 })
            {
                foreach (var (kindProof, idProof) in new[] { (false, false), (false, true), (true, false) })
                {
                    var row = PersonalShopBankRules.Match(Listing(bank, raw, flag), Build, kindProof, idProof).Rows.Single();
                    Need(row.ItemIdentificationState == "Unknown" && row.ItemIdentificationEvidence == "Unverified", "Missing separate evidence became identification.");
                }
                var proved = PersonalShopBankRules.Match(Listing(bank, raw, flag), Build, true, true).Rows.Single();
                Need(proved.ItemIdentificationState == (flag == 0 ? "Unidentified" : "Identified") && proved.ItemIdentificationEvidence == "Verified",
                    "Supported flag and closed evidence did not survive bank join.");
                Need(proved.ItemKindRaw == raw && proved.ItemKindDetailFlag == flag, "Identification changed raw state.");
                if (flag == 0)
                {
                    Need(proved.ItemKind is null && proved.ItemKindState == "Unknown", "Unidentified flag fabricated true kind.");
                    Need(MarketItemStatePresentation.Display(proved) == "未知（未鑑定）", "Verified unidentified display changed.");
                    Need(MarketItemStatePresentation.EffectiveKind(proved) == "Normal" && MarketItemStatePresentation.EffectiveKindReason(proved) == "UserRuleUnidentifiedAsNormal",
                        "Existing user comparison rule was changed.");
                }
            }
            foreach (var (raw, flag, sha) in new (int?, byte?, string?)[] {
                (null, 0, Build), (-1, 1, Build), (3, 1, Build), (1, null, Build), (1, 2, Build), (1, 255, Build),
                (1, 0, null), (1, 1, new string('A', 64)), (1, 0, OldBuild) })
                Need(PersonalShopItemIdentificationRules.Decode(raw, flag, sha, true) == ("Unknown", "Unverified"), "Unsupported identification input was promoted.");
        });
        Check(checks, "broken_bank_closure_clears_prior_labels_and_identification", () =>
        {
            var valid = Listing(0, 0, 1);
            var forgedRows = valid.Rows.Select(row => row with { ItemKindRaw = 0, ItemKindDetailFlag = 1, ItemKind = "Blessed", ItemKindState = CurrentState,
                ItemIdentificationState = "Identified", ItemIdentificationEvidence = "Verified" }).ToArray();
            var badPrice = valid.SlotRows.ToArray(); badPrice[0] = badPrice[0] with { PriceCandidate = 101 };
            var duplicate = valid.SlotRows.ToArray(); duplicate[^1] = duplicate[0];
            foreach (var broken in new[] { valid with { Complete = false }, valid with { BankState = "Unknown" },
                valid with { SlotRows = badPrice }, valid with { SlotRows = duplicate } })
            {
                var row = PersonalShopBankRules.Match(broken with { Rows = forgedRows }, Build, true, true).Rows.Single();
                Need(row.ItemKindRaw is null && row.ItemKindDetailFlag is null && row.ItemKind is null && row.ItemKindState == "Unknown"
                    && row.ItemIdentificationState == "Unknown" && row.ItemIdentificationEvidence == "Unverified", "Broken bank reused a previous page's state.");
            }
        });
        Check(checks, "history_with_unverified_38a7_evidence_is_not_retrofitted", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "LC-Market77-kind-history-" + Guid.NewGuid().ToString("N"));
            var store = new LocalStore(root);
            var originalRow = Row(0, 1, "Blessed", "BuildSemanticsUnverified");
            var page = new MarketListing { Revision = "retained-unverified-38a7", SourceSessionKey = "retained-" + Build, CapturedAt = DateTimeOffset.UnixEpoch,
                ViewerDirection = ShopPageModes.ViewerBuy, Owner = new() { Id = 7, Name = "舊店主", LocalIdentity = "old-owner" }, Rows = [originalRow] };
            Need(store.SaveListing(page), "Could not create isolated retained history fixture.");
            string file = Directory.GetFiles(store.HistoryDirectory, "*.json").Single(); string before = Hash(file);
            var loaded = new LocalStore(root).LoadHistory().Single(); var row = loaded.Rows.Single();
            Need(row == originalRow && loaded.CapturedAt == page.CapturedAt && loaded.SourceSessionKey == page.SourceSessionKey && Hash(file) == before,
                "Loading old history rewrote fields, source, acquisition time or immutable bytes.");
            Need(MarketItemStatePresentation.EffectiveKind(row) == "Unknown" && MarketItemStatePresentation.Display(row) == "未知（未鑑定未核實）",
                "New 38A7 support retroactively promoted an unverified stored row.");
            Need(PersonalShopItemKindRules.Decode(0, 0, 1, Build, true).Kind == "Blessed", "History isolation test requires new current decoder to support proved 38A7.");
        });
        AddCodeProofChecks(checks);
        return checks.ToArray();
    }

    private static partial void AddCodeProofChecks(List<OfflineCheck> checks);
    private static void Check(List<OfflineCheck> checks, string name, Action body)
    {
        try { body(); checks.Add(new("item_kind_38a7_" + name, true, "Synthetic offline fixtures only; LC reads=0; native calls=0; game input=0.")); }
        catch (Exception ex) { checks.Add(new("item_kind_38a7_" + name, false, ex.ToString())); }
    }
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static int RunReport(string output)
    {
        var checks = Run(); var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true);
        return report.Success ? 0 : 1;
    }
    private static PersonalShopRow Row(int? raw, byte? flag, string? kind, string state) =>
        new(0, "Observed", true, "同名道具", "100", 100, "金幣", 1, "Observed", "離線合成資料")
        { ItemKindRaw = raw, ItemKindDetailFlag = flag, ItemKind = kind, ItemKindState = state };
    private static PersonalShopListing Listing(int occupiedBank, int? raw, byte? flag)
    {
        var slots = (from bank in new[] { 0, 1 } from index in Enumerable.Range(0, 8)
            select new PersonalShopSlotRow { BankIndex = bank, RowIndex = index, State = "EmptyPair" }).ToArray();
        slots[occupiedBank * 8] = new() { BankIndex = occupiedBank, RowIndex = 0, State = "TypedPayloadCandidate", PairPresent = true,
            ControlVerified = true, PayloadVerified = true, RawPriceField20 = 100, PriceCandidate = 100, RawQuantityField28 = 1,
            QuantityCandidate = 1, PayloadQuantityCandidate = 1, QuantityMatchesPayload = true, RawDisplayTextCandidate = "同名道具",
            ItemKindRaw = raw, ItemKindDetailFlag = flag, ItemKind = "Normal", ItemKindState = "Unknown" };
        return new() { Complete = true, LayoutState = "Present", BankState = "Closed", BankFingerprint = "synthetic-closed",
            RawBankSummary = new[] { 0, 1 }.Select(bank => new PersonalShopBankSummary(bank, bank == 0 ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell,
                bank == 0 ? 2 : 1, bank == occupiedBank ? 1 : 0, bank == occupiedBank ? 1 : 0, "synthetic-bank")).ToArray(),
            SlotRows = slots, Rows = [Row(null, null, null, "Unknown") with { Quantity = null, QuantityState = "Unverified" }] };
    }
}
