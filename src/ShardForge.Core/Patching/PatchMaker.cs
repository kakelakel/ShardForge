using System.IO.Compression;
using System.Security.Cryptography;

namespace ShardForge.Core.Patching;

/// <summary>A file to upload for a new release (gzip-compressed).</summary>
public sealed record PatchAsset(string Name, string Path, long Size);

public sealed record PatchBuild(PatchManifest Manifest, IReadOnlyList<PatchAsset> Uploads, IReadOnlyList<string> Unchanged, IReadOnlyList<string> Changed);

/// <summary>
/// Builds a release: hashes every file in the shard's patch folder, reuses entries for files that
/// haven't changed since the previous release, and gzips the new or changed ones for upload.
/// </summary>
public static class PatchMaker
{
    /// <param name="sourceDir">Folder with the files players should have (flat).</param>
    /// <param name="outDir">Where to write the gzipped uploads and manifest.json.</param>
    /// <param name="urlFor">Download URL for an asset of this release, e.g. a GitHub release download link.</param>
    public static PatchBuild Make(string sourceDir, string outDir, string shard, string release, Func<string, string> urlFor,
        PatchManifest? previous = null, string? notes = null)
    {
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException($"No folder {sourceDir}.");
        Directory.CreateDirectory(outDir);
        var prev = previous?.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase) ?? new();
        var manifest = new PatchManifest { Shard = shard, Release = release, Created = DateTime.UtcNow.ToString("O"), Notes = notes };
        var uploads = new List<PatchAsset>();
        var unchanged = new List<string>();
        var changed = new List<string>();

        var files = Directory.GetFiles(sourceDir).Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.StartsWith('.') && !n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) throw new InvalidDataException($"{sourceDir} has no files to publish.");

        foreach (var name in files)
        {
            PatchRules.EnsureAllowed(name);
            string path = Path.Combine(sourceDir, name);
            long size = new FileInfo(path).Length;
            string sha = Sha256File(path);
            if (prev.TryGetValue(name, out var old) && old.Sha256 == sha && old.Size == size)
            {
                manifest.Files.Add(old);
                unchanged.Add(name);
                continue;
            }
            string asset = name + ".gz", assetPath = Path.Combine(outDir, asset);
            using (var input = File.OpenRead(path))
            using (var output = File.Create(assetPath))
            using (var gz = new GZipStream(output, CompressionLevel.SmallestSize))
                input.CopyTo(gz);
            long gzSize = new FileInfo(assetPath).Length;
            uploads.Add(new PatchAsset(asset, assetPath, gzSize));
            changed.Add(name);
            manifest.Files.Add(new PatchFile
            {
                Name = name, Size = size, Sha256 = sha, Url = urlFor(asset), Compression = "gzip", DownloadSize = gzSize, Release = release,
            });
        }

        File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToJson());
        PatchManifest.Parse(manifest.ToJson()); // never ship a manifest the patcher would reject
        return new PatchBuild(manifest, uploads, unchanged, changed);
    }

    public static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
