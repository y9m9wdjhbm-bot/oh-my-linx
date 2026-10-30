using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Service;

internal static class ShopResidentWorkerPackage
{
    internal const string WorkerFile = "LC_PlayerShopListActionWorkerV18.exe";
    internal const string RuntimeInventoryFile = "worker-runtime-files-v1.json";
    internal const string RuntimeInventorySha256 = "BDA3677F472D597CE8A98A6B5BBB8C15001005232A5446A8110BB6BACDEFB119";
    internal const int RuntimeInventoryFiles = 193;
    // Filled only by the root's administrator worker-build/publication receipt.
    internal static readonly (string Name, string Sha256)[] ExpectedFiles =
    [
        ("LC_PlayerShopListActionWorkerV18.exe", "66A6664AF024D2BA7C9B375AC964971C7F3723E0C2BA3E6BB7AE4A04107C6874"),
        ("LC_PlayerShopListActionWorkerV18.dll", "BB68EBC690E7E3B089F531E040931C55143E51D8C49EAD0AB9116EE3D0D35F45"),
        ("LC_PlayerShopListActionWorkerV18.deps.json", "C608BECE72739BD249E194F186555C3B43E0EF1B4DA3F7874F92FA1AA6344EC9"),
        ("LC_PlayerShopListActionWorkerV18.runtimeconfig.json", "75C22507A5F93B05B500DF30F602C39F675E05A13A3FF6EBEFD679C23867D940"),
        (ShopNativePackage.NativePayloadFile, ShopNativePackage.NativePayloadSha256)
    ];
    internal static IDisposable Pin(string directory)
    {
        if (RuntimeInventorySha256.Length != 64 || !RuntimeInventorySha256.All(Uri.IsHexDigit)
            || ExpectedFiles.Any(x => x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("RESIDENT_WORKER_PACKAGE_NOT_CERTIFIED");
        var opened = new List<FileStream>();
        try
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var manifest = new FileStream(Path.Combine(directory, RuntimeInventoryFile), FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Add(manifest);
            if (Convert.ToHexString(SHA256.HashData(manifest)) != RuntimeInventorySha256)
                throw new InvalidDataException("RESIDENT_RUNTIME_INVENTORY_HASH_CHANGED");
            manifest.Position = 0;
            using var doc = JsonDocument.Parse(manifest);
            if (doc.RootElement.GetProperty("Schema").GetString() != "LC.NativeWorker.Package.V18.1"
                || doc.RootElement.GetProperty("FilesCount").GetInt32() != RuntimeInventoryFiles)
                throw new InvalidDataException("RESIDENT_RUNTIME_MANIFEST_SCHEMA");
            var files = doc.RootElement.GetProperty("Files").EnumerateArray().ToArray();
            foreach (var folder in Directory.EnumerateDirectories(directory,"*",SearchOption.AllDirectories).Prepend(directory))
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("RESIDENT_RUNTIME_REPARSE_DIRECTORY");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if (files.Length != RuntimeInventoryFiles) throw new InvalidDataException("RESIDENT_RUNTIME_FILE_COUNT");
            foreach (var expected in files)
            {
                string name = expected.GetProperty("RelativePath").GetString()!;
                string hash = expected.GetProperty("Sha256").GetString()!;
                string path = Path.GetFullPath(Path.Combine(directory, name));
                if (Path.IsPathRooted(name) || !path.StartsWith(root,StringComparison.OrdinalIgnoreCase)
                    || !names.Add(path) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)
                    || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("RESIDENT_RUNTIME_FILE_PATH_OR_DUPLICATE");
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                opened.Add(stream);
                if (stream.Length != expected.GetProperty("Length").GetInt64() || Convert.ToHexString(SHA256.HashData(stream)) != hash)
                    throw new InvalidOperationException("RESIDENT_WORKER_PACKAGE_HASH_CHANGED:" + name);
                hashes.Add(name.Replace('/','\\'),hash);
            }
            string manifestPath=Path.GetFullPath(Path.Combine(directory,RuntimeInventoryFile));
            var actual=Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories)
                .Select(Path.GetFullPath).Where(p=>!string.Equals(p,manifestPath,StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actual.SetEquals(names)) throw new InvalidDataException("RESIDENT_RUNTIME_FILE_SET_CHANGED");
            foreach (var expected in ExpectedFiles)
                if (!hashes.TryGetValue(expected.Name,out string? actualHash) || actualHash!=expected.Sha256)
                    throw new InvalidDataException("RESIDENT_RUNTIME_PRIMARY_HASH_MISMATCH:"+expected.Name);
            return new HeldFiles(opened);
        }
        catch { foreach (var stream in opened) stream.Dispose(); throw; }
    }
    private sealed record HeldFiles(List<FileStream> Files) : IDisposable
    { public void Dispose() { foreach (var file in Files) file.Dispose(); } }
}
