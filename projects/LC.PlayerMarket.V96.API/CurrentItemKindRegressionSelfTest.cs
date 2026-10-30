using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Uses the production decoder, bank join and presentation. All evidence is synthetic; never opens LC.</summary>
internal static class CurrentItemKindRegressionSelfTest
{
    internal const string CurrentBuild = "FEDFC89D776E011C0F1D57FD2235413BA2A857AD1E51CADA08BB9209253A7B6A";
    internal const string CurrentState = "CurrentBuildItemKindAccessorVerified";
    private const string OldBuild = "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF";
    private const string OldState = "CurrentItemEntityUserCorroborated";

    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action body)
        { try { body(); checks.Add(new(name, true, "離線合成證據；未讀取或操作 LC")); } catch (Exception e) { checks.Add(new(name, false, e.Message)); } }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        foreach (int bank in new[] { 0, 1 }) foreach (var (raw, kind, label) in new[] { (0, "Blessed", "受祝福"), (1, "Normal", "一般"), (2, "Cursed", "受詛咒") })
        {
            int b = bank, r = raw; string k = kind, l = label;
            Check($"current_build_bank{b}_raw{r}_requires_and_consumes_exact_code_proof", () =>
            {
                var unproved = Decode(b, r, 1, CurrentBuild, false);
                Need(unproved.Kind is null, "同 SHA 沒有當次程式碼證據仍被分類");
                var decoded = Decode(b, r, 1, CurrentBuild, true);
                Need(decoded.Kind == k && decoded.State == CurrentState, $"FEDFC 原始值已讀到但分類失敗：{decoded}");
                var row = Row(r, 1, decoded.Kind, decoded.State);
                Need(MarketItemStatePresentation.EffectiveKind(row) == k, "Reader 新證據標記未傳到呈現層");
                Need(MarketItemStatePresentation.EffectiveKindReason(row) == CurrentState, "呈現層丟失目前 build 證據理由");
                Need(MarketItemStatePresentation.Display(row) == l, "已知三態顯示仍混入未核實鑑定文字或中文錯誤");
                Need(MarketItemStatePresentation.MatchesFilter(row, "kind=" + k), "目前三態不能精準篩選");
                Need(MarketItemStatePresentation.MatchesFilter(row, l), "目前三態中文篩選不吻合");
            });
            Check($"current_build_bank{b}_raw{r}_survives_closed_page_join", () =>
            {
                var listing = Listing(b, r, 1);
                var unproved = Match(listing, CurrentBuild, false, false);
                Need(unproved.Rows.Single().ItemKind is null, "最終 bank join 忽略缺少 code proof");
                var joined = Match(listing, CurrentBuild, true, false);
                Need(joined.PageModeState == "MatchedExclusiveBankAndRows", "有效完整頁面未連到唯一方向 bank");
                var row = joined.Rows.Single();
                Need(row.ItemKind == k && row.ItemKindState == CurrentState && row.ItemKindRaw == r && row.ItemKindDetailFlag == 1,
                    "目前 build 的 raw/state 在 slot→row join 遺失");
                Need(row.ItemIdentificationState == "Unknown" && row.ItemIdentificationEvidence == "Unverified", "只有祝咒程式碼證據便猜成已鑑定");
            });
        }
        Check("retained_build_three_kinds_keep_original_evidence_marker", () =>
        {
            foreach (int bank in new[] { 0, 1 }) foreach (var (raw, kind) in new[] { (0, "Blessed"), (1, "Normal"), (2, "Cursed") })
            {
                var value = Decode(bank, raw, 1, OldBuild, false);
                Need(value.Kind == kind && value.State == OldState, "舊 build 校準樣本相容性遺失");
                Need(MarketItemStatePresentation.EffectiveKind(Row(raw, 1, value.Kind, value.State)) == kind, "舊 marker 不再支援");
            }
        });
        Check("proof_does_not_authorize_unknown_build_flag_bank_or_raw", () =>
        {
            foreach (var (bank, raw, flag, sha) in new (int, int?, byte?, string?)[] {
                (0, 1, 0, CurrentBuild), (1, 1, null, CurrentBuild), (1, null, 1, CurrentBuild),
                (1, 8, 1, CurrentBuild), (1, -1, 1, CurrentBuild), (2, 1, 1, CurrentBuild), (-1, 1, 1, CurrentBuild),
                (1, 1, 2, CurrentBuild), (1, 1, 255, CurrentBuild), (1, 1, 1, null), (1, 1, 1, new string('A', 64)) })
                Need(Decode(bank, raw, flag, sha, true).Kind is null, "proof bool 將未知來源或不支持原始值升格");
            Need(Decode(0, 0, 1, CurrentBuild.ToLowerInvariant(), true).State == CurrentState, "SHA 正確但大小寫不同被拒絕");
        });
        Check("identification_needs_separate_current_build_closed_evidence", () =>
        {
            foreach (int bank in new[] { 0, 1 }) foreach (int raw in new[] { 0, 1, 2 }) foreach (byte flag in new byte[] { 0, 1 })
            {
                var listing = Listing(bank, raw, flag);
                foreach (var (kindProof, idProof) in new[] { (false, false), (true, false), (false, true) })
                {
                    var row = Match(listing, CurrentBuild, kindProof, idProof).Rows.Single();
                    Need(row.ItemIdentificationState == "Unknown" && row.ItemIdentificationEvidence == "Unverified", "未閉合兩種證據就猜鑑定狀態");
                }
                var identified = Match(listing, CurrentBuild, true, true).Rows.Single();
                Need(identified.ItemIdentificationState == (flag == 1 ? "Identified" : "Unidentified")
                    && identified.ItemIdentificationEvidence == "Verified", "目前旗標與人類樣本對照未傳到商品列");
                Need(identified.ItemKindRaw == raw && identified.ItemKindDetailFlag == flag, "鑑定規則改寫了 raw");
                if (flag == 0)
                {
                    Need(identified.ItemKind is null && identified.ItemKindState == "Unknown", "未鑑定的真實祝咒被偽造一般");
                    Need(MarketItemStatePresentation.Display(identified) == "未知（未鑑定）", "已證未鑑定顯示錯誤");
                    Need(MarketItemStatePresentation.EffectiveKind(identified) == "Normal"
                        && MarketItemStatePresentation.EffectiveKindReason(identified) == "UserRuleUnidentifiedAsNormal", "未鑑定沒有依使用者規則參與一般分類");
                }
                else Need(MarketItemStatePresentation.Display(identified).EndsWith("（已鑑定）", StringComparison.Ordinal), "已證已鑑定沒有明示");
            }
        });
        Check("identification_never_upgrades_missing_unsupported_or_foreign_data", () =>
        {
            foreach (var (raw, flag, sha) in new (int?, byte?, string?)[] {
                (null, 0, CurrentBuild), (0, null, CurrentBuild), (3, 0, CurrentBuild), (-1, 1, CurrentBuild),
                (1, 2, CurrentBuild), (1, 255, CurrentBuild), (0, 0, null), (0, 0, OldBuild), (1, 1, new string('A', 64)) })
            {
                var row = Match(Listing(0, raw, flag), sha, true, true).Rows.Single();
                Need(row.ItemIdentificationState == "Unknown" && row.ItemIdentificationEvidence == "Unverified", "不支持欄位或舊/未知 build 被鑑定");
            }
        });
        Check("flag0_or_missing_stays_unknown_without_identification_proof", () =>
        {
            foreach (int raw in new[] { 0, 1, 2 })
            {
                var row = Row(raw, 0, "Normal", CurrentState);
                Need(MarketItemStatePresentation.EffectiveKind(row) == "Unknown", "flag0被當成已證未鑑定或一般");
                Need(MarketItemStatePresentation.Display(row) == "未知（未鑑定未核實）", "未證旗標0被顯示為已證未鑑定");
            }
            foreach (var row in new[] { Row(null, 1, "Blessed", CurrentState), Row(0, null, "Blessed", CurrentState), Row(4, 1, "Normal", CurrentState), Row(1, 7, "Normal", CurrentState) })
                Need(MarketItemStatePresentation.EffectiveKind(row with { ItemIdentificationState = "Unidentified", ItemIdentificationEvidence = "Verified" }) == "Unknown", "不完整 raw 藉鑑定字串繞過契約");
        });
        Check("verified_unidentified_display_and_price_classification_are_distinct", () =>
        {
            var unidentified = Row(2, 0, null, "Unknown") with { ItemIdentificationState = "Unidentified", ItemIdentificationEvidence = "Verified" };
            var normal = Row(1, 1, "Normal", CurrentState) with { ItemIdentificationState = "Identified", ItemIdentificationEvidence = "Verified" };
            Need(MarketItemStatePresentation.Display(unidentified) == "未知（未鑑定）", "顯示把實際未知祝咒洗成一般");
            Need(MarketItemStatePresentation.Display(normal) == "一般（已鑑定）", "已證一般/已鑑定顯示錯誤");
            Need(MarketItemStatePresentation.FilterKey(unidentified) == MarketItemStatePresentation.FilterKey(normal)
                && MarketItemStatePresentation.MatchesFilter(unidentified, "一般"), "一般與已證未鑑定無法共同分類");
            var normalView = View(normal);
            var unidentifiedView = View(unidentified);
            Need(MarketItemStatePresentation.ComparisonKey(normalView) == MarketItemStatePresentation.ComparisonKey(unidentifiedView), "一般與已證未鑑定比價鍵不同");
            var unknown = Row(1, 0, null, "Unknown");
            Need(MarketItemStatePresentation.ComparisonKey(View(unknown)).IsIsolated, "未核實 unknown 混入一般比價");
        });
        Check("unverified_current_marker_cannot_be_forged_by_old_item_label", () =>
        {
            var row = Row(0, 1, "Blessed", "BuildSemanticsUnverified");
            Need(MarketItemStatePresentation.EffectiveKind(row) == "Unknown", "舊 itemKind 字串繞過證據 marker");
        });
        return checks.ToArray();
    }

    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed),
            checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(Path.GetFullPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    // The same test source compiles with the retained four-argument V62 decoder.
    // Reflection changes only the call shape; expected production outcomes stay identical in red and green.
    private static (string? Kind, string State) Decode(int bank, int? raw, byte? flag, string? sha, bool proof)
    {
        var method = typeof(PersonalShopItemKindRules).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Decode");
        object?[] args = method.GetParameters().Length switch
        {
            6 => [bank, raw, flag, sha, proof, null],
            5 => [bank, raw, flag, sha, proof],
            4 => [bank, raw, flag, sha],
            _ => throw new InvalidOperationException("Unsupported production Decode signature")
        };
        return ((string? Kind, string State))method.Invoke(null, args)!;
    }
    private static PersonalShopListing Match(PersonalShopListing listing, string? sha, bool kindProof, bool idProof)
    {
        var method = typeof(PersonalShopBankRules).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "Match");
        object?[] args = method.GetParameters().Length switch
        {
            5 => [listing, sha, kindProof, idProof, null],
            4 => [listing, sha, kindProof, idProof],
            3 => [listing, sha, kindProof],
            2 => [listing, sha],
            _ => throw new InvalidOperationException("Unsupported production Match signature")
        };
        return (PersonalShopListing)method.Invoke(null, args)!;
    }
    private static PersonalShopRow Row(int? raw, byte? flag, string? kind, string state) =>
        new(0, "Observed", true, "同名道具", "100", 100, "金幣", 1, "Observed", "離線合成資料")
        { ItemKindRaw = raw, ItemKindDetailFlag = flag, ItemKind = kind, ItemKindState = state };
    private static MarketRowView View(PersonalShopRow row) => new(new MarketListing
    { ViewerDirection = ShopPageModes.ViewerBuy, CapturedAt = DateTimeOffset.UnixEpoch, Owner = new() { Id = 1, Name = "離線店主" }, Rows = [row] }, row, null, null, false);
    private static PersonalShopListing Listing(int occupiedBank, int? raw, byte? flag)
    {
        var slots = (from bank in new[] { 0, 1 } from index in Enumerable.Range(0, 8)
                     select new PersonalShopSlotRow { BankIndex = bank, RowIndex = index, State = "EmptyPair" }).ToArray();
        slots[occupiedBank * 8] = new() {
            BankIndex = occupiedBank, RowIndex = 0, State = "TypedPayloadCandidate", PairPresent = true,
            ControlVerified = true, PayloadVerified = true, RawPriceField20 = 100, PriceCandidate = 100,
            RawQuantityField28 = 1, QuantityCandidate = 1, PayloadQuantityCandidate = 1, QuantityMatchesPayload = true,
            RawDisplayTextCandidate = "同名道具", ItemKindRaw = raw, ItemKindDetailFlag = flag,
            ItemKind = "Normal", ItemKindState = "Unknown" };
        return new() {
            Complete = true, LayoutState = "Present", BankState = "Closed", BankFingerprint = "synthetic-closed",
            RawBankSummary = new[] { 0, 1 }.Select(bank => new PersonalShopBankSummary(bank,
                bank == 0 ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell, bank == 0 ? 2 : 1,
                bank == occupiedBank ? 1 : 0, bank == occupiedBank ? 1 : 0, "synthetic-bank")).ToArray(),
            SlotRows = slots, Rows = [Row(null, null, null, "Unknown") with { Quantity = null, QuantityState = "Unverified" }] };
    }
}
