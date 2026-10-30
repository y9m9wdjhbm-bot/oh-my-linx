using MemoryHub;

namespace LcPlayerMarket;

// Accepted navigation receipts keep their position for patrol presentation.
// This data has no map renderer or terrain provider and grants no command admission.
internal sealed record VerifiedDisplayPose(SourceIdentity Source, ulong SelfId, int MapId, int X, int Y,
    DateTimeOffset SampledAt, string? JobId = null, int? TargetX = null, int? TargetY = null, int? TargetMapId = null)
{
    internal bool Fresh(DateTimeOffset now) => SampledAt <= now && now - SampledAt <= TimeSpan.FromSeconds(2)
        && SelfId > 0 && MapId >= 0 && X is >= 0 and <= 65535 && Y is >= 0 and <= 65535;
}
