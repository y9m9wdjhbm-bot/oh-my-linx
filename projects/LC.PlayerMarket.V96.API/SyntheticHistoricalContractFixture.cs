using System.Text.Json.Nodes;
using MemoryHub.Service;
using MemoryHub;
using System.Security.Cryptography;
using System.Text;

namespace LcPlayerMarket;

/// <summary>OFFLINE SYNTHETIC historical-38A7 grammar/dataflow only. Original FEDFC bytes remain evidence of FEDFC only. No authenticated translation, current LC/source evidence, historical recovery or native authority is produced.</summary>
internal static class SyntheticHistoricalContractFixture
{
    private const string HistoricalBuild="FEDFC89D776E011C0F1D57FD2235413BA2A857AD1E51CADA08BB9209253A7B6A";
    private const string HistoricalPolicy="Only Create/Copy committed image NOACCESS, pinned historical exact-image seven full A/B bodies; independently verified current source; single trial; immediate strict full postbody; maxmask3";
    private static readonly (uint Old,uint Current)[] Addresses=
    [
        (0x3FB70,0x3FC90),(0x281510,0x287910),(0x282000,0x288000),(0x281000,0x287000),
        (0x2718F0,0x277CF0),(0x271000,0x277000),(0x72C580,0x73B790),(0x72C000,0x73B000),
        (0x1019A0,0x104870),(0x101000,0x104000),(0x71E1C0,0x72D300),(0x71E000,0x72D000),
        (0x75CC40,0x76BEB0),(0x75C000,0x76B000),(0x3E842F8,0x3EC5C1C),(0x1512414,0x15371DC)
    ];
    internal static string FromSavedShape(string historical)
    {
        string originalSha=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(historical)));
        string[] allowedShapes=["1D38BBEF234CCA2977E2BDFFA253416FA59FD9E67A49FDABACA650C492CB49B0", "565A6E7E65894A048695CABB11303CE31FB3678725EE732C30435AB00B8CA4D5", "3D09875198B528DC05304A4D2D42B1CDBA277B46664E421AA2E5E3AB02F33204"];
        if(!allowedShapes.Contains(originalSha,StringComparer.Ordinal))throw new InvalidDataException("Unknown saved shape is not a synthetic contract input");
        JsonObject[] rows=historical.Split('\n',StringSplitOptions.RemoveEmptyEntries)
            .Where(line=>!string.IsNullOrWhiteSpace(line)).Select(line=>JsonNode.Parse(line)!.AsObject()).ToArray();
        var source=rows.Single(row=>row["Kind"]!.GetValue<string>()=="SOURCE")["Data"]!;
        if(source["Sha256"]!.GetValue<string>()!=HistoricalBuild || source["Timestamp"]!.GetValue<string>()!="0x6AC5130F" || source["ImageSize"]!.GetValue<string>()!="0x3F1D000")
            throw new InvalidDataException("Synthetic shape origin must retain exact original FEDFC identity");
        ulong image=source["Image"]!.GetValue<ulong>();
        string hookSha=ShopNativeHistoricalPackage.LegacyStrictV16HookSha;
        string Change(string value)
        {
            if(value==HistoricalBuild)return ShopNativeHistoricalPackage.SupportedSha;
            if(value.Equals("0x6AC5130F",StringComparison.OrdinalIgnoreCase))return $"0x{ShopNativeHistoricalPackage.SupportedTimestamp:X}";
            if(value.Equals("0x3F1D000",StringComparison.OrdinalIgnoreCase))return $"0x{ShopNativeHistoricalPackage.SupportedImageSize:X}";
            if(value==HistoricalPolicy)return ShopNativeHistoricalPackage.LegacyStrictV16Policy;
            if(value=="9EDC334118A730E367966DB1BF238FF71EBFD7E645C5ECCA1CF4D9CEBF3F5D86")return hookSha;
            foreach(var address in Addresses)
                value=value.Replace($"0x{address.Old:X}",$"0x{address.Current:X}",StringComparison.OrdinalIgnoreCase);
            return value;
        }
        void Visit(JsonNode? node)
        {
            if(node is JsonArray array){foreach(var child in array)Visit(child);return;}
            if(node is not JsonObject obj)return;
            foreach(var property in obj.ToArray())
            {
                if(property.Value is JsonValue value)
                {
                    if(value.TryGetValue<string>(out string? text)&&text is not null)obj[property.Key]=Change(text);
                    else if(property.Key is "Base" or "Address" or "PacketCreate" or "PacketCopy")
                    {
                        if(value.TryGetValue<ulong>(out ulong pointer))
                            foreach(var address in Addresses)
                                if(pointer==image+address.Old){obj[property.Key]=image+address.Current;break;}
                    }
                }
                else Visit(property.Value);
            }
            if(obj["Rva"] is JsonValue rva&&rva.TryGetValue<string>(out string? current)&&obj.ContainsKey("Length"))
            {
                if(current=="0x287910")obj["Length"]=1776;
                else if(current=="0x288000")obj["Length"]=1570;
            }
        }
        foreach(var row in rows)Visit(row);
        return string.Join('\n',rows.Select(row=>row.ToJsonString()))+"\n";
    }
    // Negative fixtures only: stamping a legacy producer with a current source must fail.
    internal static SourceIdentity CurrentBindingForRejection(SourceIdentity historical) => historical with
        { DiskSha256=ShopNativePackage.SupportedSha, PeTimestamp=ShopNativePackage.SupportedTimestamp, ImageSize=ShopNativePackage.SupportedImageSize };
    internal static string RelabelCurrentForRejection(string syntheticHistorical)
    {
        var rows=syntheticHistorical.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonNode.Parse(line)!.AsObject()).ToArray();
        var source=rows.Single(row=>row["Kind"]!.GetValue<string>()=="SOURCE")["Data"]!;
        source["Sha256"]=ShopNativePackage.SupportedSha;
        source["Timestamp"]=$"0x{ShopNativePackage.SupportedTimestamp:X}";
        source["ImageSize"]=$"0x{ShopNativePackage.SupportedImageSize:X}";
        return string.Join('\n',rows.Select(row=>row.ToJsonString()))+"\n";
    }
}
