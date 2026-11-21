using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        File.WriteAllBytes(Path.Combine(client, "art.mul"), new byte[16]);
        FacetWriter.WriteAll(TinyProject().Build(7), maps, 7);

        var env = new Dictionary<string, string>
        {
            ["ShardForge__ClientPath"] = client,
            ["ShardForge__DataPath"] = Path.Combine(Root, "data"),
            ["ShardForge__ExportPath"] = Path.Combine(Root, "export"),
            ["ShardForge__PasswordIterations"] = "1000",
            ["ShardForge__OpenBrowser"] = "false",
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
    public async Task ServerItemsAreSharedAndPackagedForServUO()
    {
        await EnsureUser("bob", "Builder");
        await EnsureUser("vic", "Viewer");
        var (bob, _) = await As("bob");
        var (viewer, _) = await As("vic");
        var maps = await bob.GetFromJsonAsync<JsonElement[]>("/api/maps");
        string id = maps![0].GetProperty("id").GetString()!;
        var put = new object[]
        {
            new { uid = "d1", kind = "door", x = 10, y = 10, z = 0, type = "DarkWoodDoor", facing = "WestCW" },
            new { uid = "s1", kind = "sign", x = 11, y = 10, z = 0, id = 0x0BD2, name = "The Rusty Anchor" },
            new { uid = "p1", kind = "spawn", x = 12, y = 12, z = 0, creatures = "Orc, OrcishMage", count = 4, range = 6, min = 5, max = 10 },
            new { uid = "p2", kind = "spawn", x = 3, y = 3, z = 0, creatures = "Chicken:4, Cow:2", count = 0, range = 6, min = 5, max = 10 },
            new { uid = "r1", kind = "region", x = 0, y = 0, z = 0, w = 16, h = 12, name = "Anchorage", guarded = true },
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/maps/{id}/items", new { put })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync($"/api/maps/{id}/items", new { put })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutAsJsonAsync($"/api/maps/{id}/items", new { put = new[] { new { uid = "x", kind = "door", x = 1, y = 1, z = 0, type = "Evil;Door", facing = "WestCW" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutAsJsonAsync($"/api/maps/{id}/items", new { put = new[] { new { uid = "x", kind = "sign", x = 999999, y = 1, z = 0, id = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync($"/api/maps/{id}/items", new { remove = new[] { "s1" } })).StatusCode);

        var items = await viewer.GetFromJsonAsync<JsonElement[]>($"/api/maps/{id}/items");
        Assert.Equal(4, items!.Length);
        var farm = items.Single(i => i.GetProperty("uid").GetString() == "p2");
        Assert.Equal("Chicken:4,Cow:2", farm.GetProperty("creatures").GetString());
        Assert.Equal(6, farm.GetProperty("count").GetInt32());
        Assert.Equal("Orc:2,OrcishMage:2", items.Single(i => i.GetProperty("uid").GetString() == "p1").GetProperty("creatures").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/maps/{id}/servuo")).StatusCode); // exports are for admins
        var (admin, _) = await As("admin");
        var zip = new ZipArchive(new MemoryStream(await admin.GetByteArrayAsync($"/api/maps/{id}/servuo")));
        Assert.Contains(zip.Entries, e => e.FullName == "ServUO/Scripts/Custom/ShardForge/ShardForgeImport.cs");
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("client/statics"));
        string sf = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".sfitems")).Open()).ReadToEnd();
        Assert.Contains("map\tFelucca", sf);
        Assert.Contains("door\t10\t10\t0\tDarkWoodDoor\tWestCW", sf);
        Assert.Contains("spawn\t12\t12\t0\t4\t6\t5\t10\tOrc:2,OrcishMage:2", sf);
        Assert.Contains("region\t0\t0\t16\t12\t1\tAnchorage", sf);
        Assert.DoesNotContain("Rusty Anchor", sf);
    }

    [Fact]
    public async Task EachLevelDoesOnlyItsOwnPartAndEverythingIsLogged()
    {
        await EnsureUser("deco", "Decorator");
        await EnsureUser("wendy", "Worldbuilder");
        var (deco, _) = await As("deco");
        var (wendy, _) = await As("wendy");
        var (admin, _) = await As("admin");
        string B(params byte[][] statics) => Convert.ToBase64String(statics.SelectMany(x => x).ToArray());
        byte[] lamp = [0x0A, 0x0B, 1, 1, 0, 0, 0], barrel = [0xAE, 0x0F, 2, 2, 0, 0, 0], crate = [0x3C, 0x0E, 5, 5, 0, 0, 0];
        Task<HttpResponseMessage> Put(HttpClient c, int block, string data) => c.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { [block.ToString()] = data } });

        // A worldbuilder puts a crate down; a decorator adds a lamp and a barrel next to it.
        Assert.Equal(HttpStatusCode.OK, (await Put(wendy, 3, B(crate))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(deco, 3, B(crate, lamp, barrel))).StatusCode);
        // The decorator may take their own barrel away, but not the worldbuilder's crate.
        Assert.Equal(HttpStatusCode.OK, (await Put(deco, 3, B(crate, lamp))).StatusCode);
        var denied = await Put(deco, 3, B(lamp));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("placed themselves", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // Nor change the land, nor place regions; a worldbuilder can do all of it.
        var land = Convert.ToBase64String(new byte[192]);
        Assert.Equal(HttpStatusCode.Forbidden, (await deco.PutAsJsonAsync("/api/maps/tiny/blocks", new { land = new Dictionary<string, string> { ["0"] = land } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deco.PutAsJsonAsync("/api/maps/tiny/items", new { put = new[] { new { uid = "rg", kind = "region", x = 0, y = 0, z = 0, w = 8, h = 8, name = "Mine" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(wendy, 3, B(lamp))).StatusCode);
        // Server items: a decorator's own sign is theirs to remove, someone else's isn't.
        Assert.Equal(HttpStatusCode.OK, (await wendy.PutAsJsonAsync("/api/maps/tiny/items", new { put = new[] { new { uid = "w-sign", kind = "sign", x = 2, y = 2, z = 0, id = 0x0BD2, name = "Wendy's" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deco.PutAsJsonAsync("/api/maps/tiny/items", new { put = new[] { new { uid = "d-sign", kind = "sign", x = 3, y = 3, z = 0, id = 0x0BD2, name = "Deco's" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deco.PutAsJsonAsync("/api/maps/tiny/items", new { remove = new[] { "w-sign" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deco.PutAsJsonAsync("/api/maps/tiny/items", new { remove = new[] { "d-sign" } })).StatusCode);
        // Exports and projects are not for decorators; exports not for worldbuilders either.
        Assert.Equal(HttpStatusCode.Forbidden, (await deco.GetAsync("/api/maps/tiny/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wendy.GetAsync("/api/maps/tiny/servuo")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deco.PostAsync("/api/projects", new StringContent(JsonSerializer.Serialize(ServerFixture.TinyProject()), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wendy.GetAsync("/api/audit")).StatusCode);

        // The log says who did what, and where.
        var users = await admin.GetFromJsonAsync<JsonElement[]>("/api/users");
        long decoId = users!.Single(u => u.GetProperty("username").GetString() == "deco").GetProperty("id").GetInt64();
        Assert.Equal("Decorator", users!.Single(u => u.GetProperty("username").GetString() == "deco").GetProperty("role").GetString());
        var mine = await admin.GetFromJsonAsync<JsonElement[]>($"/api/audit?user={decoId}");
        Assert.Contains(mine!, e => e.GetProperty("action").GetString() == "statics.add" && e.GetProperty("summary").GetString()!.Contains("added 2 statics") && e.GetProperty("x").GetInt32() == 9 && e.GetProperty("y").GetInt32() == 9);
        Assert.Contains(mine!, e => e.GetProperty("action").GetString() == "statics.remove" && e.GetProperty("summary").GetString()!.Contains("removed 1 static"));
        Assert.Contains(mine!, e => e.GetProperty("action").GetString() == "denied.statics");
        Assert.Contains(mine!, e => e.GetProperty("action").GetString() == "items.remove" && e.GetProperty("summary").GetString()!.Contains("Deco's"));
        Assert.Contains(mine!, e => e.GetProperty("action").GetString() == "auth.login");
        Assert.All(mine!, e => Assert.Equal("deco", e.GetProperty("username").GetString()));
        var exports = await admin.GetFromJsonAsync<JsonElement[]>("/api/audit?action=export");
        Assert.DoesNotContain(exports!, e => e.GetProperty("username").GetString() is "deco" or "wendy");
        var people = await admin.GetFromJsonAsync<JsonElement[]>("/api/audit/people");
        Assert.Contains(people!, p => p.GetProperty("username").GetString() == "deco" && p.GetProperty("statics").GetInt64() >= 2);
    }

    [Fact]
    public async Task BackupsBringTheWorldBackButNotThePeopleOrTheLog()
    {
        await EnsureUser("rogue", "Worldbuilder");
        var (admin, _) = await As("admin");
        var (rogue, _) = await As("rogue");
        byte[] well = [0x2D, 0x0E, 2, 2, 0, 0, 0];
        Assert.Equal(HttpStatusCode.OK, (await rogue.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { ["2"] = Convert.ToBase64String(well) } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rogue.PostAsync("/api/backups", null)).StatusCode);
        var made = await admin.PostAsync("/api/backups", null);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        string name = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;

        // The rogue wipes the block and changes something else; later someone new joins.
        Assert.Equal(HttpStatusCode.OK, (await rogue.PutAsJsonAsync("/api/maps/tiny/blocks", new { blocks = new Dictionary<string, string> { ["2"] = "" } })).StatusCode);
        await EnsureUser("newcomer", "Viewer");

        var restore = await admin.PostAsJsonAsync($"/api/backups/{name}/restore", new { scope = "map:tiny" });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var blocks = await admin.GetFromJsonAsync<JsonElement>("/api/maps/tiny/blocks");
        Assert.Equal(Convert.ToBase64String(well), blocks.GetProperty("blocks").GetProperty("2").GetString());
        // People and the log are as they are now: the newcomer is still there, and so is the rogue's wipe in the log.
        var users = await admin.GetFromJsonAsync<JsonElement[]>("/api/users");
        Assert.Contains(users!, u => u.GetProperty("username").GetString() == "newcomer");
        var log = await admin.GetFromJsonAsync<JsonElement[]>("/api/audit?q=rogue&limit=500");
        Assert.Contains(log!, e => e.GetProperty("action").GetString() == "statics.remove" && e.GetProperty("username").GetString() == "rogue");
        var restored = await admin.GetFromJsonAsync<JsonElement[]>("/api/audit?action=backup.restore");
        Assert.Contains("the map tiny", restored![0].GetProperty("summary").GetString());
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/backups");
        Assert.Contains(list.GetProperty("list").EnumerateArray(), b => b.GetProperty("kind").GetString() == "before-restore");
    }

    [Fact]
    public async Task OnlyTheManagerWithTheTokenCanStopTheServer()
    {
        var c = Anon();
        var none = await c.PostAsync("/api/control/stop", null);
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/control/stop");
        req.Headers.Add("X-ShardForge-Control", "not-the-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);
        // The control file the Manager reads: this process, with a token, in the data folder.
        string file = Path.Combine(fx.Root, "data", "control", "server.json");
        Assert.True(File.Exists(file));
        var info = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        Assert.Equal(Environment.ProcessId, info.GetProperty("pid").GetInt32());
        Assert.Equal(48, info.GetProperty("token").GetString()!.Length);
        // And the server keeps its own log in data/logs.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(fx.Root, "data", "logs"), "server-*.log"));
    }

    [Fact]
    public async Task TheHouseLibraryIsSharedByEveryone()
    {
        await EnsureUser("vic", "Viewer");
        await EnsureUser("dora", "Decorator");
        await EnsureUser("dan", "Decorator");
        await EnsureUser("wes", "Worldbuilder");
        var (viewer, _) = await As("vic");
        var (bob, _) = await As("dora");
        var (ann, _) = await As("dan");
        var (wes, _) = await As("wes");
        var plan = new { plan = new { walls = "Dark Wood Standard Walls", storeys = new[] { new[] { new[] { 0, 0 }, new[] { 1, 0 } } } } };

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/prefabs", new { name = "Hut", data = plan })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync("/api/prefabs", new { name = "", data = plan })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync("/api/prefabs", new { name = "Junk", data = new { parts = new[] { new[] { 1, 2 } } } })).StatusCode);

        var made = await bob.PostAsJsonAsync("/api/prefabs", new { name = "Bob's hut", data = plan });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        long id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var piece = await ann.PostAsJsonAsync("/api/prefabs", new { name = "Ann's well", data = new { parts = new[] { new[] { 0x0E2D, 0, 0, 0 }, new[] { 0x0E2E, 1, 0, 0, 33 } } } });
        Assert.Equal(HttpStatusCode.OK, piece.StatusCode);

        // Everyone sees both, viewers included; the plan comes back as saved.
        var all = await viewer.GetFromJsonAsync<JsonElement[]>("/api/prefabs");
        var hut = all!.Single(p => p.GetProperty("id").GetInt64() == id);
        Assert.Equal("dora", hut.GetProperty("createdBy").GetString());
        Assert.Equal(2, hut.GetProperty("data").GetProperty("plan").GetProperty("storeys")[0].GetArrayLength());
        Assert.Contains(all!, p => p.GetProperty("name").GetString() == "Ann's well");

        // Decorators change and delete only their own; worldbuilders anyone's.
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.PutAsJsonAsync($"/api/prefabs/{id}", new { name = "Dan's hut now", data = plan })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.DeleteAsync($"/api/prefabs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await wes.PutAsJsonAsync($"/api/prefabs/{id}", new { name = "Bob's bigger hut", data = plan })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync("/api/prefabs/999999", new { name = "x", data = plan })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/prefabs/{id}")).StatusCode);
        Assert.DoesNotContain(await viewer.GetFromJsonAsync<JsonElement[]>("/api/prefabs") ?? [], p => p.GetProperty("id").GetInt64() == id);
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

        Assert.Equal(HttpStatusCode.Forbidden, (await builder.GetAsync("/api/maps/tiny/export")).StatusCode);
        var (admin, _) = await As("admin");
        using var zip = new ZipArchive(new MemoryStream(await admin.GetByteArrayAsync("/api/maps/tiny/export")));
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
        await Receive(bob, "welcome"); // bob is in the room before vic arrives (the upgrade returns before that)
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

    [Fact]
    public async Task AdminsCanLetOtherComputersOnTheNetworkIn()
    {
        var (admin, _) = await As("admin");
        var net = (await admin.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("network");
        Assert.False(net.GetProperty("now").GetBoolean());
        Assert.True(net.GetProperty("port").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Array, net.GetProperty("addresses").ValueKind);

        // Saved for the next start (the running server keeps listening where it is), and logged.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", new { network = true })).StatusCode);
        net = (await admin.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("network");
        Assert.True(net.GetProperty("saved").GetBoolean());
        Assert.False(net.GetProperty("now").GetBoolean());
        Assert.Contains("\"network\"", File.ReadAllText(Path.Combine(fx.Root, "data", "settings.json")));
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/audit?action=settings.network");
        Assert.Contains("other computers", audit.ToString());

        // Only admins.
        await EnsureUser("wes2", "Worldbuilder");
        var (wb, _) = await As("wes2");
        Assert.Equal(HttpStatusCode.Forbidden, (await wb.PutAsJsonAsync("/api/settings", new { network = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", new { network = false })).StatusCode);
        Assert.Contains("\"local\"", File.ReadAllText(Path.Combine(fx.Root, "data", "settings.json")));
    }

    [Fact]
    public async Task AdminsChooseTheClientFolderAndOnlyARealClientIsAccepted()
    {
        var (admin, _) = await As("admin");
        var st = await admin.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(st.GetProperty("client").GetProperty("ok").GetBoolean());
        string good = st.GetProperty("client").GetProperty("path").GetString()!;

        // Not a client folder: refused, and the setting is unchanged.
        string empty = Path.Combine(fx.Root, "not-a-client");
        Directory.CreateDirectory(empty);
        var bad = await admin.PutAsJsonAsync("/api/settings", new { clientPath = empty });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("isn't a UO client folder", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings", new { clientPath = "relative\\path" })).StatusCode);

        // A copy of the client: accepted and saved to data/settings.json.
        string copy = Path.Combine(fx.Root, "client2");
        Directory.CreateDirectory(copy);
        foreach (var f in Directory.GetFiles(good)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)), true);
        var ok = await admin.PutAsJsonAsync("/api/settings", new { clientPath = copy });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("client2", File.ReadAllText(Path.Combine(fx.Root, "data", "settings.json")));
        Assert.True((await Anon().GetFromJsonAsync<JsonElement>("/api/status")).GetProperty("clientFound").GetBoolean());
        await admin.PutAsJsonAsync("/api/settings", new { clientPath = good });

        // Viewers and strangers can't read or change server settings; detection is only for first-run setup.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anon().GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Anon().GetAsync("/api/setup/detect")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Anon().PostAsJsonAsync("/api/setup/check", new { path = good })).StatusCode);
    }

    [Fact]
    public async Task CaveMouthsLeadIntoADungeonMapAndBackAndMoongatesExport()
    {
        await EnsureUser("bob", "Builder");
        var (builder, _) = await As("bob");
        async Task<string> Build(ShardProject p, int facet)
        {
            long pid = (await (await builder.PostAsync("/api/projects", new StringContent(JsonSerializer.Serialize(p), Encoding.UTF8, "application/json"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            var r = await builder.PostAsJsonAsync($"/api/projects/{pid}/build", new { facet });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mapId").GetString()!;
        }
        // The dungeon map: one named area, its way in at 12, 4.
        var dungeon = ServerFixture.TinyProject("Deeps");
        dungeon.Places = [new ProjectPlace { Name = "Black Hollow", Kind = "dungeon", X = 12, Y = 4 }];
        string dmap = await Build(dungeon, 5);

        // The overworld: a mouth into it, a moongate, and the mouth's own land tile.
        var world = ServerFixture.TinyProject("Overland");
        world.Objects = new ProjectObjects
        {
            Land = [[0x1DB, 10, 10, 20]],
            ServerItems =
            [
                JsonNode.Parse($$"""{"kind":"tele","x":10,"y":10,"z":0,"link":{"map":"{{dmap}}","place":"Black Hollow"},"back":[10,12,0]}""")!.AsObject(),
                JsonNode.Parse("""{"kind":"tele","x":11,"y":11,"z":0,"dx":3,"dy":3,"dz":0,"look":"moongate","name":"Moongate to Ashford"}""")!.AsObject(),
                JsonNode.Parse("""{"kind":"tele","x":12,"y":12,"z":0,"link":{"map":"nowhere","place":"Nothing"},"back":[1,1,0]}""")!.AsObject(),
            ],
        };
        long id = (await (await builder.PostAsync("/api/projects", new StringContent(JsonSerializer.Serialize(world), Encoding.UTF8, "application/json"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var build = await (await builder.PostAsJsonAsync($"/api/projects/{id}/build", new { facet = 4 })).Content.ReadFromJsonAsync<JsonElement>();
        string wmap = build.GetProperty("mapId").GetString()!;
        Assert.Equal(1, build.GetProperty("waysBack").GetInt32());
        Assert.Equal("Nothing", build.GetProperty("unlinked")[0].GetString());

        var mouth = (await builder.GetFromJsonAsync<JsonElement[]>($"/api/maps/{wmap}/items"))!.Single(i => i.GetProperty("x").GetInt32() == 10);
        Assert.Equal(dmap, mouth.GetProperty("dmapId").GetString());
        Assert.Equal("TerMur", mouth.GetProperty("dmap").GetString());
        Assert.InRange(Math.Max(Math.Abs(mouth.GetProperty("dx").GetInt32() - 12), Math.Abs(mouth.GetProperty("dy").GetInt32() - 4)), 0, 4);
        var back = (await builder.GetFromJsonAsync<JsonElement[]>($"/api/maps/{dmap}/items"))!.Single(i => i.GetProperty("uid").GetString()!.StartsWith($"link-{wmap}-"));
        Assert.Equal(12, back.GetProperty("x").GetInt32());
        Assert.Equal(10, back.GetProperty("dx").GetInt32());
        Assert.Equal(12, back.GetProperty("dy").GetInt32());
        Assert.Equal(wmap, back.GetProperty("dmapId").GetString());

        // The mouth's land tile is in the built map.
        string dir = Path.Combine(fx.Root, "data", "maps", wmap);
        Assert.Equal(0x1DB, FacetLoader.Load(dir, 4, new FacetInfo(4, "t", 16, 16)).GetLand(10, 10).TileId);

        // Rebuilding without the mouth takes the way back away again.
        world.Objects.ServerItems = [world.Objects.ServerItems[1]];
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{id}?baseRev=1") { Content = new StringContent(JsonSerializer.Serialize(world), Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.OK, (await builder.SendAsync(put)).StatusCode);
        await builder.PostAsJsonAsync($"/api/projects/{id}/build", new { facet = 4 });
        Assert.DoesNotContain((await builder.GetFromJsonAsync<JsonElement[]>($"/api/maps/{dmap}/items"))!, i => i.GetProperty("uid").GetString()!.StartsWith($"link-{wmap}-"));

        // The export folder: client\ and ServUO\ ready to copy, one map per facet.
        var (adm, _) = await As("admin");
        var ex = await (await adm.PostAsync($"/api/maps/{wmap}/export-folder", null)).Content.ReadFromJsonAsync<JsonElement>();
        string exportDir = ex.GetProperty("path").GetString()!;
        Assert.True(File.Exists(Path.Combine(exportDir, "client", "statics4.mul")));
        Assert.True(File.Exists(Path.Combine(exportDir, "client", "map4LegacyMUL.uop"))); // a built map's own land, unedited
        Assert.True(File.Exists(Path.Combine(exportDir, "ServUO", "Scripts", "Custom", "ShardForge", "ShardForgeImport.cs")));
        Assert.True(File.Exists(Path.Combine(exportDir, "ServUO", "Data", "ShardForge", wmap + ".sfitems")));
        await adm.PostAsync($"/api/maps/{dmap}/export-folder", null);
        // Another map for the same facet takes its place.
        string other = await Build(ServerFixture.TinyProject("Overland Two"), 4);
        var ex2 = await (await adm.PostAsync($"/api/maps/{other}/export-folder", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(File.Exists(Path.Combine(exportDir, "ServUO", "Data", "ShardForge", wmap + ".sfitems")));
        Assert.True(File.Exists(Path.Combine(exportDir, "ServUO", "Data", "ShardForge", dmap + ".sfitems")));
        Assert.Equal(2, ex2.GetProperty("maps").GetArrayLength());
        Assert.Contains("Overland Two", File.ReadAllText(Path.Combine(exportDir, "README.txt")));
        Assert.Equal(HttpStatusCode.Forbidden, (await builder.PostAsync($"/api/maps/{other}/export-folder", null)).StatusCode);

        // The moongate goes to ServUO as a visible, named teleporter.
        var (admin, _) = await As("admin");
        var zip = new ZipArchive(new MemoryStream(await admin.GetByteArrayAsync($"/api/maps/{wmap}/servuo")));
        string sf = new StreamReader(zip.Entries.Single(e => e.Name.EndsWith(".sfitems")).Open()).ReadToEnd();
        Assert.True(sf.Contains("tele\t11\t11\t0\t3\t3\t0\t"), sf); Assert.Contains("\tmoongate\tMoongate to Ashford\n", sf);
        var badLook = await builder.PutAsJsonAsync($"/api/maps/{wmap}/items", new { put = new[] { new { uid = "x1", kind = "tele", x = 1, y = 1, z = 0, dx = 2, dy = 2, dz = 0, look = "dragon" } } });
        Assert.Equal(HttpStatusCode.BadRequest, badLook.StatusCode);
    }

    [Fact]
    public async Task WalkEditsSurviveRebuildingTheProject()
    {
        await EnsureUser("bob", "Builder");
        var (builder, _) = await As("bob");
        var project = ServerFixture.TinyProject("Rebuilt Isle");
        string json = JsonSerializer.Serialize(project);
        long id = (await (await builder.PostAsync("/api/projects", new StringContent(json, Encoding.UTF8, "application/json"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        string mapId = (await (await builder.PostAsJsonAsync($"/api/projects/{id}/build", new { facet = 4 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mapId").GetString()!;
        string dir = Path.Combine(fx.Root, "data", "maps", mapId);
        var first = FacetLoader.Load(dir, 4, new FacetInfo(4, "t", 16, 16));

        // In Walk: block 2 (forest) loses its first tree piece and gets a lamp; one water cell in block 0 becomes land.
        var before = first.Statics[2];
        Assert.NotEmpty(before);
        var edited = before.Skip(1).Append(new StaticTile(0x0B24, 1, 1, 7, 0)).ToArray();
        var land = new byte[192];
        for (int c = 0; c < 64; c++) { var cell = first.GetLand(c & 7, c >> 3); BitConverter.TryWriteBytes(land.AsSpan(c * 3), cell.TileId); land[c * 3 + 2] = (byte)cell.Z; }
        BitConverter.TryWriteBytes(land.AsSpan(0), (ushort)0x0003); land[2] = 5;
        var save = await builder.PutAsJsonAsync($"/api/maps/{mapId}/blocks", new
        {
            blocks = new Dictionary<string, string> { ["2"] = Convert.ToBase64String(Maps.BlockStore.EncodeStatics(edited)) },
            land = new Dictionary<string, string> { ["0"] = Convert.ToBase64String(land) },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // The project changes (new seed: trees move) and is rebuilt.
        project.Seed = 77;
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{id}?baseRev=1") { Content = new StringContent(JsonSerializer.Serialize(project), Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.OK, (await builder.SendAsync(put)).StatusCode);
        var rebuild = await (await builder.PostAsJsonAsync($"/api/projects/{id}/build", new { facet = 4 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, rebuild.GetProperty("keptEdits").GetProperty("statics").GetInt32());
        Assert.Equal(1, rebuild.GetProperty("keptEdits").GetProperty("land").GetInt32());

        var second = FacetLoader.Load(dir, 4, new FacetInfo(4, "t", 16, 16));
        var all = await builder.GetFromJsonAsync<JsonElement>($"/api/maps/{mapId}/blocks");
        var statics = Maps.BlockStore.DecodeStatics(Convert.FromBase64String(all.GetProperty("blocks").GetProperty("2").GetString()!));
        // The lamp is still there, on top of the new trees (which are kept apart from the one removed piece).
        Assert.Contains(new StaticTile(0x0B24, 1, 1, 7, 0), statics);
        Assert.True(statics.Length >= second.Statics[2].Length - 1);
        var landBack = Convert.FromBase64String(all.GetProperty("land").GetProperty("0").GetString()!);
        Assert.Equal(0x0003, BitConverter.ToUInt16(landBack, 0));
        Assert.Equal(5, (sbyte)landBack[2]);
        // Untouched cells follow the new build.
        Assert.Equal(second.GetLand(5, 5).TileId, BitConverter.ToUInt16(landBack, (5 * 8 + 5) * 3));

        // The export carries the land edit too: a full map, not just statics.
        var (exporter, _) = await As("admin");
        var zip = await exporter.GetAsync($"/api/maps/{mapId}/export");
        using var archive = new ZipArchive(await zip.Content.ReadAsStreamAsync());
        Assert.Contains(archive.Entries, e => e.Name == "map4LegacyMUL.uop");
        Assert.Contains(archive.Entries, e => e.Name == "statics4.mul");
    }
}
