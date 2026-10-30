using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Service;

internal static class SearchedOwnerArchiveSelfTest
{
    internal static void Run()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "LC-Archive-SelfTest-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(fixture, "source"), target = Path.Combine(fixture, "target");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "writer.lock"), []);
            var at = DateTimeOffset.Parse("2026-09-30T03:00:00+08:00"); string epoch = Guid.NewGuid().ToString("N");
            var events = new[] {
                new SearchedOwnerEvent { Sequence=1, Kind="Initialize", Epoch=epoch, At=at },
                new SearchedOwnerEvent { Sequence=2, Kind="ImportComplete", Epoch=epoch, At=at.AddSeconds(1) },
                new SearchedOwnerEvent { Sequence=3, Kind="Intent", Epoch=epoch, At=at.AddSeconds(2), State="Uncertain",
                    Source=new SourceIdentity(123,123456,"fixture.exe","123",1,4096,new string('A',64),"source-fence"), SourceSessionKey="source-fence", RequestId="fixture-request",
                    Owner=new MarketOwner {Id=7, Name="fixture", LocalIdentity="owner-7", Position=new(100,101), ObservedAt=at}, Direction=ShopPageModes.ViewerBuy,
                    SearchedAt=at.AddSeconds(2), Key=new string('B',64) },
                new SearchedOwnerEvent { Sequence=4, Kind="Clear", Epoch=Guid.NewGuid().ToString("N"), At=at.AddSeconds(3) }
            };
            var raw = events.Select(e => JsonSerializer.SerializeToUtf8Bytes(e, HubProtocol.Json)).ToArray();
            // Preserve whitespace and unknown future evidence bytes rather than reserializing them.
            raw[2] = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(raw[2])[..^1] + ",\"futureEvidence\":\"verbatim\" }\n");
            for(int i=0;i<raw.Length;i++) File.WriteAllBytes(Path.Combine(source,(i+1).ToString("D10")+".json"),raw[i]);
            byte[] head = JsonSerializer.SerializeToUtf8Bytes(new SearchedOwnerHead(4,Hash(raw[3])),HubProtocol.Json);
            File.WriteAllBytes(Path.Combine(source,"head.checkpoint"),head);
            var originalHashes=Directory.GetFiles(source).ToDictionary(Path.GetFileName,p=>Hash(File.ReadAllBytes(p)));
            var metadata=SearchedOwnerArchive.CreateFromLegacy(source,target);
            var read=SearchedOwnerArchive.Read(target)!;
            Check(metadata.EventCount==4 && read.Events.Count==4 && read.TailSequence==4 && read.TailOriginalFileSHA==Hash(raw[3]),"archive counters and verified tail");
            Check(read.Events[2].State=="Uncertain" && read.Events[2].Source==events[2].Source && read.Events[2].Epoch!=read.Events[3].Epoch,"cross-epoch uncertain source fence retained");
            Check(head.SequenceEqual(File.ReadAllBytes(Path.Combine(target,"head.checkpoint"))),"exact legacy head preserved");
            using(var binary=new BinaryReader(File.OpenRead(Path.Combine(target,"events.archive"))))
            { binary.ReadBytes(8); binary.ReadInt32(); binary.ReadInt32(); foreach(var bytes in raw) Check(bytes.SequenceEqual(binary.ReadBytes(binary.ReadInt32())),"original event bytes preserved"); }
            Check(Directory.GetFiles(target,"*.json").Length==0,"migration creates no per-event JSON files");
            Check(originalHashes.All(kv=>Hash(File.ReadAllBytes(Path.Combine(source,kv.Key!)))==kv.Value),"legacy source files unchanged");
            var appended=events[3] with {Sequence=5,Kind="ImportComplete",At=at.AddSeconds(4)};
            byte[] next=JsonSerializer.SerializeToUtf8Bytes(appended,HubProtocol.Json);
            File.WriteAllBytes(Path.Combine(target,"0000000005.json"),next);
            File.WriteAllBytes(Path.Combine(target,"head.checkpoint"),JsonSerializer.SerializeToUtf8Bytes(new SearchedOwnerHead(5,Hash(next)),HubProtocol.Json));
            Check(SearchedOwnerArchive.Read(target)!.TailSequence==4,"post-archive append leaves verified prefix intact");
            File.WriteAllBytes(Path.Combine(target,"head.checkpoint"),head);
            string commit=Path.Combine(target,"archive.checkpoint"); byte[] savedCommit=File.ReadAllBytes(commit); File.Delete(commit);
            ExpectFailure(()=>SearchedOwnerArchive.Read(target),"missing commit fails closed"); File.WriteAllBytes(commit,savedCommit);
            string blob=Path.Combine(target,"events.archive"); byte[] savedBlob=File.ReadAllBytes(blob); var bad=(byte[])savedBlob.Clone(); bad[30]^=0x40; File.WriteAllBytes(blob,bad);
            ExpectFailure(()=>SearchedOwnerArchive.Read(target),"corrupt archive fails closed"); File.WriteAllBytes(blob,savedBlob);
            File.WriteAllText(commit,"{}"); ExpectFailure(()=>SearchedOwnerArchive.Read(target),"required commit fields fail closed"); File.WriteAllBytes(commit,savedCommit);
            ExpectFailure(()=>SearchedOwnerArchive.CreateFromLegacy(source,target),"existing nonempty destination rejected");
            File.WriteAllBytes(Path.Combine(source,"head.checkpoint"),JsonSerializer.SerializeToUtf8Bytes(new SearchedOwnerHead(3,Hash(raw[2])),HubProtocol.Json));
            ExpectFailure(()=>SearchedOwnerArchive.CreateFromLegacy(source,Path.Combine(fixture,"lagging")),"uncommitted legacy tail rejected"); File.WriteAllBytes(Path.Combine(source,"head.checkpoint"),head);
            using(var held=new FileStream(Path.Combine(source,"writer.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                ExpectFailure(()=>SearchedOwnerArchive.CreateFromLegacy(source,Path.Combine(fixture,"locked")),"active source writer excluded");
            File.Delete(Path.Combine(source,"0000000002.json"));
            ExpectFailure(()=>SearchedOwnerArchive.CreateFromLegacy(source,Path.Combine(fixture,"gap")),"missing legacy event rejected");
            Check(SearchedOwnerArchive.Read(Path.Combine(fixture,"ordinary-empty")) is null,"ordinary unarchived root supported");
            Console.WriteLine("SearchedOwnerArchiveSelfTest: PASS");
        }
        finally
        {
            string resolved=Path.GetFullPath(fixture);
            if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("LC-Archive-SelfTest-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected cleanup path");
            Directory.Delete(resolved,true);
        }
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static void Check(bool success,string name){if(!success)throw new InvalidOperationException("FAIL: "+name); Console.WriteLine("PASS: "+name);}
    private static void ExpectFailure(Action action,string name)
    {try{action();}catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException){Console.WriteLine("PASS: "+name);return;} throw new InvalidOperationException("FAIL: "+name);}
}
