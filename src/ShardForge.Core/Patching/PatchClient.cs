using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShardForge.Core.Patching;

public enum PatchStep { CheckingManifest, Hashing, Downloading, Installing, UpToDate, Done }

public sealed record PatchProgress(PatchStep Step, string? File = null, long Done = 0, long Total = 0, int Index = 0, int Count = 0);

public sealed record PatchResult(string Release, IReadOnlyList<string> Updated, IReadOnlyList<string> AlreadyCurrent, long BytesDownloaded);

/// <summary>
/// The patcher's engine: brings a client folder up to a manifest. Downloads go to a temporary file, are
/// size- and SHA-256-checked, and only then replace the original (which is backed up once, the first time).
/// </summary>
public sealed class PatchClient(HttpClient http, string clientDir)
{
    public const string StateFile = ".shardforge-patch.json";
    public const string BackupDir = "ShardForge-backup";
    private const string TempDir = ".shardforge-tmp";

    /// <summary>Wait before retrying a failed download (doubles per attempt).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<PatchManifest> FetchManifest(string url, CancellationToken ct = default)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Couldn't get the patch list ({(int)res.StatusCode} {res.ReasonPhrase}) from {url}.");
        return PatchManifest.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Which manifest files differ from the client folder.</summary>
    public List<PatchFile> Outdated(PatchManifest manifest, IProgress<PatchProgress>? progress = null)
    {
        var state = LoadState();
        var list = new List<PatchFile>();
        int i = 0;
        foreach (var f in manifest.Files)
        {
            progress?.Report(new PatchProgress(PatchStep.Hashing, f.Name, Index: ++i, Count: manifest.Files.Count));
            string path = Path.Combine(clientDir, f.Name);
            if (!File.Exists(path) || LocalHash(path, state) != f.Sha256) list.Add(f);
        }
        SaveState(state); // keeps freshly computed hashes so the next run is instant
        return list;
    }

    public async Task<PatchResult> Update(PatchManifest manifest, IProgress<PatchProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureClientFolder();
        var outdated = Outdated(manifest, progress);
        var current = manifest.Files.Select(f => f.Name).Except(outdated.Select(f => f.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (outdated.Count == 0)
        {
            progress?.Report(new PatchProgress(PatchStep.UpToDate));
            var s = LoadState(); s.Release = manifest.Release; SaveState(s);
            return new PatchResult(manifest.Release, [], current, 0);
        }

        string temp = Path.Combine(clientDir, TempDir);
        Directory.CreateDirectory(temp);
        long downloaded = 0, total = outdated.Sum(f => f.DownloadSize > 0 ? f.DownloadSize : f.Size);
        var updated = new List<string>();
        int index = 0;
        foreach (var f in outdated)
        {
            index++;
            string part = Path.Combine(temp, f.Name + ".part");
            downloaded += await Download(f, part, d => progress?.Report(new PatchProgress(PatchStep.Downloading, f.Name, downloaded + d, total, index, outdated.Count)), ct);
            progress?.Report(new PatchProgress(PatchStep.Installing, f.Name, Index: index, Count: outdated.Count));
            Install(f, part);
            updated.Add(f.Name);
        }
        try { Directory.Delete(temp, true); } catch (IOException) { }
        var state = LoadState(); state.Release = manifest.Release; SaveState(state);
        progress?.Report(new PatchProgress(PatchStep.Done));
        return new PatchResult(manifest.Release, updated, current, downloaded);
    }

    /// <summary>Puts back every original the patcher replaced and removes files it added.</summary>
    public IReadOnlyList<string> Restore()
    {
        var state = LoadState();
        var restored = new List<string>();
        string backups = Path.Combine(clientDir, BackupDir);
        if (Directory.Exists(backups))
            foreach (var b in Directory.GetFiles(backups))
            {
                string name = Path.GetFileName(b);
                if (!PatchRules.IsAllowed(name)) continue;
                File.Copy(b, Path.Combine(clientDir, name), overwrite: true);
                restored.Add(name);
            }
        foreach (var name in state.Added.Where(PatchRules.IsAllowed))
        {
            string path = Path.Combine(clientDir, name);
            if (File.Exists(path)) { File.Delete(path); restored.Add(name + " (removed)"); }
        }
        File.Delete(Path.Combine(clientDir, StateFile));
        return restored;
    }

    private async Task<long> Download(PatchFile f, string part, Action<long> onBytes, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                await using var net = await res.Content.ReadAsStreamAsync(ct);
                var counting = new CountingStream(net, onBytes);
                await using Stream body = f.Compression == "gzip" ? new GZipStream(counting, CompressionMode.Decompress) : counting;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long written = 0;
                await using (var file = File.Create(part))
                {
                    var buffer = new byte[1 << 16];
                    int n;
                    while ((n = await body.ReadAsync(buffer, ct)) > 0)
                    {
                        written += n;
                        if (written > f.Size) throw new InvalidDataException($"{f.Name} is bigger than the patch says.");
                        sha.AppendData(buffer, 0, n);
                        await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                }
                string got = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (written != f.Size || got != f.Sha256)
                    throw new InvalidDataException($"{f.Name} arrived damaged (checksum mismatch).");
                return counting.Count;
            }
            catch (Exception e) when (attempt < 3 && e is HttpRequestException or IOException or InvalidDataException && !ct.IsCancellationRequested)
            {
                File.Delete(part);
                await Task.Delay(RetryDelay * attempt, ct);
            }
            catch
            {
                File.Delete(part);
                throw;
            }
        }
    }

    private void Install(PatchFile f, string part)
    {
        string target = Path.Combine(clientDir, f.Name);
        var state = LoadState();
        bool firstTime = !state.Patched.Contains(f.Name, StringComparer.OrdinalIgnoreCase);
        if (File.Exists(target))
        {
            // Keep the original from before the first patch only; later updates don't overwrite it.
            string backups = Path.Combine(clientDir, BackupDir), backup = Path.Combine(backups, f.Name);
            if (firstTime && !File.Exists(backup)) { Directory.CreateDirectory(backups); File.Copy(target, backup); }
        }
        else if (firstTime && !state.Added.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) state.Added.Add(f.Name);
        if (firstTime) state.Patched.Add(f.Name);
        File.Move(part, target, overwrite: true);
        var info = new FileInfo(target);
        state.Files[f.Name] = new LocalFileInfo { Size = info.Length, MTimeTicks = info.LastWriteTimeUtc.Ticks, Sha256 = f.Sha256 };
        SaveState(state);
    }

    private string LocalHash(string path, PatchState state)
    {
        var info = new FileInfo(path);
        string name = Path.GetFileName(path);
        if (state.Files.TryGetValue(name, out var cached) && cached.Size == info.Length && cached.MTimeTicks == info.LastWriteTimeUtc.Ticks)
            return cached.Sha256;
        string sha = PatchMaker.Sha256File(path);
        state.Files[name] = new LocalFileInfo { Size = info.Length, MTimeTicks = info.LastWriteTimeUtc.Ticks, Sha256 = sha };
        return sha;
    }

    private void EnsureClientFolder()
    {
        if (!Directory.Exists(clientDir)) throw new DirectoryNotFoundException($"The client folder {clientDir} doesn't exist.");
    }

    public PatchState LoadState()
    {
        string path = Path.Combine(clientDir, StateFile);
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), PatchJson.Default.PatchState) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private void SaveState(PatchState state) =>
        File.WriteAllText(Path.Combine(clientDir, StateFile), JsonSerializer.Serialize(state, PatchJson.Default.PatchState));

    private sealed class CountingStream(Stream inner, Action<long> onBytes) : Stream
    {
        public long Count { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); Add(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { int n = await inner.ReadAsync(buffer, ct); Add(n); return n; }
        private void Add(int n) { Count += n; onBytes(Count); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
