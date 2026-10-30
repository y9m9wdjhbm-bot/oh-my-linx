namespace LcPlayerMarket;

// Product versions share this diagnostics base. Data, pending journals and
// negative item-state ledgers retain their existing locations and semantics.
internal static class MarketLogPaths
{
    internal const string DefaultRoot = @"D:\lineage\LC_PlayerMarket_LOG";

    internal static string Resolve(string? explicitDataRoot = null, string? explicitDiagnosticsRoot = null) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(explicitDiagnosticsRoot
            ?? (explicitDataRoot is null ? DefaultRoot : Path.Combine(explicitDataRoot, "logs"))));

    internal static string Application(string diagnosticsRoot) => Path.Combine(diagnosticsRoot, "application");
    internal static string Navigation(string diagnosticsRoot) => Path.Combine(diagnosticsRoot, "navigation-runtime");
    internal static string RuntimeSession(string diagnosticsRoot, string sessionName) =>
        Path.Combine(diagnosticsRoot, "market-runtime", sessionName);

    internal static string? BackendDiagnostics(string stateRoot, bool production, string? explicitDiagnosticsRoot)
    {
        if (explicitDiagnosticsRoot is not null) return Resolve(explicitDiagnosticsRoot: explicitDiagnosticsRoot);
        if (!production) return null; // Fake ports keep their explicitly isolated state directory.
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        // Pending records retain an absolute NativeLog path. A reopened backend
        // must use the same directory so exact-path recovery can find that proof.
        // Windows absolute lexical normalization preserves case/trailing-dot aliases.
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot)).ToUpperInvariant();
        string stateKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
        return RuntimeSession(Resolve(), "standalone-state-" + stateKey);
    }
}