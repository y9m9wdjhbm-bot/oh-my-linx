using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryHub.Reader;

// Only the four exact, retained HUD numeric paths are implemented here. A static
// spell catalog is not a learned-spell collection and is never emitted as one.
internal static class SkillsStatsFieldReader
{
    private sealed record Field(string Key,int[] Path,bool Percent,int Minimum,int Maximum);
    private static readonly Field[] Fields=
    [
        new("foodPercent",[2,2,3,0,8],true,0,100),
        new("armorClass",[2,2,3,0,11],false,-1000,1000),
        new("magicResistancePercent",[2,2,3,0,12],true,0,1000),
        new("alignment",[2,2,3,0,16,1],false,-32768,32767)
    ];
    private static readonly UTF8Encoding Utf8=new(false,true);

    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        foreach(var field in Fields.Where(f=>selection.Enabled(f.Key)))
        {
            DateTimeOffset at=DateTimeOffset.UtcNow;
            var proofs=new Dictionary<(ulong Address,int Size),byte[]>();
            var metadata=new HashSet<(ulong Address,int Size)>();
            var types=new HashSet<uint>();
            byte[] Read(ulong address,int size)
            {
                budget.Check();Need(size is >0 and <=4096&&address>=0x10000&&address<=0x7FFFFFFFFFFFUL-(uint)size,"STATS_READ_RANGE","自身資訊讀取範圍不符");
                if(!memory.Pointed(address,size,out var bytes))throw new ProbeFailure("STATS_READ_FAILED","自身資訊讀取未完整完成",memory.LastNativeError);
                return bytes;
            }
            byte[] Track(ulong address,int size,bool isMetadata=false)
            {
                budget.Check();var key=(address,size);
                if(proofs.TryGetValue(key,out var old))return old;
                Need(proofs.Count<256,"STATS_PROOF_LIMIT","自身資訊證據數超界");
                var bytes=isMetadata?memory.MetaPointed(address,size):Read(address,size);
                if(isMetadata)metadata.Add(key);proofs.Add(key,bytes);return bytes;
            }
            void Exact(ulong address,uint baselineVtable)
            {
                Need(Probe.Pointer(address)&&Probe.U64(Track(address,8),0)==memory.Address(baselineVtable),"STATS_TYPE_VTABLE","自身資訊精確型別不符");
                if(!types.Add(baselineVtable))return;
                var type=ProfileSymbols.Types.Single(t=>t.Vtable==baselineVtable);
                ulong vt=memory.Address(type.Vtable),colAddress=memory.Address(type.Col);
                byte[] prefix=Track(vt-8,24,true),col=Track(colAddress,24,true);
                Need(Probe.U64(prefix,0)==colAddress&&Probe.U32(col,0)==1&&Probe.U32(col,4)==0&&Probe.U32(col,8)==0&&
                    Probe.U32(col,12)==memory.Rva(type.Descriptor)&&Probe.U32(col,20)==memory.Rva(type.Col)&&
                    (memory.Executable(Probe.U64(prefix,8))||memory.Executable(Probe.U64(prefix,16))),"STATS_TYPE_COL","自身資訊 COL、型別或函式槽不符");
                byte[] name=Encoding.ASCII.GetBytes(type.Name+'\0');
                Need(Track(memory.Address(type.Descriptor,16),name.Length,true).AsSpan().SequenceEqual(name),"STATS_TYPE_NAME","自身資訊完整 RTTI 名稱不符");
            }
            ulong[] Vector(byte[] header)
            {
                ulong begin=Probe.U64(header,0),end=Probe.U64(header,8),cap=Probe.U64(header,16);
                if(begin==0&&end==0&&cap==0)return [];
                Need(Probe.Pointer(begin)&&Probe.Pointer(end)&&Probe.Pointer(cap)&&begin<=end&&end<=cap&&
                    (end-begin)%8==0&&(cap-begin)%8==0&&(cap-begin)/8<=4096,"STATS_CHILDREN_RANGE","自身資訊子節點集合界線不符");
                var children=new List<ulong>();
                for(ulong offset=0;offset<end-begin;offset+=4096)
                {
                    byte[] values=Track(begin+offset,(int)Math.Min(4096,end-begin-offset));
                    for(int i=0;i<values.Length;i+=8)children.Add(Probe.U64(values,i));
                }
                Need(children.All(Probe.Pointer)&&children.Distinct().Count()==children.Count,"STATS_CHILDREN_DUPLICATE","自身資訊子節點無效或重複");return children.ToArray();
            }
            ulong director=Probe.U64(Track(memory.Address(0x17E00D8),8),0);Exact(director,0x121AC40);
            ulong scene=Probe.U64(Track(director+0x150,8),0);journal.RequireScene(director,scene);
            var visited=new HashSet<ulong>();
            ulong[] Node(ulong node,ulong parent,uint type)
            {
                Need(visited.Add(node),"STATS_NODE_CYCLE","自身資訊節點循環");Exact(node,type);
                byte[] links=Track(node+0x178,40);
                Need(Probe.U64(links,24)==parent&&Probe.U64(links,32)==director,"STATS_NODE_OWNER","自身資訊節點 parent／Director 不符");
                return Vector(links[..24]);
            }
            ulong current=scene,parent=0;
            for(int depth=0;depth<field.Path.Length;depth++)
            {
                // ProbeV4 proved only alignment's Scene/2/2/3/0/16 parent
                // (depth 5) is exact Layout. Other paths still require Node.
                uint expectedType=depth==0?0x121D980u:
                    field.Key=="alignment"&&depth==5?0x1227950u:0x121B1D0u;
                ulong[] children=Node(current,parent,expectedType);
                int index=field.Path[depth];Need(index<children.Length,"STATS_PATH_MISSING","自身資訊精確路徑目前不存在");
                parent=current;current=children[index];
            }
            Node(current,parent,0x1223A30);
            ulong renderer=Probe.U64(Track(current+0x740,8),0);
            Need(Vector(Track(current+0x340,24)).Count(p=>p==renderer)==1,"STATS_RENDERER_OWNER","自身資訊 renderer 不是唯一所屬 protected child");
            Node(renderer,current,0x1220C90);
            byte[] descriptor=Track(renderer+0x378,32);ulong length=Probe.U64(descriptor,16),capacity=Probe.U64(descriptor,24);
            Need(length is >0 and <=128&&capacity>=length&&capacity<=4096&&(capacity==15||capacity>=16),"STATS_TEXT_SIZE","自身資訊文字長度不符");
            byte[] body=capacity==15?descriptor[..((int)length+1)]:Track(Probe.U64(descriptor,0),(int)length+1);
            Need(body[(int)length]==0&&body.AsSpan(0,(int)length).IndexOf((byte)0)<0,"STATS_TEXT_END","自身資訊文字結尾不符");
            string text;
            try{text=Utf8.GetString(body,0,(int)length);}
            catch(DecoderFallbackException error)
            {throw new ProbeFailure("STATS_TEXT_UTF8","自身資訊文字不是完整有效 UTF-8："+error.GetType().Name);}
            string clean=Regex.Replace(text,@"\x1B\[[0-9;]{0,24}m","").Trim();
            string pattern=field.Percent?@"^(?<n>[0-9]{1,4})%$":@"^(?<n>-?[0-9]{1,5})$";
            var match=Regex.Match(clean,pattern,RegexOptions.CultureInvariant);
            Need(match.Success&&int.TryParse(match.Groups["n"].Value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out _),"STATS_VALUE_FORMAT","自身資訊完整數字格式不符");
            int value=int.Parse(match.Groups["n"].Value,CultureInfo.InvariantCulture);
            Need(value>=field.Minimum&&value<=field.Maximum,"STATS_VALUE_RANGE","自身資訊數值超出可接受範圍");
            // Re-read values and every dynamic ownership link before publication.
            foreach(var proof in proofs.Reverse())
            {
                byte[] after=metadata.Contains(proof.Key)?memory.MetaPointed(proof.Key.Address,proof.Key.Size):Read(proof.Key.Address,proof.Key.Size);
                Need(after.AsSpan().SequenceEqual(proof.Value),"STATS_SAMPLE_CHANGED","自身資訊文字或來源在本輪變動");
            }
            memory.Verify();journal.RequireScene(director,scene);
            journal.Set(field.Key,value,at,"ExactHudNumericPath;CurrentTypesAndOwnerGraphClosed;ValueABStable;FreshVisualComparisonPending");
        }
    }
    private static void Need(bool condition,string code,string message)
    {if(!condition)throw new ProbeFailure(code,message);}
}
