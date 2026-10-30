using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Reader;

public sealed record TemplateImportResult(string ProfileId, string SourceSha256, string TemplateSha256,
    int CandidateFields, string SavedDirectory);

/// <summary>Versioned local template imports. Does not open LC or authorize a native action.</summary>
public static class TemplateProfileImport
{
    private const string Prefix = "template-import-";

    public static TemplateImportResult Import(string sourcePath, string profileDirectory)
    {
        byte[] bytes;
        using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is < 1 or > LcLocationTemplate.ProfileLoader.MaximumJsonBytes)
                throw new InvalidDataException("模板檔案為空或超出大小限制。");
            bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        }
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        // Verify the exact saved bytes before writing the activation pointer.
        Directory.CreateDirectory(profileDirectory);
        string directory = Path.Combine(profileDirectory, Prefix + DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string name = "template-profile-" + sha[..12] + ".json";
        WriteNew(Path.Combine(directory, name), bytes);
        var candidate = TemplateReaderProfileAdapter.LoadCandidate(Path.Combine(directory, name), sha);
        var pointer = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, fileName = name, sha256 = sha }, HubProtocol.Json);
        WriteNew(Path.Combine(directory, TemplateReaderProfileAdapter.PointerFile), pointer);
        return new(candidate.Id, candidate.Sha256, sha, candidate.AllowedFields.Count, directory);
    }

    internal static ReaderProfile? LoadLatest(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        string[] imports = Directory.EnumerateDirectories(directory, Prefix + "*", SearchOption.TopDirectoryOnly).Take(257).ToArray();
        if (imports.Length > 256) throw new InvalidDataException("模板版本數量超界，請整理保存檔後再啟動。");
        var committed = imports.Where(p => File.Exists(Path.Combine(p, TemplateReaderProfileAdapter.PointerFile)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
        return committed is null ? null : TemplateReaderProfileAdapter.Load(committed);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes); file.Flush(true);
    }
}
