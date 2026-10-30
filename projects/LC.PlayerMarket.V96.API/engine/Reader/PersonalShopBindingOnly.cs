namespace MemoryHub.Reader;

internal sealed partial class PersonalShopSampler
{
    // A native argument binding, not a page/content observation. Every pointer,
    // owner scalar and subsystem lifetime is freshly closed in this same pass.
    private PersonalShopListing FinishBindingOnly(DateTimeOffset sampledAt, ulong root, ulong ui,
        ulong shop, ulong owner)
    {
        CloseProofs("商店 binding-only 完整 B");
        var previous = journal.SourceClosure;
        journal.SourceClosure = () =>
        {
            previous?.Invoke();
            CloseProofs("跨資料組商店 binding-only 最終 B");
            journal.RequireScene(director, scene);
        };
        string own = journal.SelfId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        string session = $"{memory.ProcessId}:{memory.CreationFileTime}:{memory.Image:X}:{director:X}:{scene:X}:{own}";
        ActionSource = new(session, sampledAt, shop, owner, root, ui, director, scene) { BindingOnly = true };
        return new()
        {
            SourceSessionKey = session, SampledAt = sampledAt, OwnerIdRaw = owner,
            LayoutState = "BindingOnly", Complete = false, PageModeState = "NotSampledBindingOnly", BankState = "NotSampled",
            Rows = [], SlotRows = [], RawBankSummary = [],
            Evidence = "FreshSourceSelfAndShopOwnerBindingABClosed;BanksRowsProductsNotSampled;NotAnEmptyPageObservation"
        };
    }
}
