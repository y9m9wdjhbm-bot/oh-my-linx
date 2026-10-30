using System.Text;

namespace MemoryHub.Reader;

/// <summary>
/// Reads only the selected MyPlayer name. Nearby actor discovery is closed by
/// Self() before the name read; later checks retain the selected ownership chain.
/// </summary>
internal static class SelfNameFieldReader
{
    private static readonly UTF8Encoding Utf8=new(false,true);

    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        if(!selection.Enabled("characterName"))return;

        var graph=new CurrentTypedGraph(memory,journal,budget,"SELF_NAME");
        var self=graph.Self();
        ulong description=graph.Component(self.Actor,".?AVDescriptionComponent@lineage@@",0x11657B0,0x1643F70);
        var sampledAt=DateTimeOffset.UtcNow;
        byte[] descriptor=graph.Track(description+0x40,32);
        ulong length=CurrentTypedGraph.U64(descriptor,16),capacity=CurrentTypedGraph.U64(descriptor,24);
        graph.Need(length is >0 and <=96&&capacity>=length&&capacity<=4096&&
            (capacity==15&&length<=15||capacity>=16),"FORMAT","自身名稱字串長度／容量格式不符。");

        byte[] body;
        if(capacity==15)
        {
            // The complete descriptor is tracked, including inline bytes and
            // its terminator. It will be freshly checked again by Close().
            body=descriptor.AsSpan(0,(int)length+1).ToArray();
        }
        else
        {
            ulong buffer=CurrentTypedGraph.U64(descriptor);
            graph.Pointer(buffer);
            body=graph.Track(buffer,(int)length+1);
        }
        graph.Need(body[(int)length]==0&&!body.AsSpan(0,(int)length).Contains((byte)0),
            "NUL","自身名稱不是有完整結尾的單一字串。");

        string name;
        try{name=Utf8.GetString(body,0,(int)length);}
        catch(DecoderFallbackException)
        {throw new ProbeFailure("SELF_NAME_UTF8","自身名稱不是完整有效 UTF-8。");}
        graph.Need(!string.IsNullOrWhiteSpace(name)&&!name.Any(char.IsControl),
            "TEXT","自身名稱為空白或含控制字元。");

        graph.Close();
        graph.AttachSourceClosure();
        journal.Set("characterName",name,sampledAt,
            "SelectedSelfDescription;CurrentTypesAndOwnerGraphClosed;ValueABStable;TypedStructureCandidate");
    }
}
