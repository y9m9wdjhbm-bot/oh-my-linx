namespace LcPlayerMarket;
internal static class PatrolStartPreparation
{
    internal static void Execute(Action preparation, Action<Exception> failed)
    {
        try { preparation(); }
        catch (Exception error) { failed(error); throw; }
    }
}
