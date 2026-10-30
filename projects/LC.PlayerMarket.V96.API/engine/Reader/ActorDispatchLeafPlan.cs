namespace MemoryHub.Reader;

/// <summary>Leaf selection only. The caller must still close its original full actor/source proof before publishing.</summary>
internal static class ActorDispatchLeafPlan
{
    internal static void Run<T>(IReadOnlyList<T> actors, ulong? ownerId, Func<T, ulong> id,
        Action<T> readLeaves, Action closeAndPublish)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(readLeaves);
        ArgumentNullException.ThrowIfNull(closeAndPublish);
        if (ownerId is 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(ownerId));
        // All identity work precedes this seam. Recheck duplicate target IDs before any leaf read.
        var chosen = ownerId is null ? actors.ToArray() : actors.Where(actor => id(actor) == ownerId.Value).ToArray();
        if (ownerId is not null && chosen.Length > 1)
            throw new ProbeFailure("ACTOR_ID_AMBIGUOUS", "指定角色 ID 在完整身份集合中不唯一；未讀取或交付目標葉。");
        foreach (var actor in chosen) readLeaves(actor);
        // An absent target still needs the complete original closure. Only then can the
        // service report TargetNoLongerNearby; absence never selects a replacement actor.
        closeAndPublish();
    }
}
