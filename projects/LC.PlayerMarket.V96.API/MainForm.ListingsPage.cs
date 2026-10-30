namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private readonly Button showListingsButton = Button("買賣清單");
    private TabControl marketPages = null!;

    private void ShowMarketListings(string reason)
    {
        if (closing || IsDisposed || marketPages.IsDisposed) return;
        marketPages.SelectedIndex = 0;
        Report("LISTINGS_PAGE_SHOWN", "已回到買／賣商品清單；保存資料與篩選條件維持。",
            new { reason, marketPages.SelectedIndex }, show: false);
    }
}
