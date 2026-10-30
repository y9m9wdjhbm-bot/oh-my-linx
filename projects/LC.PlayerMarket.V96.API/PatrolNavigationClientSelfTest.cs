using System.Net;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class PatrolNavigationClientSelfTest
{
    internal static IReadOnlyList<OfflineCheck> Run()
    {
        var checks = new List<OfflineCheck>();
        var source = new SourceIdentity(123, 133000000000000000, @"C:\game\LC.exe", "0x140000000", 1, 500000,
            new string('A', 64), "local-scene");
        const ulong ownId = 9007199254740993;
        Dictionary<string, object> Identity() => new()
        {
            ["pid"] = 123, ["creationFileTime"] = "133000000000000000", ["imagePath"] = @"C:\game\LC.exe",
            ["imageBase"] = "0x140000000", ["imageSize"] = 500000, ["sha256"] = new string('A', 64),
            ["selfId"] = ownId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        void Check(string name, bool passed) => checks.Add(new(name, passed, passed ? "通過" : "導航來源或端點核對未符合要求"));
        bool Matches(Dictionary<string, object> value) => PatrolNavigationClient.SourceMatches(JsonSerializer.SerializeToElement(value), source, ownId);
        Check("patrol_wire_preserves_64bit_identity_as_decimal_strings", Matches(Identity()));
        var changed = Identity(); changed["creationFileTime"] = "133000000000000001";
        Check("patrol_wire_same_pid_new_process_rejected", !Matches(changed));
        changed = Identity(); changed["selfId"] = "9007199254740992";
        Check("patrol_wire_different_character_rejected", !Matches(changed));
        changed = Identity(); changed["selfId"] = ownId;
        Check("patrol_wire_numeric_identity_not_rounded_or_coerced", !Matches(changed));
        changed = Identity(); changed["imageBase"] = "0x150000000";
        Check("patrol_wire_module_rebind_rejected", !Matches(changed));
        changed = Identity(); changed["sha256"] = new string('B', 64);
        Check("patrol_wire_changed_game_image_rejected", !Matches(changed));
        changed = Identity(); changed.Remove("imageSize");
        Check("patrol_wire_incomplete_identity_rejected", !Matches(changed));
        Uri Endpoint(Action<UriBuilder>? configure = null)
        {
            var builder = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), 23456);
            configure?.Invoke(builder);
            return builder.Uri;
        }
        Check("patrol_wire_only_exact_loopback_endpoint", PatrolNavigationClient.IsLocalUrl(Endpoint())
            && !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.Host = "localhost"))
            && !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.Host = IPAddress.IPv6Loopback.ToString()))
            && !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.Host = new IPAddress(new byte[] { 192, 0, 2, 10 }).ToString()))
            && !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.Path = "other"))
            && !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.UserName = "user"))
            && !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.Query = "other=1"))
            && !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.Fragment = "other")));
        Check("patrol_wire_version_replacement_128_0_0_1_rejected",
            !PatrolNavigationClient.IsLocalUrl(Endpoint(uri => uri.Host = new IPAddress(new byte[] { 128, 0, 0, 1 }).ToString())));
        var granted = new List<int>();
        bool Grant(int pid) { granted.Add(pid); return true; }
        Check("patrol_foreground_host_grants_only_owned_backend", PatrolForegroundPermission.Prepare(11, 22, 33, 11, Grant)
            && granted.SequenceEqual([33]));
        granted.Clear();
        Check("patrol_foreground_game_needs_no_grant", PatrolForegroundPermission.Prepare(11, 22, 33, 22, Grant) && granted.Count == 0);
        Check("patrol_foreground_foreign_window_does_not_grant", !PatrolForegroundPermission.Prepare(11, 22, 33, 44, Grant)
            && !PatrolForegroundPermission.Prepare(11, 22, 33, 0, Grant) && granted.Count == 0);
        Check("patrol_foreground_denied_grant_is_reported", !PatrolForegroundPermission.Prepare(11, 22, 33, 11, _ => false));
        Check("patrol_foreground_invalid_owner_never_grants", !PatrolForegroundPermission.Prepare(11, 22, 11, 11, Grant)
            && !PatrolForegroundPermission.Prepare(11, 22, 22, 11, Grant)
            && !PatrolForegroundPermission.Prepare(11, 22, 0, 11, Grant) && granted.Count == 0);
        return checks;
    }
}
