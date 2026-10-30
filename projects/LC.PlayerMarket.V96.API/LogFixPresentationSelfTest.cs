using MemoryHub;
namespace LcPlayerMarket;
public static class LogFixPresentationSelfTest
{
    public static OfflineCheck[] Run()
    {
        var checks=new List<OfflineCheck>();
        void Check(string name,Action body){try{body();checks.Add(new(name,true,"Passed"));}catch(Exception e){checks.Add(new(name,false,e.Message));}}
        void Need(bool ok,string detail){if(!ok)throw new InvalidOperationException(detail);}
        var scan=new MarketScanState{JobId="fixture-scan",Total=88,Completed=10,CompletedShops=4,TotalShops=44,
            Succeeded=6,EmptyObservedPages=3,Failed=1,FailureCode="NativeCodeUnavailable",FailureRva="0x7710F0",
            FailureDetail="Unknown unpublished phase",StopReason="SessionBlocked"};
        var unknown=new ShopCommandResponse{State="State",FailureCode="NativeCodeUnavailable",FailurePhase="Native",
            FailureScope="Global",FailureRva="0x7710F0",Detail="Unknown unpublished phase",SessionBlocked=true,
            WorkSettled=true,NativeEntered=false,NativeReturned=false,Scan=scan};
        Check("recorded_blocked_page_does_not_claim_no_dispatch",()=>{
            string s=MarketFailurePresentation.Status(unknown,true);
            Need(!s.Contains("本頁查詢尚未送出") && s.Contains("結果仍未確認"),"Blocked outcome falsely claims never dispatched");
            Need(s.Contains("確認 6、未讀到商品 3（方向未確認）、失敗或未確認 1"),"Prior observations/counts changed");
            Need(!s.Contains("可直接下達下一指令"),"Blocked request must not recommend next input");
        });
        Check("unsettled_native_refusal_does_not_claim_no_dispatch",()=>{
            string s=MarketFailurePresentation.Format(unknown with{SessionBlocked=false,WorkSettled=false},true);
            Need(!s.Contains("尚未送出") && s.Contains("結果仍未確認"),"Unsettled refusal overclaims");
            Need(!s.Contains("不會阻擋下一個使用者指令"),"Unsettled request suggests another command");
        });
        Check("entered_code_failure_does_not_claim_no_dispatch",()=>{
            string s=MarketFailurePresentation.Format(unknown with{SessionBlocked=false,WorkSettled=true,NativeEntered=true,NativeReturned=true});
            Need(!s.Contains("尚未送出"),"Entered request called undispatched");
        });
        Check("settled_refusal_retains_items_and_avoids_empty_shop_claim",()=>{
            string s=MarketFailurePresentation.Format(unknown with{State="NativeRejected",SessionBlocked=false,WorkSettled=true,FailurePhase="Preflight"});
            Need(s.Contains("先前讀到的商品仍保留") && s.Contains("這不代表商店沒有商品"),"Data preservation/empty distinction lost");
        });
        Check("scan_only_unknown_does_not_borrow_current_success",()=>{
            string s=MarketFailurePresentation.Format(unknown with{FailureCode=null,SessionBlocked=false,WorkSettled=true});
            Need(!s.Contains("尚未送出"),"Prior scan without no-entry proof called undispatched");
        });
        return checks.ToArray();
    }
}
