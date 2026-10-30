namespace MemoryHub.Reader;

// Pure projection boundary. No process access is owned by this class.
internal static class PartyLocalProjection
{
    internal sealed record Candidate(string Name,ulong CandidateId,int PartyKey);
    internal sealed record Player(ulong Id,string? Name,string Detail="");
    internal sealed record Member(string Name,ulong CandidateId,ulong? LocalActorId,int PartyKey,string LocalMatchState,string LocalMatchDetail);

    // Roster membership comes only from the separately closed Party graph. A missing
    // local Player is a projection result, never evidence of leaving the party.
    internal static Member[] Capture(IReadOnlyList<Candidate> candidates,Func<IReadOnlyList<Player>> readLocal,
        Action closeLocal,Action verifySource,Action rollbackLocal)
    {
        if(candidates.Count==0)return [];
        try
        {
            var players=readLocal();var result=new List<Member>();
            foreach(var candidate in candidates)
            {
                var matches=players.Where(p=>p.Id==candidate.CandidateId).ToArray();
                if(matches.Length==0)
                    result.Add(Unresolved(candidate,"NotFoundInLocalSample","目前完整本機 Player/MyPlayer 採樣沒有此候選 ID；不代表離線、退隊或不在同一畫面。"));
                else if(matches.Length!=1)
                    result.Add(Unresolved(candidate,"AmbiguousLocalId","目前本機採樣出現多個相同 ID，不能唯一對應。"));
                else if(matches[0].Name is null)
                    result.Add(Unresolved(candidate,"LocalNameUnavailable",string.IsNullOrEmpty(matches[0].Detail)?"目前本機角色名稱未完整確認。":matches[0].Detail));
                else if(!string.Equals(matches[0].Name,candidate.Name,StringComparison.Ordinal))
                    result.Add(Unresolved(candidate,"NameMismatch","候選名稱與目前本機 Description 名稱不一致。"));
                else
                    result.Add(new(candidate.Name,candidate.CandidateId,candidate.CandidateId,candidate.PartyKey,"MatchedIdAndName","此輪本機角色 ID 與名稱一致；不推定跨用戶端 ID 或可直接施法。"));
            }
            // Even a negative lookup needs a closed complete local collection.
            // Nothing derived from the local graph is returned before this succeeds.
            closeLocal();return result.ToArray();
        }
        catch(ProbeFailure error)when(error.NativeError==0&&error.Code is "PARTY_LOCAL_CHANGED" or "PARTY_LOCAL_CLOSURE" or "PARTY_LOCAL_CONTROL_CLOSURE")
        {
            rollbackLocal();verifySource();
            return candidates.Select(c=>Unresolved(c,"LocalSampleUnavailable",error.Code+"：本機物件在讀取期間變動；保留獨立確認的隊伍候選名單，本機 ID 暫不提供。")).ToArray();
        }
    }
    private static Member Unresolved(Candidate candidate,string state,string detail)
        =>new(candidate.Name,candidate.CandidateId,null,candidate.PartyKey,state,detail);
}
