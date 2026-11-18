using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShardForge.Core.Patching;

/// <summary>One file a patch delivers into the player's client folder.</summary>
public sealed class PatchFile
{
    /// <summary>File name in the client folder (no folders).</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Size of the finished file in bytes.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>SHA-256 of the finished file (lower-case hex).</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    /// <summary>Absolute download URL.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>"gzip" when the download is gzip-compressed, otherwise null.</summary>
    [JsonPropertyName("compression")] public string? Compression { get; set; }
    /// <summary>Bytes to download (compressed size).</summary>
    [JsonPropertyName("downloadSize")] public long DownloadSize { get; set; }
    /// <summary>The release that first shipped this version of the file.</summary>
    [JsonPropertyName("release")] public string Release { get; set; } = "";
}

/// <summary>
/// A shard's patch: the full list of files players need on top of a stock client. The patcher compares
/// it with the player's folder and downloads only what differs.
/// </summary>
public sealed class PatchManifest
{
    public const string FormatName = "shardforge-patch";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("shard")] public string Shard { get; set; } = "";
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("files")] public List<PatchFile> Files { get; set; } = new();

    public static PatchManifest Parse(string json)
    {
        PatchManifest? m;
        try { m = JsonSerializer.Deserialize(json, PatchJson.Default.PatchManifest); }
        catch (JsonException e) { throw new InvalidDataException("The patch manifest isn't valid JSON: " + e.Message); }
        if (m is null || m.Format != FormatName) throw new InvalidDataException("That isn't a ShardForge patch manifest.");
        if (m.Version != 1) throw new InvalidDataException($"This patcher understands manifest version 1, not {m.Version}. Download a newer patcher.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
        {
            PatchRules.EnsureAllowed(f.Name);
            if (!seen.Add(f.Name)) throw new InvalidDataException($"The manifest lists {f.Name} twice.");
            if (f.Size < 0 || !Regex.IsMatch(f.Sha256, "^[0-9a-f]{64}$")) throw new InvalidDataException($"The manifest entry for {f.Name} is broken.");
            if (!Uri.TryCreate(f.Url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"{f.Name} must be downloaded over https.");
            if (f.Compression is not null and not "gzip") throw new InvalidDataException($"{f.Name} uses an unknown compression '{f.Compression}'.");
        }
        return m;
    }

    public string ToJson() => JsonSerializer.Serialize(this, PatchJson.Default.PatchManifest);
}

/// <summary>What a patch may write: client data files only, straight into the client folder.</summary>
public static class PatchRules
{
    private static readonly string[] Extensions = [".mul", ".uop", ".idx", ".def", ".enu", ".cfg", ".txt"];
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Data files only: no executables or scripts, no folders, no hidden or reserved names. A compromised
    /// manifest still can't drop a program on a player's PC or write outside their client folder.
    /// </summary>
    public static bool IsAllowed(string name) =>
        SafeName.IsMatch(name) && !name.Contains("..") && Extensions.Contains(Path.GetExtension(name).ToLowerInvariant())
        && !name.StartsWith("shardforge", StringComparison.OrdinalIgnoreCase);

    public static void EnsureAllowed(string name)
    {
        if (!IsAllowed(name))
            throw new InvalidDataException($"'{name}' isn't something a patch may install (client data files only: {string.Join(", ", Extensions)}).");
    }
}

/// <summary>The patcher's memory of what it installed, kept in the client folder.</summary>
public sealed class PatchState
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("files")] public Dictionary<string, LocalFileInfo> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files the patcher has installed at least once (their originals are in the backup folder).</summary>
    [JsonPropertyName("patched")] public List<string> Patched { get; set; } = new();
    /// <summary>Files the patch added that didn't exist before (removed again on restore).</summary>
    [JsonPropertyName("added")] public List<string> Added { get; set; } = new();
}

public sealed class LocalFileInfo
{
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime")] public long MTimeTicks { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PatchManifest))]
[JsonSerializable(typeof(PatchState))]
public partial class PatchJson : JsonSerializerContext;
