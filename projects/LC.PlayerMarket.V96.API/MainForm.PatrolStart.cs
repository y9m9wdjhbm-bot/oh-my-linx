namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private readonly Button startSavedPatrolButton = Button("開始巡迴");
    private bool homePatrolRequested;

    private async Task StartOrStopSavedPatrolAsync()
    {
        if (!SearchMutationAllowed && !patrolRunning) { if (resettingSearch) CancelSearchRestart(); return; }
        try
        {
            if (patrolRunning)
            {
                if (patrolStopRequested) return;
                var stopping = StopPatrolAsync();
                UpdateButtons();
                await stopping;
                return;
            }
            if (PatrolActive) return;
            homePatrolRequested = true;
            var loaded = patrolRoutes.Load();
            if (loaded.Error is not null || loaded.Snapshot is null)
            {
                throw new InvalidDataException(loaded.Error ?? "無法讀取保存的巡迴點，尚未開始巡迴。");
            }
            if (loaded.Snapshot.Points.Length == 0)
            {
                patrolStatus = "尚未保存巡迴點，請先加入定位點；設定完成後可直接從首頁開始巡迴。";
                Report("PATROL_HOME_SETUP_REQUIRED", patrolStatus);
                ShowPatrolRoute();
                return;
            }
            PatrolRouteRules.Validate(loaded.Snapshot);
            Report("PATROL_HOME_START", "從首頁使用已保存的定位點依設定輪數開始巡迴。",
                new { loaded.Snapshot.Revision, loaded.Snapshot.RoundCount, PointCount = loaded.Snapshot.Points.Length, loaded.Snapshot.RetryLimit, loaded.Snapshot.ArrivalRadius });
            await RunPatrolAsync(loaded.Snapshot);
        }
        catch (Exception error)
        {
            PresentPatrolStartFailure(error);
        }
        finally { if (!closing) UpdateButtons(); }
    }
}
