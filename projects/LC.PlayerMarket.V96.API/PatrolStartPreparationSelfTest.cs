namespace LcPlayerMarket;
internal static class PatrolStartPreparationSelfTest
{
    internal static int Run(string output)
    {
        var checks = new List<object>(); int failed = 0;
        foreach (string entry in new[] { "home", "route" })
        foreach (string previous in new[] { "Completed", "Failed" })
        {
            var oldAt = DateTimeOffset.UtcNow.AddMinutes(-1); var now = oldAt.AddMinutes(1);
            SearchOverlayView? view = PatrolOverlayPresentation.Build(previous, "old reason", oldAt);
            int backendCalls = 0; string reason = entry + " injected identity failure";
            try { PatrolStartPreparation.Execute(() => throw new InvalidOperationException(reason), error =>
                view = PatrolOverlayPresentation.Build("Failed", error.Message, now)); backendCalls++; }
            catch (InvalidOperationException) { }
            bool passed = view?.PatrolState == "Failed" && view.TransitionAt == now && view.Detail.Contains(reason)
                && !view.Detail.Contains("old reason") && backendCalls == 0;
            if (!passed) failed++;
            checks.Add(new { Entry = entry, Previous = previous, Passed = passed, BackendCalls = backendCalls });
        }
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { Success = failed == 0, Passed = 4-failed,
            Failed = failed, GuiStarted = false, GameOpened = false, ForegroundChanged = false, BackendStarted = false, Checks = checks }));
        return failed == 0 ? 0 : 1;
    }
}
