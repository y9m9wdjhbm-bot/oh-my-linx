using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MemoryHub;

namespace LcPlayerMarket;

public sealed record ExcelExportCheck(string Name, bool Passed, string Detail);
public sealed record ExcelExportCheckReport(string Version, bool Success, int Passed, int Failed, string SamplePath,
    bool ConnectedToService, bool OpenedGameProcess, bool CreatedWindow, ExcelExportCheck[] Checks);

/// <summary>Offline only: synthesizes rows, writes a sample, and validates its ZIP/XML without constructing any Form.</summary>
public static class ExcelExportSelfTest
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static ExcelExportCheckReport Run(string samplePath)
    {
        var checks = new List<ExcelExportCheck>(); string sample = Path.GetFullPath(samplePath);
        string scratch = Path.Combine(Path.GetDirectoryName(sample)!, ".excel-export-selftest-" + Guid.NewGuid().ToString("N"));
        Dictionary<string, XDocument>? parts = null; bool createdScratch = false;
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new(name, true, "通過")); }
            catch (Exception ex) { checks.Add(new(name, false, ex.GetType().Name + ": " + ex.Message)); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        static void MustFail(Action action, string message)
        { try { action(); } catch { return; } throw new InvalidDataException(message); }
        XDocument Doc(string entry) => parts is not null && parts.TryGetValue(entry, out var document) ? document : throw new InvalidDataException("樣本尚未成功解析：" + entry);
        XElement Cell(string reference) => Doc("xl/worksheets/sheet1.xml").Descendants(Spreadsheet + "c").Single(c => (string?)c.Attribute("r") == reference);
        string Text(string reference) => string.Concat(Cell(reference).Descendants(Spreadsheet + "t").Select(e => e.Value));
        string Type(string reference) => (string?)Cell(reference).Attribute("t") ?? "n";
        string Numeric(string reference) => Cell(reference).Element(Spreadsheet + "v")?.Value ?? "";
        var acquired = new DateTimeOffset(2026, 9, 20, 3, 4, 5, TimeSpan.FromHours(8));
        const string Special = "=中文<&>\"'😀\u0001\uD800_x000A_";
        MarketRowView Make(int index, string name, ulong? price, ulong? quantity, bool live = false, string? unit = "每個", long? oldDistance = 7)
        {
            var item = new PersonalShopRow(index, "Observed", true, name, price?.ToString(CultureInfo.InvariantCulture), price, unit, quantity, "OfflineSynthetic", "合成資料");
            item = index switch {
                4 => item with { ItemKindRaw=0,ItemKindDetailFlag=1,ItemKind="Blessed",ItemKindState=PersonalShopItemKindRules.VerifiedState },
                1 => item with { ItemKindRaw=2,ItemKindDetailFlag=0,ItemIdentificationState="Unidentified",ItemIdentificationEvidence="Verified" },
                _ => item
            };
            var owner = new MarketOwner { Id = 77, Name = "+店主<&>", LocalIdentity = "do-not-export-local-identity", Position = new(100, 107), ObservedAt = acquired };
            var listing = new MarketListing { SourceSessionKey = "do-not-export-session", Revision = "do-not-export-revision", RequestId = "do-not-export-request",
                Owner = owner, CapturedAt = acquired, ViewerDirection = index % 2 == 0 ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell,
                DistanceAtRequest = oldDistance, OwnerAndResponseMatched = true, Complete = true, Rows = [item] };
            return new(listing, item, live ? new LivePlayer(77, owner.LocalIdentity, owner.Name, new(101, 102), acquired) : null, live ? new(100, 100) : null, live, manuallyRefreshed:live);
        }
        MarketRowView[] rows = [Make(4, Special, 25_000, 98, true), Make(1, "+十五位商品", 999_999_999_999_999, 999_999_999_999_999),
            Make(7, "-長整數商品", 1_000_000_000_000_000, ulong.MaxValue), Make(2, "@未知商品", null, null, unit: null, oldDistance: null),
            Make(0, "零元商品", 0, 0)];
        try
        {
            Check("write_sample_and_parse_every_xml_part", () =>
            {
                Require(!File.Exists(sample), "離線測試的樣本路徑必須是新檔，避免覆寫既有資料。");
                ExcelExporter.Write(sample, rows, "道具：中文<&>；價格≥0；=不可執行\u0002", "金額遞增；同價依原清單順序");
                parts = ReadParts(sample);
                string[] expected = ["[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml"];
                Require(parts.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(k => k, StringComparer.Ordinal)), "缺少或多出非預期的 ZIP 套件部分。");
            });
            Check("workbook_relationships_and_content_types", () =>
            {
                var names = Doc("xl/workbook.xml").Descendants(Spreadsheet + "sheet").ToArray();
                Require(names.Length == 2 && (string?)names[0].Attribute("name") == "商店查詢" && (string?)names[1].Attribute("name") == "匯出資訊", "工作表名稱或順序不符。");
                var rels = Doc("xl/_rels/workbook.xml.rels").Descendants(PackageRelationships + "Relationship").ToArray();
                Require(rels.Length == 3 && rels.All(r => parts!.ContainsKey("xl/" + (string?)r.Attribute("Target"))), "workbook relationship 目標無法解析。");
                Require(names.All(s => rels.Any(r => (string?)r.Attribute("Id") == (string?)s.Attribute(Relationships + "id"))), "工作表沒有對应的 relationship。");
                Require(Doc("_rels/.rels").Descendants(PackageRelationships + "Relationship").Any(r => (string?)r.Attribute("Target") == "xl/workbook.xml"), "根 relationship 缺 workbook。");
                XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
                Require(Doc("[Content_Types].xml").Descendants(types + "Override").Count() == 4, "必要的 content types 未齊全。");
            });
            Check("row_order_and_all_twelve_headers", () =>
            {
                var sheetRows = Doc("xl/worksheets/sheet1.xml").Descendants(Spreadsheet + "row").ToArray();
                Require(sheetRows.Length == 6 && sheetRows.All(r => r.Elements(Spreadsheet + "c").Count() == 23), "列數或欄數不符。");
                string[] expected = ["方向", "道具名", "金額", "數量", "單位", "店主", "店主座標", "目前至店主保存位置距離（格）", "取得時距離（格）", "取得時間", "狀態", "物品狀態", "原始旗標", "原始祝咒值", "鑑定辨識狀態", "鑑定證據", "分類依據", "祝咒證據", "證據來源", "來源版本 SHA256", "認證 SHA256", "當前程式碼驗證", "當前中繼資料驗證"];
                Require(Enumerable.Range(0, 23).Select(i => Text(((char)('A' + i)) + "1")).SequenceEqual(expected), "欄位順序不符。");
                Require(Text("B2").StartsWith("=中文", StringComparison.Ordinal) && Text("B3") == "+十五位商品" && Text("B4") == "-長整數商品" && Text("B5") == "@未知商品" && Text("B6") == "零元商品", "匯出重新排序或漏掉資料。");
            });
            Check("unicode_xml_characters_and_literal_excel_escape", () =>
            {
                Require(Text("B2") == "=中文<&>\"'😀��_x005F_x000A_", "中文、XML 特殊字元、非法字元或 literal Excel escape 未正確保存。");
                Require(Text("F2") == "+店主<&>", "店主 XML 特殊字元未保留。");
            });
            Check("all_user_text_inline_and_no_formula_or_external_link", () =>
            {
                Require(parts is not null && parts.Values.All(d => !d.Descendants(Spreadsheet + "f").Any()), "不應產生任何公式。");
                Require(new[] { "B2", "B3", "B4", "B5", "F2" }.All(r => Type(r) == "inlineStr"), "公式樣式開頭的文字被當成數字或公式。");
                Require(parts!.Values.All(d => !d.Descendants().Attributes("TargetMode").Any(a => a.Value == "External")), "不應有外部連結。");
            });
            Check("prices_and_quantities_numeric_through_fifteen_digits", () =>
            {
                Require(Type("C2") == "n" && Numeric("C2") == "25000" && Type("D2") == "n" && Numeric("D2") == "98", "金額或數量不是數值。");
                Require(Type("C3") == "n" && Numeric("C3") == "999999999999999" && Type("D3") == "n" && Numeric("D3") == "999999999999999", "15 位整數應保留数值型別。");
            });
            Check("long_integers_preserve_every_digit_as_text", () =>
            {
                Require(Type("C4") == "inlineStr" && Text("C4") == "1000000000000000", "16 位金額會失真。");
                Require(Type("D4") == "inlineStr" && Text("D4") == "18446744073709551615", "ulong 數量會失真。");
            });
            Check("unknown_is_not_zero_and_real_zero_remains_numeric", () =>
            {
                Require(new[] { "C5", "D5", "E5", "H5", "I5" }.All(r => Type(r) == "inlineStr" && Text(r) == "未知"), "未知值被填成 0。");
                Require(Type("C6") == "n" && Numeric("C6") == "0" && Type("D6") == "n" && Numeric("D6") == "0", "真正 0 被當成未知。");
            });
            Check("current_and_historical_distance_are_separate", () =>
            {
                Require(Numeric("H2") == "2" && Numeric("I2") == "7", "目前距離與取得時距離混用。");
                Require(Text("H3") == "未知" && Numeric("I3") == "7", "歷史距離冒充目前距離。");
                Require(Text("G2") == "101,102" && Text("G3") == "100,107", "店主座標未沿用列資料。");
            });
            Check("timestamps_and_status_preserve_capture_semantics", () =>
            {
                Require(Text("J2") == acquired.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), "取得時間被改為匯出時間。");
                Require(Text("K2") == "手動周遭快照／保存至下次刷新" && Text("K3") == "歷史／尚未對應手動周遭快照", "手動刷新或歷史狀態失真。");
            });
            Check("item_state_policy_and_raw_audit_columns", () =>
            {
                Require(Text("L2").Contains("祝福") && Text("M2")=="1" && Text("N2")=="0" && Text("R2")==PersonalShopItemKindRules.VerifiedState,"Blessing/raw evidence missing");
                Require(Text("L3")=="未知（未鑑定）" && Text("Q3")=="UserRuleUnidentifiedAsNormal" && Text("N3")=="2" && Text("O3")=="Unidentified" && Text("P3")=="Verified","User rule erased raw curse or proof");
                Require(Text("L4").Contains("未知") || Text("L4").Contains("未核實"),"Missing raw state defaulted to normal");
            });
            Check("frozen_header_filter_and_readable_column_widths", () =>
            {
                var sheet = Doc("xl/worksheets/sheet1.xml"); var pane = sheet.Descendants(Spreadsheet + "pane").Single();
                Require((string?)pane.Attribute("state") == "frozen" && (string?)pane.Attribute("ySplit") == "1" && (string?)pane.Attribute("topLeftCell") == "A2", "表頭未凍結。");
                Require((string?)sheet.Descendants(Spreadsheet + "autoFilter").Single().Attribute("ref") == "A1:W6", "自動篩選範圍錯誤。");
                Require(sheet.Descendants(Spreadsheet + "col").Count() == 23 && sheet.Descendants(Spreadsheet + "col").All(c => double.Parse(c.Attribute("width")!.Value, CultureInfo.InvariantCulture) >= 16), "中文欄寬不足。");
            });
            Check("metadata_records_filter_sort_version_and_service_only_source", () =>
            {
                var information = Doc("xl/worksheets/sheet2.xml"); string all = string.Join("\n", information.Descendants(Spreadsheet + "t").Select(t => t.Value));
                Require(all.Contains("V83（83.0.0）", StringComparison.Ordinal) && all.Contains("道具：中文<&>", StringComparison.Ordinal) && all.Contains("金額遞增", StringComparison.Ordinal) && all.Contains("由本程式直接讀取 LC 取得", StringComparison.Ordinal), "匯出說明不完整。");
                Require(parts!.Values.All(d => !d.ToString().Contains("do-not-export-", StringComparison.Ordinal)), "不應逐列或metadata揭露 session/localIdentity/request。");
            });
            Check("create_private_scratch_directory", () => { Directory.CreateDirectory(scratch); createdScratch = true; });
            Check("empty_export_remains_valid_header_only_workbook", () =>
            {
                ExcelExporter.Write(Path.Combine(scratch, "empty.xlsx"), [], "無資料", "原順序"); var empty = ReadParts(Path.Combine(scratch, "empty.xlsx"));
                Require(empty["xl/worksheets/sheet1.xml"].Descendants(Spreadsheet + "row").Count() == 1, "空結果不是表頭一列。");
                Require((string?)empty["xl/worksheets/sheet1.xml"].Descendants(Spreadsheet + "autoFilter").Single().Attribute("ref") == "A1:W1", "空結果篩選範圍錯誤。");
            });
            Check("existing_destination_never_overwritten", () =>
            {
                string target = Path.Combine(scratch, "atomic.xlsx"); byte[] sentinel = Encoding.UTF8.GetBytes("existing file must survive"); File.WriteAllBytes(target, sentinel);
                bool rejected = false;
                try { ExcelExporter.Write(target, rows, "", ""); } catch (IOException) { rejected = true; }
                Require(rejected && File.ReadAllBytes(target).SequenceEqual(sentinel), "合法資料匯出覆寫了既有檔案，或未明確拒絕。");
                Require(Directory.GetFiles(scratch, ".atomic.xlsx.*.tmp").Length == 0, "已有檔案仍建立或留下暫存檔。");
            });
            Check("failed_new_export_cleans_temporary_and_never_publishes_partial_file", () =>
            {
                string target = Path.Combine(scratch, "partial.xlsx");
                MustFail(() => ExcelExporter.Write(target, [null!], "", ""), "無效列意外寫入成功。");
                Require(!File.Exists(target) && Directory.GetFiles(scratch, ".partial.xlsx.*.tmp").Length == 0, "無效列失敗留下部分目標檔或暫存檔。");
                MustFail(() => ExcelExporter.Write(target, [Make(1, new string('字', 32_768), 1, 1)], "", ""), "超長文字未明確拒絕。");
                Require(!File.Exists(target) && Directory.GetFiles(scratch, ".partial.xlsx.*.tmp").Length == 0, "超長文字失敗留下部分目標檔或暫存檔。");
            });
            Check("excel_row_limit_rejects_before_enumeration_or_file_change", () =>
            {
                string target = Path.Combine(scratch, "limit.xlsx"); byte[] sentinel = [1, 2, 3]; File.WriteAllBytes(target, sentinel);
                bool rejected = false;
                try { ExcelExporter.Write(target, new OversizedRows(), "", ""); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected && File.ReadAllBytes(target).SequenceEqual(sentinel), "超過 Excel 列數上限未明確拒絕或改變舊檔。");
                Require(Directory.GetFiles(scratch, ".limit.xlsx.*.tmp").Length == 0, "列數超限仍建立暫存檔。");
            });
            Check("new_target_created_during_export_is_not_overwritten", () =>
            {
                string target = Path.Combine(scratch, "race.xlsx"); byte[] sentinel = Encoding.UTF8.GetBytes("another writer created this file");
                MustFail(() => ExcelExporter.Write(target, new FileAppearingRows(rows[0], () => File.WriteAllBytes(target, sentinel)), "", ""), "匯出期間新出現的目標檔被覆寫。");
                Require(File.ReadAllBytes(target).SequenceEqual(sentinel), "另一寫入者的新檔內容被改變。");
                Require(Directory.GetFiles(scratch, ".race.xlsx.*.tmp").Length == 0, "新檔競態失敗後留下暫存。");
            });
        }
        finally
        {
            if (createdScratch) Check("remove_only_owned_selftest_files", () =>
            {
                // No recursive deletion: only the five exact paths owned by this test.
                foreach (string name in new[] { "empty.xlsx", "atomic.xlsx", "partial.xlsx", "limit.xlsx", "race.xlsx" }) File.Delete(Path.Combine(scratch, name));
                Directory.Delete(scratch, recursive: false);
            });
        }
        return new("42.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed), checks.Count(c => !c.Passed), sample, false, false, false, checks.ToArray());
    }

    private static Dictionary<string, XDocument> ReadParts(string path)
    {
        using var file = File.OpenRead(path); using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var result = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            result.Add(entry.FullName, XDocument.Load(reader));
        }
        return result;
    }
    private sealed class OversizedRows : IReadOnlyList<MarketRowView>
    {
        public int Count => ExcelExporter.MaximumDataRows + 1;
        public MarketRowView this[int index] => throw new InvalidOperationException("超限輸入不得被存取。");
        public IEnumerator<MarketRowView> GetEnumerator() => throw new InvalidOperationException("超限輸入不得被列舉。");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class FileAppearingRows(MarketRowView row, Action appear) : IReadOnlyList<MarketRowView>
    {
        private bool appeared;
        public int Count => 1;
        public MarketRowView this[int index]
        {
            get { if (index != 0) throw new ArgumentOutOfRangeException(nameof(index)); if (!appeared) { appear(); appeared = true; } return row; }
        }
        public IEnumerator<MarketRowView> GetEnumerator() { yield return this[0]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}


