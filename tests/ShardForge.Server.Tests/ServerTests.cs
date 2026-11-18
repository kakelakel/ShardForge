using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ShardForge.Core.Map;
using ShardForge.Core.Project;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ShardForge.Server.Tests;

/// <summary>Runs the real server in-process with a temporary data folder and a tiny 16x16 map.</summary>
public sealed class ServerFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sf-server-" + Guid.NewGuid().ToString("N")[..8]);
    public WebApplicationFactory<Program> Factory { get; }

    public ServerFixture()
    {
        string client = Path.Combine(Root, "client"), maps = Path.Combine(Root, "mapdir");
        Directory.CreateDirectory(client);
        File.WriteAllBytes(Path.Combine(client, "tiledata.mul"), Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        FacetWriter.WriteAll(TinyProject().Build(7), maps, 7);

        var env = new Dictionary<string, string>
        {
            ["ShardForge__ClientPath"] = client,
            ["ShardForge__DataPath"] = Path.Combine(Root, "data"),
            ["ShardForge__PasswordIterations"] = "1000",
            ["ShardForge__LoginAttemptsPerMinute"] = "10000",
            ["ShardForge__Maps__0__Id"] = "tiny",
            ["ShardForge__Maps__0__Name"] = "Tiny",
            ["ShardForge__Maps__0__Facet"] = "7",
            ["ShardForge__Maps__0__Width"] = "16",
            ["ShardForge__Maps__0__Height"] = "16",
            ["ShardForge__Maps__0__Path"] = maps,
        };
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        Factory = new WebApplicationFactory<Program>();
        Factory.CreateClient().Dispose(); // start the host while the variables are set
    }

    public static ShardProject TinyProject(string name = "Tiny Isle")
    {
        const int w = 16, h = 16;
        var terrain = new byte[w * h];
        for (int i = 0; i < terrain.Length; i++) terrain[i] = (byte)(i % w < 8 ? 0 : 1);
        var density = new byte[w * h]; Array.Fill(density, (byte)255);
        return new ShardProject
        {
            Format = ShardProject.FormatName, Version = 1, Name = name, Width = w, Height = h, Seed = 3,
            Terrains =
            [
                new TerrainDef { Key = "water", LandTiles = [0xA8], FixedZ = -5 },
                new TerrainDef { Key = "forest", LandTiles = [0xC4, 0xC5], Populate = [new PopulateRule { Kind = "tree", Density = 0.5, Items = [[0x0CCD, 0x0CCE]] }] },
            ],
            Layers = new ProjectLayers { Terrain = Convert.ToBase64String(terrain), Height = Convert.ToBase64String(new byte[w * h]), Density = Convert.ToBase64String(density) },
        };
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

public class ServerTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private HttpClient Anon()
    {
        var c = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-ShardForge", "1");
        return c;
    }

    /// <summary>Signs in (running first-run setup for "admin" if needed) and returns a client carrying the cookie.</summary>
    private async Task<(HttpClient Client, string Cookie)> As(string user, string password = "correct horse")
    {
        var c = Anon();
        var status = await c.GetFromJsonAsync<JsonElement>("/api/status");
        var res = status.GetProperty("needsSetup").GetBoolean() && user == "admin"
            ? await c.PostAsJsonAsync("/api/setup", new { username = user, password })
            : await c.PostAsJsonAsync("/api/auth/login", new { username = user, password });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string cookie = res.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        c.DefaultRequestHeaders.Add("Cookie", cookie);
        return (c, cookie);
    }

    private async Task EnsureUser(string name, string role)
    {
        var (admin, _) = await As("admin");
        var users = await admin.GetFromJsonAsync<JsonElement[]>("/api/users");
        if (users!.Any(u => u.GetProperty("username").GetString() == name)) return;
        var r = await admin.PostAsJsonAsync("/api/users", new { username = name, password = "correct horse", role });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task SetupHappensOnceAndProtectsTheApi()
    {
        var anon = Anon();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/maps")).StatusCode);
        var (admin, _) = await As("admin");
        Assert.False((await anon.GetFromJsonAsync<JsonElement>("/api/status")).GetProperty("needsSetup").GetBoolean());
        var again = await anon.PostAsJsonAsync("/api/setup", new { username = "intruder", password = "correct horse" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("Admin", (await admin.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("role").GetString());
    }

    [Fact]
    public async Task ChangesWithoutTheCsrfHeaderAreRefused()
    {
        var (_, cookie) = await As("admin");
        var raw = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        raw.DefaultRequestHeaders.Add("Cookie", cookie);
        var r = await raw.PostAsJsonAsync("/api/users", new { username = "sneaky", password = "correct horse", role = "Admin" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordsAndDisabledAccountsCannotSignIn()
    {
        await EnsureUser("dora", "Builder");
        var anon = Anon();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "dora", password = "wrong password" })).StatusCode);
        var (admin, _) = await As("admin");
        long id = (await admin.GetFromJsonAsync<JsonElement[]>("/api/users"))!.First(u => u.GetProperty("username").GetString() == "dora").GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/users/{id}", new { disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "dora", password = "correct horse" })).StatusCode);
        await admin.PatchAsJsonAsync($"/api/users/{id}", new { disabled = false });
    }

    [Fact]
    public async Task RolesLimitWhatPeopleCanDo()
    {
        await EnsureUser("vic", "Viewer");
        await EnsureUser("bob", "Builder");
        var (viewer, _) = await As("vic");
        var (builder, _) = await As("bob");
        string json = JsonSerializer.Serialize(ServerFixture.TinyProject());

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/projects", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/maps")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await builder.PostAsync("/api/projects", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task ChangingARoleSignsThatPersonOut()
    {
        await EnsureUser("rita", "Builder");
        var (rita, _) = await As("rita");
        Assert.Equal(HttpStatusCode.OK, (await rita.GetAsync("/api/me")).StatusCode);
        var (admin, _) = await As("admin");
        long id = (await admin.GetFromJsonAsync<JsonElement[]>("/api/users"))!.First(u => u.GetProperty("username").GetString() == "rita").GetProperty("id").GetInt64();
        await admin.PatchAsJsonAsync($"/api/users/{id}", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await rita.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task TheLastAdminCannotBeDemoted()
    {
        var (admin, _) = await As("admin");
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/me");
        var r = await admin.PatchAsJsonAsync($"/api/users/{me.GetProperty("id").GetInt64()}", new { role = "Builder" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ProjectsKeepRevisionsAndDetectConflicts()
    {
        await EnsureUser("bob", "Builder");
        var (builder, _) = await As("bob");
        string json = JsonSerializer.Serialize(ServerFixture.TinyProject("Conflict Isle"));
        var created = await (await builder.PostAsync("/api/projects", new StringContent(json, Encoding.UTF8, "application/json"))).Content.ReadFromJsonAsync<JsonElement>();
        long id = created.GetProperty("id").GetInt64();

        var get = await builder.GetAsync($"/api/projects/{id}");
        Assert.Equal("1", get.Headers.GetValues("X-Revision").Single());

        var save = await builder.PutAsync($"/api/projects/{id}?baseRev=1", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(2, (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rev").GetInt32());

        var stale = await builder.PutAsync($"/api/projects/{id}?baseRev=1", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, conflict.GetProperty("latest").GetInt32());
        Assert.Equal("bob", conflict.GetProperty("by").GetString());

        Assert.Equal(2, (await builder.GetFromJsonAsync<JsonElement[]>($"/api/projects/{id}/history"))!.Length);
        var bad = await builder.PostAsync("/api/projects", new StringContent("{\"format\":\"nope\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task BuildingAProjectMakesAWalkableMap()
    {
        await EnsureUser("bob", "Builder");
        var (builder, _) = await As("bob");
        string json = JsonSerializer.Serialize(ServerFixture.TinyProject("Built Isle"));
        long id = (await (await builder.PostAsync("/api/projects", new StringContent(json, Encoding.UTF8, "application/json"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var build = await builder.PostAsJsonAsync($"/api/projects/{id}/build", new { facet = 4 });
        Assert.Equal(HttpStatusCode.OK, build.StatusCode);
        string mapId = (await build.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mapId").GetString()!;

        var map = (await builder.GetFromJsonAsync<JsonElement[]>("/api/maps"))!.Single(m => m.GetProperty("id").GetString() == mapId);
        Assert.Equal(16, map.GetProperty("width").GetInt32());
        Assert.True(map.GetProperty("files").TryGetProperty("map4legacymul.uop", out _));

        // Range requests let Walk read only what it needs.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/maps/{mapId}/files/staidx4.mul");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 11);
        var part = await builder.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(12, (await part.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task SharedStaticsAreStoredValidatedAndExported()
    {
        await EnsureUser("bob", "Builder");
        await EnsureUser("vic", "Viewer");
        var (builder, _) = await As("bob");
        var (viewer, _) = await As("vic");
        byte[] tree = [0xCD, 0x0C, 3, 4, 7, 0, 0];

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { ["1"] = Convert.ToBase64String(tree) } })).StatusCode);
        var bad = await builder.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { ["1"] = Convert.ToBase64String([0xCD, 0x0C, 9, 4, 7, 0, 0]) } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var outside = await builder.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { ["99"] = Convert.ToBase64String(tree) } });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);

        var ok = await builder.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { ["1"] = Convert.ToBase64String(tree) } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var all = await viewer.GetFromJsonAsync<JsonElement>("/api/maps/tiny/blocks");
        Assert.Equal(Convert.ToBase64String(tree), all.GetProperty("blocks").GetProperty("1").GetString());

        using var zip = new ZipArchive(new MemoryStream(await builder.GetByteArrayAsync("/api/maps/tiny/export")));
        byte[] idx = Read(zip, "staidx7.mul"), sta = Read(zip, "statics7.mul");
        var facet = new Facet(new FacetInfo(7, "t", 16, 16));
        FacetLoader.DecodeStatics(idx, sta, facet);
        Assert.Equal(new StaticTile(0x0CCD, 3, 4, 7, 0), Assert.Single(facet.Statics[1]));
        Assert.NotEmpty(facet.Statics[2]); // the untouched forest block keeps its original trees
    }

    [Fact]
    public async Task EveryoneOnAMapSeesPositionsAndEditsLive()
    {
        await EnsureUser("bob", "Builder");
        await EnsureUser("vic", "Viewer");
        var (builder, bobCookie) = await As("bob");
        var (_, vicCookie) = await As("vic");

        using var bob = await Connect(bobCookie);
        using var vic = await Connect(vicCookie);
        var welcome = await Receive(vic, "welcome");
        Assert.Contains(welcome.GetProperty("peers").EnumerateArray(), p => p.GetProperty("name").GetString() == "bob");

        await bob.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"pos\",\"x\":5,\"y\":6,\"z\":3,\"dir\":2}"), WebSocketMessageType.Text, true, default);
        var pos = await Receive(vic, "pos");
        Assert.Equal(5, pos.GetProperty("x").GetInt32());

        byte[] rock = [0x74, 0x17, 1, 1, 0, 0, 0];
        await builder.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { ["0"] = Convert.ToBase64String(rock) } });
        var change = await Receive(vic, "blocks");
        Assert.Equal("bob", change.GetProperty("by").GetString());
        Assert.Equal(Convert.ToBase64String(rock), change.GetProperty("blocks").GetProperty("0").GetString());
    }

    [Fact]
    public async Task ServesTheAppPagesAndClientData()
    {
        var anon = Anon();
        Assert.Contains("ShardForge", await anon.GetStringAsync("/"));
        Assert.Contains("<title>ShardForge Editor</title>", await anon.GetStringAsync("/editor/"));
        Assert.Contains("<title>ShardForge Walk</title>", await anon.GetStringAsync("/walk/"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/data/tiledata.mul")).StatusCode);
        var (viewerish, _) = await As("admin");
        Assert.Equal(1000, (await viewerish.GetByteArrayAsync("/api/data/tiledata.mul")).Length);
        Assert.Equal(HttpStatusCode.NotFound, (await viewerish.GetAsync("/api/data/secret.txt")).StatusCode);
    }

    private async Task<WebSocket> Connect(string cookie)
    {
        var ws = fx.Factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers["Cookie"] = cookie;
        return await ws.ConnectAsync(new Uri(fx.Factory.Server.BaseAddress, "/api/live/tiny"), default);
    }

    private static async Task<JsonElement> Receive(WebSocket ws, string type)
    {
        var buffer = new byte[65536];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            var msg = JsonDocument.Parse(buffer.AsMemory(0, r.Count)).RootElement;
            if (msg.GetProperty("type").GetString() == type) return msg;
        }
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
