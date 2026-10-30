using System.Text;

namespace MemoryHub.Reader;

/// <summary>Complete typed party candidate inventory with independent optional local-player projection.</summary>
internal static class PartyFieldReader
{
    private sealed record Candidate(int PartyKey,int Index,ulong Id,ulong Object,string Name);
    private static readonly UTF8Encoding Utf8=new(false,true);
    internal static void Capture(ReadSession memory,Journal journal,Budget budget,ReadSelection selection)
    {
        if(!selection.Enabled("party"))return;
        var at=DateTimeOffset.UtcNow;var g=new CurrentTypedGraph(memory,journal,budget,"PARTY_FIELD");
        // CurrentTypedGraph resolves the fixed type anchors through this session profile.
        ulong partySubsystem=g.Subsystem(".?AVPartySubsystem@lineage@@",0x11AC548,0x16447A0);
        // CurrentLcSource has already bound CharacterSourceFence. The roster itself
        // must not require any nearby actor (including an extra self discovery).
        var header=g.Track(partySubsystem+0x4A0,16);ulong head=CurrentTypedGraph.U64(header),count=CurrentTypedGraph.U64(header,8);
        g.Pointer(head);g.Need(count<=16,"TREE_LIMIT","組隊樹超過 16 節點，不截斷。");
        var sentinel=g.Track(head,32);g.Need(sentinel[0x19]==1,"SENTINEL","組隊 sentinel 的 nil flag 不符。");
        ulong root=CurrentTypedGraph.U64(sentinel,8),leftmost=CurrentTypedGraph.U64(sentinel),rightmost=CurrentTypedGraph.U64(sentinel,16);
        g.Pointer(root);g.Pointer(leftmost);g.Pointer(rightmost);
        var candidates=new List<Candidate>();var visited=new HashSet<ulong>();var ordered=new List<ulong>();int entries=0;
        if(count==0)g.Need(root==head&&leftmost==head&&rightmost==head,"EMPTY_LINKS","空組隊樹的完整連結不符。");
        else
        {
            g.Need(root!=head,"NONEMPTY_ROOT","非空組隊樹沒有 root。");
            Walk(root,head,long.MinValue,long.MaxValue);
            g.Need(visited.Count==(int)count&&ordered.Count>0&&ordered[0]==leftmost&&ordered[^1]==rightmost,"TREE_COVERAGE","組隊 count／extrema 與完整走訪不同。");
        }
        g.Need(candidates.Select(c=>c.Id).Distinct().Count()==candidates.Count,"DUPLICATE_ID","隊員候選 ID 重複，不能唯一投影。");
        g.Need(candidates.Select(c=>c.Object).Distinct().Count()==candidates.Count,"DUPLICATE_MEMBER","隊員物件重複，不能唯一投影。");
        g.Need(candidates.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==candidates.Count,"DUPLICATE_NAME","隊員名稱重複，無法形成唯一名單。");
        g.Close();
        int localCheckpoint=memory.MetadataCheckpoint();
        CurrentTypedGraph? local=null;
        var projected=PartyLocalProjection.Capture(candidates.Select(c=>new PartyLocalProjection.Candidate(c.Name,c.Id,c.PartyKey)).ToArray(),
            ReadLocal,()=>local!.Close(),()=>{memory.Verify();journal.SourceClosure?.Invoke();},()=>memory.RollbackMetadataCandidate(localCheckpoint));
        // Recheck only the Party graph after optional projection. Its proof set
        // contains no local actors, so unrelated arrivals do not erase the roster.
        g.Close();g.AttachSourceClosure();
        journal.Write("PARTY_CURRENT_SAMPLE",new {StartedAt=at,PartyCount=count,MemberEntries=entries,ResolvedMembers=projected.Count(p=>p.LocalActorId is not null),
            EmptyTree=count==0,AllCandidatesResolved=projected.All(p=>p.LocalActorId is not null),CurrentTypedRoot=true,CompleteCollection=true,
            LocalActorIdOnly=false,CandidateIdSemanticsVerified=false,AtomicSnapshot=false,UserPartyVerified=false,
            LocalMatchStates=projected.GroupBy(p=>p.LocalMatchState).ToDictionary(p=>p.Key,p=>p.Count())});
        journal.Set("party",projected,at,"CurrentTypedPartyCandidatesWithOptionalLocalIdNameMatch");

        IReadOnlyList<PartyLocalProjection.Player> ReadLocal()
        {
            local=new CurrentTypedGraph(memory,journal,budget,"PARTY_LOCAL");
            var players=local.Players();var wanted=candidates.Select(c=>c.Id).ToHashSet();
            var result=new List<PartyLocalProjection.Player>();
            foreach(var player in players.Where(p=>wanted.Contains(p.Id)))
            {
                // Keep duplicate IDs as separate rows for explicit ambiguity.
                // Their descriptions cannot make the identifier unique.
                if(players.Count(p=>p.Id==player.Id)!=1){result.Add(new(player.Id,null));continue;}
                ulong description=local.Component(player.Actor,".?AVDescriptionComponent@lineage@@",0x11657B0,0x1643F70);
                string? name;string detail="";
                try{name=Text(local,description+0x40,"PARTY_LOCAL");}
                catch(ProbeFailure error)when(error.NativeError==0&&error.Code is "PARTY_LOCAL_STRING_END" or "PARTY_LOCAL_STRING_VALUE" or "PARTY_LOCAL_UTF8")
                {name=null;detail=error.Code+"：本機名稱格式尚未確認。";}
                result.Add(new(player.Id,name,detail));
            }
            return result;
        }

        void Walk(ulong node,ulong parent,long lo,long hi)
        {
            g.Pointer(node);if(node==head)return;
            g.Need(visited.Count<16&&visited.Add(node),"TREE_CYCLE","組隊節點數超界、循環或共享子節點。");
            var n=g.Track(node,56);int key=CurrentTypedGraph.I32(n,0x20);
            g.Need(n[0x19]==0&&n[0x18]<=1&&CurrentTypedGraph.U64(n,8)==parent&&key>lo&&key<hi,"TREE_NODE","組隊樹 nil／color／parent／排序不符。");
            Walk(CurrentTypedGraph.U64(n),node,lo,key);ordered.Add(node);
            ulong party=CurrentTypedGraph.U64(n,0x28),control=CurrentTypedGraph.U64(n,0x30);g.Pointer(party);g.Control(control);
            g.Exact(control,".?AV?$_Ref_count_obj2@VParty@lineage@@@std@@",descriptor:0x1704768,col:0x12BA4F8);
            g.Need(party==control+0x10,"VALUE_LAYOUT","Party shared_ptr payload 關係不符。");
            g.Need(CurrentTypedGraph.U64(g.Track(party,8))==party,"SELF_FIELD","Party payload self 欄位不符。");
            g.Need(CurrentTypedGraph.I32(g.Track(party+0x48,4))==key,"PARTY_KEY","Party 類別欄位與樹 key 不符。");
            var memberHeader=g.Track(party+0x30,24);ulong begin=CurrentTypedGraph.U64(memberHeader),end=CurrentTypedGraph.U64(memberHeader,8),cap=CurrentTypedGraph.U64(memberHeader,16);
            int memberCount;
            if(begin==0&&end==0&&cap==0)memberCount=0;
            else
            {
                g.Pointer(begin);g.Pointer(end);g.Pointer(cap);
                g.Need(begin<=end&&end<=cap&&(end-begin)%16==0&&(cap-begin)%16==0&&(cap-begin)/16<=32,"MEMBER_VECTOR","Party 成員集合超界或不完整。");
                memberCount=checked((int)((end-begin)/16));
            }
            entries+=memberCount;g.Need(entries<=128,"MEMBER_TOTAL","總隊員項目超過 128，不截斷。");
            if(memberCount>0)
            {
                var members=g.Vector(party+0x30,32);
                g.Need(members.Count==memberCount,"MEMBER_COVERAGE","隊員集合含空值或重複項，不能形成完整名單。");
                for(int index=0;index<members.Count;index++)
                {
                    var member=members[index];g.Control(member.Control);
                    g.Exact(member.Control,".?AV?$_Ref_count_obj2@VPartyPlayer@lineage@@@std@@",descriptor:0x17045F0,col:0x12B9EA8);
                    g.Need(member.Object==member.Control+0x10,"MEMBER_LAYOUT","PartyPlayer payload 與 control 關係不符。");
                    ulong id=CurrentTypedGraph.U64(g.Track(member.Object+0x28,8));g.Need(id is not(0 or ulong.MaxValue),"MEMBER_ID","隊員候選 ID 無效。");
                    string[] names=new[]{Text(g,member.Object+0x08),Text(g,member.Object+0x30)}.Where(n=>n is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
                    g.Need(names.Length==1,"MEMBER_NAME","固定 0x08／0x30 名稱候選不唯一或未完整讀得。");
                    candidates.Add(new(key,index,id,member.Object,names[0]));
                }
            }
            Walk(CurrentTypedGraph.U64(n,0x10),node,key,hi);
        }
    }
    private static string? Text(CurrentTypedGraph g,ulong descriptorAt,string failurePrefix="PARTY_FIELD")
    {
        byte[] descriptor=g.Track(descriptorAt,32);ulong length=CurrentTypedGraph.U64(descriptor,16),capacity=CurrentTypedGraph.U64(descriptor,24);
        if(length is 0 or >96||capacity<length||capacity>4096||capacity<15)return null;
        bool sso=capacity==15;if(sso&&length>15||!sso&&capacity<16)return null;
        byte[] text;
        if(sso)text=descriptor.AsSpan(0,checked((int)length+1)).ToArray();
        else {ulong pointer=CurrentTypedGraph.U64(descriptor);g.Pointer(pointer);text=g.Track(pointer,checked((int)length+1));}
        g.Need(text[^1]==0&&text.AsSpan(0,text.Length-1).IndexOf((byte)0)<0,"STRING_END","名稱字串缺少 terminator 或含內部 NUL。");
        string decoded;
        try{decoded=Utf8.GetString(text,0,text.Length-1);}
        catch(DecoderFallbackException){throw new ProbeFailure(failurePrefix+"_UTF8","名稱不是完整合法的 UTF-8。");}
        g.Need(decoded.Length is >0 and <=96&&!string.IsNullOrWhiteSpace(decoded)&&!decoded.Any(c=>char.IsControl(c)||c=='\uFFFD'),"STRING_VALUE","名稱格式不符。");
        return decoded;
    }
}
