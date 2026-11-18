using System.Net;
using ShardForge.Core.Patching;

namespace ShardForge.Tests;

/// <summary>Serves files from memory, like GitHub release downloads, and counts requests.</summary>
sealed class FakeWeb : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<string> Requests { get; } = new();
    public Func<string, byte[], byte[]>? Tamper { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        Requests.Add(url);
        if (!Files.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (Tamper is not null) bytes = Tamper(url, bytes);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}

public sealed class PatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sf-patch-" + Guid.NewGuid().ToString("N")[..8]);
    private string Src => Path.Combine(_root, "src");
    private string Client => Path.Combine(_root, "client");
    private readonly FakeWeb _web = new();
    private const string ManifestUrl = "https://example.test/latest/manifest.json";

    public PatchTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Client);
        File.WriteAllBytes(Path.Combine(Src, "map4LegacyMUL.uop"), Bytes(200_000, 1));
        File.WriteAllBytes(Path.Combine(Src, "statics4.mul"), Bytes(50_000, 2));
        File.WriteAllBytes(Path.Combine(Client, "map4LegacyMUL.uop"), Bytes(180_000, 9)); // the stock file
        File.WriteAllBytes(Path.Combine(Client, "client.exe"), [1, 2, 3]);
    }

    private static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); for (int i = 0; i < n; i += 7) b[i] = 0; return b; }

    /// <summary>Makes a release from the source folder and "uploads" it to the fake web.</summary>
    private PatchBuild Publish(string release, PatchManifest? previous = null)
    {
        string outDir = Path.Combine(_root, "out-" + release);
        var build = PatchMaker.Make(Src, outDir, "Test Shard", release, asset => $"https://example.test/{release}/{asset}", previous);
        foreach (var up in build.Uploads) _web.Files[$"https://example.test/{release}/{up.Name}"] = File.ReadAllBytes(up.Path);
        _web.Files[ManifestUrl] = File.ReadAllBytes(Path.Combine(outDir, "manifest.json"));
        return build;
    }

    private PatchClient NewClient() => new(new HttpClient(_web), Client) { RetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task InstallsChangedFilesAndBacksUpTheOriginalsOnce()
    {
        Publish("r1");
        var client = NewClient();
        var result = await client.Update(await client.FetchManifest(ManifestUrl));

        Assert.Equal(["map4LegacyMUL.uop", "statics4.mul"], result.Updated.Order());
        Assert.Equal(File.ReadAllBytes(Path.Combine(Src, "map4LegacyMUL.uop")), File.ReadAllBytes(Path.Combine(Client, "map4LegacyMUL.uop")));
        Assert.Equal(Bytes(180_000, 9), File.ReadAllBytes(Path.Combine(Client, PatchClient.BackupDir, "map4LegacyMUL.uop")));
        Assert.True(result.BytesDownloaded < 250_000 * 0.95); // gzip-compressed downloads
    }

    [Fact]
    public async Task ASecondRunDownloadsNothing()
    {
        Publish("r1");
        var client = NewClient();
        await client.Update(await client.FetchManifest(ManifestUrl));
        _web.Requests.Clear();
        var again = await client.Update(await client.FetchManifest(ManifestUrl));
        Assert.Empty(again.Updated);
        Assert.Equal([ManifestUrl], _web.Requests);
    }

    [Fact]
    public async Task NewReleasesShipOnlyWhatChanged()
    {
        var r1 = Publish("r1");
        var client = NewClient();
        await client.Update(await client.FetchManifest(ManifestUrl));

        File.WriteAllBytes(Path.Combine(Src, "statics4.mul"), Bytes(51_000, 3));
        var r2 = Publish("r2", r1.Manifest);
        Assert.Equal(["statics4.mul"], r2.Changed);
        Assert.Equal("r1", r2.Manifest.Files.Single(f => f.Name == "map4LegacyMUL.uop").Release); // still downloaded from r1

        _web.Requests.Clear();
        var result = await client.Update(await client.FetchManifest(ManifestUrl));
        Assert.Equal(["statics4.mul"], result.Updated);
        Assert.Contains("https://example.test/r2/statics4.mul.gz", _web.Requests);
        // The backup still holds the stock file from before the first patch.
        Assert.Equal(Bytes(180_000, 9), File.ReadAllBytes(Path.Combine(Client, PatchClient.BackupDir, "map4LegacyMUL.uop")));
    }

    [Fact]
    public async Task DamagedDownloadsNeverReplaceTheOriginal()
    {
        Publish("r1");
        _web.Tamper = (url, bytes) => url.EndsWith(".gz") ? bytes.Take(bytes.Length / 2).ToArray() : bytes;
        var client = NewClient();
        var manifest = await client.FetchManifest(ManifestUrl);
        await Assert.ThrowsAnyAsync<Exception>(() => client.Update(manifest));
        Assert.Equal(Bytes(180_000, 9), File.ReadAllBytes(Path.Combine(Client, "map4LegacyMUL.uop")));
        Assert.Equal(3, _web.Requests.Count(u => u.EndsWith("map4LegacyMUL.uop.gz"))); // retried, then gave up
    }

    [Fact]
    public async Task RestorePutsTheClientBackAsItWas()
    {
        Publish("r1");
        var client = NewClient();
        await client.Update(await client.FetchManifest(ManifestUrl));
        client.Restore();
        Assert.Equal(Bytes(180_000, 9), File.ReadAllBytes(Path.Combine(Client, "map4LegacyMUL.uop")));
        Assert.False(File.Exists(Path.Combine(Client, "statics4.mul"))); // it was added by the patch
        Assert.True(File.Exists(Path.Combine(Client, "client.exe")));
    }

    [Theory]
    [InlineData("client.exe")]
    [InlineData("ClassicUO.dll")]
    [InlineData("../evil.mul")]
    [InlineData("sub/map0.mul")]
    [InlineData("run.bat")]
    [InlineData(".hidden.mul")]
    public void OnlyClientDataFilesArePatchable(string name)
    {
        Assert.False(PatchRules.IsAllowed(name));
        string json = $$"""{"format":"shardforge-patch","version":1,"files":[{"name":"{{name.Replace("\\", "\\\\")}}","size":1,"sha256":"{{new string('a', 64)}}","url":"https://x.test/a"}]}""";
        Assert.Throws<InvalidDataException>(() => PatchManifest.Parse(json));
    }

    [Fact]
    public void ManifestsMustUseHttps()
    {
        string json = $$"""{"format":"shardforge-patch","version":1,"files":[{"name":"map0.mul","size":1,"sha256":"{{new string('a', 64)}}","url":"http://x.test/a"}]}""";
        Assert.Throws<InvalidDataException>(() => PatchManifest.Parse(json));
        Assert.True(PatchRules.IsAllowed("map0LegacyMUL.uop") && PatchRules.IsAllowed("staidx5x.mul") && PatchRules.IsAllowed("Cliloc.enu"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
