using System.Text.Json;

namespace MemoryHub.Service;

internal sealed partial record ShopRuntimeContext
{
    private static bool CollectionScopeMatches(JsonElement value, ulong? ownerId)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (ownerId is null)
        {
            // An accidental Complete=true must not promote private partial data to
            // a public complete-list contract used by refresh/background consumers.
            return True(value, "complete") && Text(value, "scope") != "SelectedDispatchTarget"
                && (!value.TryGetProperty("selectedOwnerId", out var selected) || selected.ValueKind == JsonValueKind.Null);
        }
        if (ownerId is 0 or > int.MaxValue || Text(value, "scope") != "SelectedDispatchTarget"
            || !value.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.False
            || !True(value, "fullIdentityCollectionVerified")
            || !Id(value, "selectedOwnerId", out ulong markedOwner) || markedOwner != ownerId
            || !Array(value, "items", out var items) || items.GetArrayLength() > 1) return false;
        // Zero rows are a closed observation of target absence, handled by RequireOwner.
        foreach (var row in items.EnumerateArray())
            if (!Id(row, "id", out ulong rowOwner) || rowOwner != ownerId) return false;
        return true;
    }
}
