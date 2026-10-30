using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace LcPlayerMarket;

/// <summary>Writes a passive snapshot of the already filtered and ordered rows; no service or game access.</summary>
internal static class ExcelExporter
{
    internal const int MaximumDataRows = 1_048_575; // One of Excel's 1,048,576 rows is the header.
    private const ulong LargestExactDecimalInteger = 999_999_999_999_999;
    private const string Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly string[] Headers = ["方向", "道具名", "金額", "數量", "單位", "店主", "店主座標", "目前至店主保存位置距離（格）", "取得時距離（格）", "取得時間", "狀態", "物品狀態", "原始旗標", "原始祝咒值", "鑑定辨識狀態", "鑑定證據", "分類依據", "祝咒證據", "證據來源", "來源版本 SHA256", "認證 SHA256", "當前程式碼驗證", "當前中繼資料驗證"];
    private static readonly double[] Widths = [24, 44, 22, 22, 16, 26, 20, 19, 21, 31, 34, 36, 16, 16, 24, 24, 42, 42, 28, 66, 66, 24, 24];

    internal static void Write(string path, IReadOnlyList<MarketRowView> rows, string filterDescription, string sortDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > MaximumDataRows) throw new ArgumentOutOfRangeException(nameof(rows), $"Excel 最多可匯出 {MaximumDataRows:N0} 筆資料（另含一列表頭）。");
        string target = Path.GetFullPath(path), directory = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("匯出資料夾不存在：" + directory);
        if (File.Exists(target)) throw new IOException("匯出只允許建立新檔，請選擇另一個檔名：" + target);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownTemporary = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                ownTemporary = true;
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteContentTypes(zip); WriteRelationships(zip); WriteWorkbook(zip); WriteStyles(zip);
                    WriteMarketSheet(zip, rows);
                    WriteInformationSheet(zip, rows.Count, filterDescription, sortDescription, DateTimeOffset.Now);
                }
                file.Flush(flushToDisk: true);
            }
            // Publish only a new path, atomically within the same directory.
            // A destination created by another writer during export must also fail.
            // No replace, delete-then-move, overwrite option or non-atomic fallback.
            File.Move(temporary, target);
            ownTemporary = false;
        }
        catch (Exception original)
        {
            if (ownTemporary)
            {
                try { File.Delete(temporary); }
                catch (Exception cleanup) { throw new AggregateException("匯出失敗；既有檔案未主動刪除，暫存檔清理亦失敗：" + temporary, original, cleanup); }
            }
            throw;
        }
    }

    private static void Part(ZipArchive zip, string path, Action<XmlWriter> body)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings
        { Encoding = new UTF8Encoding(false), CloseOutput = false, CheckCharacters = true, NewLineHandling = NewLineHandling.Entitize });
        xml.WriteStartDocument(); body(xml); xml.WriteEndDocument();
    }
    private static void WriteContentTypes(ZipArchive zip) => Part(zip, "[Content_Types].xml", xml =>
    {
        xml.WriteStartElement("Types", ContentTypes);
        void Default(string extension, string type) { xml.WriteStartElement("Default", ContentTypes); xml.WriteAttributeString("Extension", extension); xml.WriteAttributeString("ContentType", type); xml.WriteEndElement(); }
        void Override(string part, string type) { xml.WriteStartElement("Override", ContentTypes); xml.WriteAttributeString("PartName", part); xml.WriteAttributeString("ContentType", type); xml.WriteEndElement(); }
        Default("rels", "application/vnd.openxmlformats-package.relationships+xml"); Default("xml", "application/xml");
        Override("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        Override("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        foreach (int sheet in new[] { 1, 2 }) Override($"/xl/worksheets/sheet{sheet}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        xml.WriteEndElement();
    });
    private static void WriteRelationships(ZipArchive zip)
    {
        void Relationship(XmlWriter xml, string id, string type, string target)
        { xml.WriteStartElement("Relationship", PackageRelationships); xml.WriteAttributeString("Id", id); xml.WriteAttributeString("Type", Relationships + "/" + type); xml.WriteAttributeString("Target", target); xml.WriteEndElement(); }
        Part(zip, "_rels/.rels", xml =>
        { xml.WriteStartElement("Relationships", PackageRelationships); Relationship(xml, "rId1", "officeDocument", "xl/workbook.xml"); xml.WriteEndElement(); });
        Part(zip, "xl/_rels/workbook.xml.rels", xml =>
        {
            xml.WriteStartElement("Relationships", PackageRelationships);
            Relationship(xml, "rId1", "worksheet", "worksheets/sheet1.xml"); Relationship(xml, "rId2", "worksheet", "worksheets/sheet2.xml");
            Relationship(xml, "rId3", "styles", "styles.xml"); xml.WriteEndElement();
        });
    }
    private static void WriteWorkbook(ZipArchive zip) => Part(zip, "xl/workbook.xml", xml =>
    {
        xml.WriteStartElement("workbook", Spreadsheet); xml.WriteStartElement("bookViews", Spreadsheet);
        Empty(xml, "workbookView", ("activeTab", "0")); xml.WriteEndElement(); xml.WriteStartElement("sheets", Spreadsheet);
        foreach (var (name, index) in new[] { ("商店查詢", 1), ("匯出資訊", 2) })
        {
            xml.WriteStartElement("sheet", Spreadsheet); xml.WriteAttributeString("name", name); xml.WriteAttributeString("sheetId", index.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("r", "id", Relationships, "rId" + index); xml.WriteEndElement();
        }
        xml.WriteEndElement(); xml.WriteEndElement();
    });
    private static void WriteStyles(ZipArchive zip) => Part(zip, "xl/styles.xml", xml =>
    {
        xml.WriteStartElement("styleSheet", Spreadsheet);
        xml.WriteStartElement("fonts", Spreadsheet); xml.WriteAttributeString("count", "2");
        foreach (bool header in new[] { false, true })
        {
            xml.WriteStartElement("font", Spreadsheet);
            if (header) Empty(xml, "b");
            Empty(xml, "sz", ("val", "11")); Empty(xml, "color", ("rgb", header ? "FFFFFFFF" : "FF1F2937"));
            Empty(xml, "name", ("val", "Microsoft JhengHei")); Empty(xml, "family", ("val", "2")); xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteStartElement("fills", Spreadsheet); xml.WriteAttributeString("count", "3");
        foreach (string pattern in new[] { "none", "gray125", "solid" })
        {
            xml.WriteStartElement("fill", Spreadsheet); xml.WriteStartElement("patternFill", Spreadsheet); xml.WriteAttributeString("patternType", pattern);
            if (pattern == "solid") { Empty(xml, "fgColor", ("rgb", "FF234E70")); Empty(xml, "bgColor", ("indexed", "64")); }
            xml.WriteEndElement(); xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteStartElement("borders", Spreadsheet); xml.WriteAttributeString("count", "1"); xml.WriteStartElement("border", Spreadsheet);
        foreach (string edge in new[] { "left", "right", "top", "bottom", "diagonal" }) Empty(xml, edge);
        xml.WriteEndElement(); xml.WriteEndElement();
        xml.WriteStartElement("cellStyleXfs", Spreadsheet); xml.WriteAttributeString("count", "1");
        Empty(xml, "xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0")); xml.WriteEndElement();
        xml.WriteStartElement("cellXfs", Spreadsheet); xml.WriteAttributeString("count", "3");
        for (int style = 0; style < 3; style++)
        {
            xml.WriteStartElement("xf", Spreadsheet); xml.WriteAttributeString("numFmtId", style == 2 ? "3" : "0");
            xml.WriteAttributeString("fontId", style == 1 ? "1" : "0"); xml.WriteAttributeString("fillId", style == 1 ? "2" : "0");
            xml.WriteAttributeString("borderId", "0"); xml.WriteAttributeString("xfId", "0"); xml.WriteAttributeString("applyAlignment", "1");
            if (style == 2) xml.WriteAttributeString("applyNumberFormat", "1");
            if (style == 1) { xml.WriteAttributeString("applyFont", "1"); xml.WriteAttributeString("applyFill", "1"); }
            Empty(xml, "alignment", ("vertical", "top"), ("wrapText", "1")); xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteStartElement("cellStyles", Spreadsheet); xml.WriteAttributeString("count", "1");
        Empty(xml, "cellStyle", ("name", "Normal"), ("xfId", "0"), ("builtinId", "0")); xml.WriteEndElement(); xml.WriteEndElement();
    });
    private static void SheetStart(XmlWriter xml, string lastCell, double[] widths)
    {
        xml.WriteStartElement("worksheet", Spreadsheet); Empty(xml, "dimension", ("ref", "A1:" + lastCell));
        xml.WriteStartElement("sheetViews", Spreadsheet); xml.WriteStartElement("sheetView", Spreadsheet); xml.WriteAttributeString("workbookViewId", "0");
        Empty(xml, "pane", ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
        Empty(xml, "selection", ("pane", "bottomLeft"), ("activeCell", "A2"), ("sqref", "A2")); xml.WriteEndElement(); xml.WriteEndElement();
        Empty(xml, "sheetFormatPr", ("defaultRowHeight", "24"));
        xml.WriteStartElement("cols", Spreadsheet);
        for (int i = 0; i < widths.Length; i++) Empty(xml, "col", ("min", (i + 1).ToString(CultureInfo.InvariantCulture)),
            ("max", (i + 1).ToString(CultureInfo.InvariantCulture)), ("width", widths[i].ToString(CultureInfo.InvariantCulture)), ("customWidth", "1"));
        xml.WriteEndElement(); xml.WriteStartElement("sheetData", Spreadsheet);
    }
    private static void WriteMarketSheet(ZipArchive zip, IReadOnlyList<MarketRowView> rows) => Part(zip, "xl/worksheets/sheet1.xml", xml =>
    {
        int last = checked(rows.Count + 1); SheetStart(xml, "W" + last, Widths);
        BeginRow(xml, 1); for (int col = 0; col < Headers.Length; col++) Text(xml, Address(col, 1), Headers[col], 1); xml.WriteEndElement();
        for (int i = 0; i < rows.Count; i++)
        {
            MarketRowView row = rows[i] ?? throw new InvalidDataException($"第 {i + 1} 筆匯出資料為空。"); int number = i + 2;
            BeginRow(xml, number);
            Text(xml, Address(0, number), row.Direction); Text(xml, Address(1, number), row.Item);
            Integer(xml, Address(2, number), row.NumericPrice, row.Price); Integer(xml, Address(3, number), row.Row.Quantity);
            Text(xml, Address(4, number), row.Unit); Text(xml, Address(5, number), row.Owner); Text(xml, Address(6, number), row.Position);
            Distance(xml, Address(7, number), row.NumericDistance); Distance(xml, Address(8, number), row.Listing.DistanceAtRequest);
            Text(xml, Address(9, number), row.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            Text(xml, Address(10, number), row.Status); Text(xml, Address(11, number), row.ItemKind);
            Text(xml, Address(12, number), row.Row.ItemKindDetailFlag?.ToString(CultureInfo.InvariantCulture) ?? "未知");
            Text(xml, Address(13, number), row.Row.ItemKindRaw?.ToString(CultureInfo.InvariantCulture) ?? "未知");
            Text(xml, Address(14, number), row.Row.ItemIdentificationState); Text(xml, Address(15, number), row.Row.ItemIdentificationEvidence);
            Text(xml, Address(16, number), MarketItemStatePresentation.EffectiveKindReason(row.Row));
            Text(xml, Address(17, number), row.Row.ItemKindState);
            Text(xml, Address(18, number), row.Row.ItemStateEvidence?.Mode ?? "未記錄");
            Text(xml, Address(19, number), row.Row.ItemStateEvidence?.SourceBuildSha256 ?? "未記錄");
            Text(xml, Address(20, number), row.Row.ItemStateEvidence?.CertificateSha256 ?? "未記錄");
            Text(xml, Address(21, number), MarketItemStatePresentation.EvidenceBoolean(row.Row.ItemStateEvidence?.CurrentRuntimeCodeVerified));
            Text(xml, Address(22, number), MarketItemStatePresentation.EvidenceBoolean(row.Row.ItemStateEvidence?.CurrentMetadataVerified)); xml.WriteEndElement();
        }
        xml.WriteEndElement(); Empty(xml, "autoFilter", ("ref", "A1:W" + last)); xml.WriteEndElement();
    });
    private static void WriteInformationSheet(ZipArchive zip, int count, string filter, string sort, DateTimeOffset generated) => Part(zip, "xl/worksheets/sheet2.xml", xml =>
    {
        (string Label, string Value)[] data = [
            ("產生時間", generated.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            ("程式版本", "LC 玩家商店查詢 V83（83.0.0）"), ("資料筆數", count.ToString(CultureInfo.InvariantCulture)),
            ("篩選條件", filter ?? ""), ("排序方式", sort ?? ""), ("遊戲資料來源", "由本程式直接讀取 LC 取得；匯出功能不讀取遊戲記憶體"),
            ("距離說明", "目前距離使用匯出時自身座標與店主保存位置計算；取得時距離另列，未知值不以 0 代替"),
            ("文字與精度", "文字不執行公式；超過 15 位的整數保留為文字；非法 XML 字元以 � 取代"),
            ("內容說明", "依匯出時已篩選及排序的結果保存；取得時間與狀態沿用原資料，不代表即時重新讀取"), ("版本認證說明", "版本認證依同版本靜態語意與本次中繼資料，不表示當前格式化程式碼可讀；歷史列保留取得時證據。")];
        SheetStart(xml, "B" + (data.Length + 1), [22, 100]); BeginRow(xml, 1); Text(xml, "A1", "項目", 1); Text(xml, "B1", "內容", 1); xml.WriteEndElement();
        for (int i = 0; i < data.Length; i++) { BeginRow(xml, i + 2); Text(xml, "A" + (i + 2), data[i].Label); Text(xml, "B" + (i + 2), data[i].Value); xml.WriteEndElement(); }
        xml.WriteEndElement(); xml.WriteEndElement();
    });
    private static void BeginRow(XmlWriter xml, int row) { xml.WriteStartElement("row", Spreadsheet); xml.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture)); }
    private static string Address(int column, int row) => ((char)('A' + column)).ToString() + row.ToString(CultureInfo.InvariantCulture);
    private static void Empty(XmlWriter xml, string name, params (string Name, string Value)[] attributes)
    { xml.WriteStartElement(name, Spreadsheet); foreach (var (attribute, value) in attributes) xml.WriteAttributeString(attribute, value); xml.WriteEndElement(); }
    private static void Distance(XmlWriter xml, string reference, long? value) => Integer(xml, reference, value is >= 0 ? (ulong)value.Value : null);
    private static void Integer(XmlWriter xml, string reference, ulong? value, string? unknown = null)
    {
        if (value is null) { Text(xml, reference, string.IsNullOrEmpty(unknown) ? "未知" : unknown); return; }
        string number = value.Value.ToString(CultureInfo.InvariantCulture);
        if (value.Value > LargestExactDecimalInteger) { Text(xml, reference, number); return; }
        xml.WriteStartElement("c", Spreadsheet); xml.WriteAttributeString("r", reference); xml.WriteAttributeString("s", "2"); xml.WriteAttributeString("t", "n");
        xml.WriteElementString("v", Spreadsheet, number); xml.WriteEndElement();
    }
    private static void Text(XmlWriter xml, string reference, string? value, int style = 0)
    {
        string clean = CleanText(value ?? "");
        xml.WriteStartElement("c", Spreadsheet); xml.WriteAttributeString("r", reference); xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("t", "inlineStr");
        xml.WriteStartElement("is", Spreadsheet); xml.WriteStartElement("t", Spreadsheet); xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        xml.WriteString(clean); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
    }
    private static string CleanText(string value)
    {
        if (value.Length > 32_767) throw new InvalidDataException("Excel 單一儲存格文字不得超過 32,767 個字元；已取消匯出以免截斷資料。");
        var clean = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { clean.Append(c).Append(value[++i]); continue; }
            clean.Append(XmlConvert.IsXmlChar(c) ? c : '\uFFFD');
        }
        // Excel decodes _xHHHH_ even inside inline strings: escape its literal underscore.
        return Regex.Replace(clean.ToString(), "_x[0-9A-Fa-f]{4}_", match => "_x005F_" + match.Value[1..]);
    }
}


