using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Snapshot identity parsing only; no reader/source construction or process access.</summary>
internal static class LiveStartupIdentityContextSelfTest
{
    internal static IReadOnlyList<OfflineCheck> Run()
    {
        var checks=new List<OfflineCheck>();
        var now=new DateTimeOffset(2026,10,29,10,0,0,TimeSpan.Zero);
        var source=new SourceIdentity(900001,123456,@"C:\offline\LC.exe","0x140000000",1793101316,66609152,
            "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC","offline-startup-identity");
        var expected=new PatrolStartupReadinessContext(source,12345);
        void Check(string name,Action body)
        {
            try{body();checks.Add(new(name,true,"Pure source/fresh selfId snapshot parser; position ignored only for startup identity."));}
            catch(Exception error){checks.Add(new(name,false,error.ToString()));}
        }
        static void Need(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
        HubSnapshot Snapshot(FieldReading? self)=>new(){Source=source,State="Reading",PublishedAt=now,
            Fields=self is null?new(){["position"]=FieldReading.Unknown("position gap")}:new(){["selfId"]=self,["position"]=FieldReading.Unknown("position gap")}};
        Check("startup_identity_fresh_self_source_accepts_independent_position_unknown",()=>
        {
            var observed=LiveStartupIdentityContext.Read(Snapshot(FieldReading.Observed(12345UL,now)),now);
            Need(observed.Context==expected&&!observed.DefinitelyDifferent(expected),"Position Unknown affected startup identity.");
        });
        foreach(string invalid in new[]{"missing","unknown","stale","future","zero","null","string","fraction","negative"})
            Check("startup_identity_"+invalid+"_has_no_ready_context",()=>
            {
                FieldReading? field=invalid switch
                {
                    "missing"=>null,"unknown"=>FieldReading.Unknown("transient"),
                    "stale"=>FieldReading.Observed(12345UL,now.AddMilliseconds(-HubProtocol.FreshMilliseconds-1)),
                    "future"=>FieldReading.Observed(12345UL,now.AddTicks(1)),
                    "zero"=>FieldReading.Observed(0UL,now),"null"=>FieldReading.Observed<object?>(null,now),
                    "string"=>FieldReading.Observed("12345",now),"fraction"=>FieldReading.Observed(12345.5,now),
                    _=>FieldReading.Observed(-1,now)
                };
                var observed=LiveStartupIdentityContext.Read(Snapshot(field),now);
                Need(observed.Context is null&&observed.FreshSelfId is null&&!observed.DefinitelyDifferent(expected),
                    "Missing/stale/non-positive/noninteger identity acquired startup authority or a false identity-change claim.");
            });
        Check("startup_identity_nonnull_source_change_is_definite_even_self_missing",()=>
        {
            var changed=Snapshot(null) with{Source=source with{CreationFileTime=123457}};
            Need(LiveStartupIdentityContext.Read(changed,now).DefinitelyDifferent(expected),"A changed full source was hidden by an unknown self field.");
        });
        Check("startup_identity_fresh_different_self_is_definite_even_source_unknown",()=>
        {
            var changed=Snapshot(FieldReading.Observed(12346UL,now)) with{Source=null};
            Need(LiveStartupIdentityContext.Read(changed,now).DefinitelyDifferent(expected),"A fresh changed self was hidden by unknown source.");
        });
        Check("startup_identity_stale_different_self_is_unknown_not_a_new_role",()=>
        {
            var changed=Snapshot(FieldReading.Observed(12346UL,now.AddSeconds(-10)));
            var observed=LiveStartupIdentityContext.Read(changed,now);
            Need(observed.Context is null&&!observed.DefinitelyDifferent(expected),"An old self sample was treated as a current role change.");
        });
        Check("startup_identity_source_unknown_never_borrows_expected_source",()=>
        {
            var snapshot=Snapshot(FieldReading.Observed(12345UL,now)) with{Source=null};
            var observed=LiveStartupIdentityContext.Read(snapshot,now);
            Need(observed.Context is null&&observed.Source is null&&observed.FreshSelfId==12345UL,
                "Missing source was filled from an expected identity.");
        });
        foreach(string kind in new[]{"disconnected","published_old","published_future","published_missing"})
            Check("startup_identity_"+kind+"_snapshot_has_no_context",()=>
            {
                var snapshot=Snapshot(FieldReading.Observed(12345UL,now));
                snapshot=kind switch { "disconnected"=>snapshot with{State="Disconnected"},
                    "published_old"=>snapshot with{PublishedAt=now.AddMilliseconds(-HubProtocol.FreshMilliseconds-1)},
                    "published_future"=>snapshot with{PublishedAt=now.AddTicks(1)},
                    _=>snapshot with{PublishedAt=default} };
                Need(LiveStartupIdentityContext.Read(snapshot,now).Context is null,"Invalid snapshot acquired identity authority.");
            });
        return checks;
    }
}
