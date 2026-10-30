namespace LcPlayerMarket;

internal static class LiveAutomaticPageOutcomePolicy
{
    // Empty observations are successful workflow outcomes, without becoming matched responses.
    // A completed four-point round can contain no requests when every point had no eligible shops.
    internal static bool Accept(int total, int matched, int empty, int failed, bool completedRoundWithoutEligibleShops)
    {
        if (total < 0 || matched < 0 || empty < 0 || failed < 0 || matched > total || empty > total || failed > total || failed != 0)
            return false;
        return total == 0 ? completedRoundWithoutEligibleShops : matched + empty > 0;
    }

    internal static OfflineCheck[] RunSelfTest()
    {
        var cases = new[] {
            ("matched", 1, 1, 0, 0, false, true),
            ("empty_only", 1, 0, 1, 0, false, true),
            ("matched_and_empty", 2, 1, 1, 0, false, true),
            ("completed_round_no_eligible_shops", 0, 0, 0, 0, true, true),
            ("zero_pages_without_completed_round", 0, 0, 0, 0, false, false),
            ("not_entered_only_has_no_page_response", 2, 0, 0, 0, false, false),
            ("failure_does_not_become_empty", 2, 1, 0, 1, false, false),
            ("negative_count", -1, 0, 0, 0, true, false),
            ("response_count_above_total", 0, 1, 0, 0, true, false)
        };
        return cases.Select(c => new OfflineCheck("live_page_outcome_" + c.Item1,
            Accept(c.Item2, c.Item3, c.Item4, c.Item5, c.Item6) == c.Item7,
            "Pure workflow acceptance only; empty is not a matched response and native/cleanup/source proof remains separate.")).ToArray();
    }
}
