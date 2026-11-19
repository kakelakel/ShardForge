using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using ShardForge.Core.Map;
using ShardForge.Core.Project;
using ShardForge.Core.Terrain;
using ShardForge.Server;
using ShardForge.Server.Auth;
using ShardForge.Server.Data;
using ShardForge.Server.Live;
using ShardForge.Server.Maps;
using ShardForge.Server.Projects;

// Started from a shortcut or another folder: settings and data live next to the program.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")) ? null : AppContext.BaseDirectory,
});
var options = builder.Configuration.GetSection("ShardForge").Get<ServerOptions>() ?? new ServerOptions();
string dataPath = options.ResolvedDataPath(builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataPath);
var settings = new ServerSettings(options, dataPath);
settings.Load();
// No client folder configured: use the first one found on this PC.
if (!ServerSettings.Check(options.ClientPath).Ok && ServerSettings.Detect(null).FirstOrDefault() is { } found) options.ClientPath = found.Path;

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new Db(dataPath));
builder.Services.AddSingleton(sp => new UserStore(sp.GetRequiredService<Db>(), options.PasswordIterations));
builder.Services.AddSingleton<ProjectStore>();
builder.Services.AddSingleton<PrefabStore>();
builder.Services.AddSingleton<BlockStore>();
builder.Services.AddSingleton(new MapRegistry(options, dataPath));
builder.Services.AddSingleton<LiveHub>();

var keys = builder.Services.AddDataProtection().SetApplicationName("ShardForge").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
if (OperatingSystem.IsWindows()) keys.ProtectKeysWithDpapi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "shardforge";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    // Every request re-checks the account, so disabling a user or changing their role takes effect at once.
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var u = long.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out long id) ? users.Get(id) : null;
        if (u is null || u.Disabled || ctx.Principal!.FindFirstValue("stamp") != u.SecurityStamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        ctx.HttpContext.Items["user"] = u;
    };
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Viewer", p => p.RequireAuthenticatedUser());
    o.AddPolicy("Builder", p => p.RequireAuthenticatedUser().RequireAssertion(c => Level(c.User) >= Role.Builder));
    o.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireAssertion(c => Level(c.User) >= Role.Admin));
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, options.LoginAttemptsPerMinute), Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// --- Security headers and a simple CSRF guard: changes need a custom header browsers won't send cross-site.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "same-origin";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src https://fonts.gstatic.com; img-src 'self' data: blob:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    var m = ctx.Request.Method;
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(m) && !HttpMethods.IsHead(m) && !HttpMethods.IsOptions(m)
        && ctx.Request.Headers["X-ShardForge"] != "1")
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-ShardForge header." });
        return;
    }
    await next();
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ============================================================================ web pages (embedded)
var web = Assembly.GetExecutingAssembly().GetManifestResourceNames()
    .Where(n => n.Replace('\\', '/').StartsWith("web/"))
    .ToDictionary(n => n.Replace('\\', '/')["web/".Length..], n => n, StringComparer.OrdinalIgnoreCase);
IResult Page(string name)
{
    if (!web.TryGetValue(name, out var res)) return Results.NotFound();
    var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(res)!;
    string type = Path.GetExtension(name) switch { ".css" => "text/css; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".svg" => "image/svg+xml", _ => "text/html; charset=utf-8" };
    return Results.Stream(stream, type);
}
app.MapGet("/", () => Page("index.html"));
// "/editor" and "/editor/" are one route; the page needs the trailing slash for its relative URLs.
app.MapGet("/editor", (HttpContext ctx) => ctx.Request.Path.Value!.EndsWith('/') ? Page("editor/index.html") : Results.Redirect("/editor/" + ctx.Request.QueryString));
app.MapGet("/walk", (HttpContext ctx) => ctx.Request.Path.Value!.EndsWith('/') ? Page("walk/index.html") : Results.Redirect("/walk/" + ctx.Request.QueryString));
app.MapGet("/{file}", (string file) => file.Contains('/') ? Results.NotFound() : Page(file));

// ============================================================================ status, setup, login
var api = app.MapGroup("/api");
string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

api.MapGet("/status", (UserStore users, MapRegistry maps) => Results.Ok(new
{
    name = "ShardForge Server", version, needsSetup = users.Count() == 0,
    clientFound = ServerSettings.Check(options.ClientPath).Ok,
}));

// First-run setup can suggest client folders found on this PC (only from this PC, only before setup).
api.MapGet("/setup/detect", (HttpContext ctx, UserStore users) =>
{
    if (users.Count() > 0) return Err(403, "Setup is done; admins change settings on the Admin page.");
    if (!options.AllowRemoteSetup && !IsLocal(ctx)) return Err(403, "Only from the PC the server runs on.");
    return Results.Ok(new { current = ServerSettings.Check(options.ClientPath), found = ServerSettings.Detect(options.ClientPath) });
});
api.MapPost("/setup/check", (HttpContext ctx, UserStore users, PathBody body) =>
{
    if (users.Count() > 0 && !(ctx.User.Identity?.IsAuthenticated == true && Level(ctx.User) >= Role.Admin)) return Err(403, "Admins only.");
    if (users.Count() == 0 && !options.AllowRemoteSetup && !IsLocal(ctx)) return Err(403, "Only from the PC the server runs on.");
    return Results.Ok(ServerSettings.Check(body.Path));
}).RequireRateLimiting("login");

// ============================================================================ server settings (admin)
api.MapGet("/settings", (MapRegistry maps) => Results.Ok(new
{
    version, dataPath, urls = app.Urls.ToArray(), client = ServerSettings.Check(options.ClientPath),
    found = ServerSettings.Detect(options.ClientPath), maps = maps.All().Count,
})).RequireAuthorization("Admin");
api.MapPut("/settings", (ServerSettings store, SettingsBody body) =>
{
    if (body.ClientPath is not null)
    {
        var check = ServerSettings.Check(body.ClientPath);
        if (!check.Ok) return Err(400, check.Note);
        store.SetClientPath(check.Path);
    }
    return Results.Ok(new { client = ServerSettings.Check(options.ClientPath) });
}).RequireAuthorization("Admin");

api.MapPost("/setup", async (HttpContext ctx, UserStore users, Credentials body) =>
{
    if (!options.AllowRemoteSetup && !IsLocal(ctx)) return Err(403, "Create the first admin from the PC the server runs on (open http://localhost:2595/ there).");
    ServerSettings.ClientCheck? client = null;
    if (!string.IsNullOrWhiteSpace(body.ClientPath))
    {
        client = ServerSettings.Check(body.ClientPath);
        if (!client.Ok) return Err(400, client.Note);
    }
    try
    {
        var u = users.Create(body.Username ?? "", body.Password ?? "", Role.Admin, onlyIfNoUsers: true);
        if (client is not null) ctx.RequestServices.GetRequiredService<ServerSettings>().SetClientPath(client.Path);
        await SignIn(ctx, u);
        return Results.Ok(Me(u));
    }
    catch (UserException e) { return Err(400, e.Message); }
}).RequireRateLimiting("login");

api.MapPost("/auth/login", async (HttpContext ctx, UserStore users, Credentials body) =>
{
    var u = users.Verify(body.Username ?? "", body.Password ?? "");
    if (u is null) return Err(401, "Wrong name or password.");
    await SignIn(ctx, u);
    return Results.Ok(Me(u));
}).RequireRateLimiting("login");

api.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
});

api.MapGet("/me", (HttpContext ctx) => Results.Ok(Me(Current(ctx)))).RequireAuthorization("Viewer");

api.MapPost("/me/password", async (HttpContext ctx, UserStore users, PasswordChange body) =>
{
    var me = Current(ctx);
    if (users.Verify(me.Username, body.Current ?? "") is null) return Err(400, "Your current password is wrong.");
    try { var u = users.Update(me.Id, password: body.Password ?? ""); await SignIn(ctx, u); return Results.Ok(Me(u)); }
    catch (UserException e) { return Err(400, e.Message); }
}).RequireAuthorization("Viewer");

// ============================================================================ users (admin)
api.MapGet("/users", (UserStore users) => Results.Ok(users.All().Where(u => !u.Username.Contains("(deleted ")).Select(u => new
{
    id = u.Id, username = u.Username, role = u.Role.ToString(), disabled = u.Disabled, created = u.Created, lastLogin = u.LastLogin,
}))).RequireAuthorization("Admin");

api.MapPost("/users", (UserStore users, NewUser body) =>
{
    if (!Enum.TryParse<Role>(body.Role, true, out var role) || !Enum.IsDefined(role)) return Err(400, "Role must be Viewer, Builder or Admin.");
    try { var u = users.Create(body.Username ?? "", body.Password ?? "", role); return Results.Ok(Me(u)); }
    catch (UserException e) { return Err(400, e.Message); }
}).RequireAuthorization("Admin");

api.MapPatch("/users/{id:long}", (long id, UserStore users, UserPatch body) =>
{
    Role? role = null;
    if (body.Role is not null)
    {
        if (!Enum.TryParse<Role>(body.Role, true, out var r) || !Enum.IsDefined(r)) return Err(400, "Role must be Viewer, Builder or Admin.");
        role = r;
    }
    try { var u = users.Update(id, role, body.Password, body.Disabled); return Results.Ok(Me(u)); }
    catch (UserException e) { return Err(400, e.Message); }
}).RequireAuthorization("Admin");

api.MapDelete("/users/{id:long}", (long id, HttpContext ctx, UserStore users) =>
{
    if (Current(ctx).Id == id) return Err(400, "You can't delete your own account.");
    try { users.Delete(id); return Results.Ok(new { ok = true }); }
    catch (UserException e) { return Err(400, e.Message); }
}).RequireAuthorization("Admin");

// ============================================================================ client data files
string[] dataFiles = ["artlegacymul.uop", "art.mul", "artidx.mul", "tiledata.mul", "animdata.mul", "hues.mul", "radarcol.mul", "multi.idx", "multi.mul", "walls.txt", "roof.txt", "floors.txt", "stairs.txt", "doors.txt", "misc.txt"];
api.MapGet("/data", () => Results.Ok(new
{
    files = dataFiles.Select(n => (n, path: MapRegistry.FindFile(options.ClientPath, n)))
        .Where(t => t.path is not null).ToDictionary(t => t.n, t => new FileInfo(t.path!).Length),
})).RequireAuthorization("Viewer");

api.MapGet("/data/{name}", (string name) =>
{
    name = name.ToLowerInvariant();
    if (!dataFiles.Contains(name)) return Results.NotFound();
    var path = MapRegistry.FindFile(options.ClientPath, name);
    return path is null ? Results.NotFound() : Results.File(path, name.EndsWith(".json") ? "application/json" : "application/octet-stream", enableRangeProcessing: true);
}).RequireAuthorization("Viewer");

// ============================================================================ maps and shared statics
api.MapGet("/maps", (MapRegistry maps, BlockStore blocks, LiveHub hub) => Results.Ok(maps.All().Select(m => new
{
    id = m.Id, name = m.Name, facet = m.Facet, width = m.Width, height = m.Height, source = m.Source, files = m.Files,
    rev = blocks.GetAll(m.Id).Rev, online = hub.Count(m.Id),
}))).RequireAuthorization("Viewer");

api.MapGet("/maps/{id}/files/{name}", (string id, string name, MapRegistry maps) =>
{
    var m = maps.Get(id);
    name = name.ToLowerInvariant();
    if (m is null || !m.Files.ContainsKey(name)) return Results.NotFound();
    var path = MapRegistry.FindFile(m.Dir, name);
    return path is null ? Results.NotFound() : Results.File(path, name.EndsWith(".json") ? "application/json" : "application/octet-stream", enableRangeProcessing: true);
}).RequireAuthorization("Viewer");

api.MapGet("/maps/{id}/blocks", (string id, MapRegistry maps, BlockStore blocks) =>
{
    if (maps.Get(id) is null) return Results.NotFound();
    var (rev, list) = blocks.GetAll(id);
    var land = blocks.GetLand(id).Blocks;
    return Results.Ok(new { rev, blocks = list.ToDictionary(b => b.Block.ToString(), b => Convert.ToBase64String(b.Data)),
        land = land.ToDictionary(b => b.Block.ToString(), b => Convert.ToBase64String(b.Data)) });
}).RequireAuthorization("Viewer");

api.MapPut("/maps/{id}/blocks", async (string id, HttpContext ctx, MapRegistry maps, BlockStore blocks, LiveHub hub, BlocksBody body) =>
{
    var m = maps.Get(id);
    if (m is null) return Results.NotFound();
    if (body.Blocks is null && body.Land is null) return Err(400, "Send { blocks: { blockIndex: base64 }, land: { blockIndex: base64 } }.");
    var edits = new List<BlockEdit>();
    var land = new List<BlockEdit>();
    try
    {
        foreach (var (key, b64) in body.Blocks ?? [])
            edits.Add(new BlockEdit(int.Parse(key), Convert.FromBase64String(b64 ?? "")));
        foreach (var (key, b64) in body.Land ?? [])
            land.Add(new BlockEdit(int.Parse(key), Convert.FromBase64String(b64 ?? "")));
        var me = Current(ctx);
        long rev = blocks.Save(m, me.Id, edits, land);
        await hub.PublishBlocks(new BlockChange(id, rev, me.Id, me.Username, edits, land, body.Cid));
        return Results.Ok(new { rev });
    }
    catch (Exception e) when (e is FormatException or OverflowException) { return Err(400, "Block keys must be numbers and data base64."); }
    catch (BlockException e) { return Err(400, e.Message); }
}).RequireAuthorization("Builder");

api.MapGet("/maps/{id}/export", (string id, MapRegistry maps, BlockStore blocks) =>
{
    var m = maps.Get(id);
    return m is null ? Results.NotFound() : Results.File(blocks.ExportZip(m), "application/zip", $"{id}-{(blocks.HasLandEdits(id) ? "map" : "statics")}.zip");
}).RequireAuthorization("Builder");

api.MapPost("/maps/{id}/publish", (string id, MapRegistry maps, BlockStore blocks) =>
{
    var m = maps.Get(id);
    if (m is null) return Results.NotFound();
    string dir = Path.Combine(dataPath, "published", id);
    Directory.CreateDirectory(dir);
    var files = blocks.MergedFiles(m);
    foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(dir, name), bytes);
    return Results.Ok(new { path = dir, files = files.Select(f => f.Name).ToArray() });
}).RequireAuthorization("Admin");

app.Map("/api/live/{id}", async (string id, HttpContext ctx, MapRegistry maps, LiveHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (maps.Get(id) is null) { ctx.Response.StatusCode = 404; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.Run(id, Current(ctx), socket, ctx.RequestAborted);
}).RequireAuthorization("Viewer");

// ============================================================================ projects
api.MapGet("/projects", (ProjectStore projects) => Results.Ok(projects.List())).RequireAuthorization("Viewer");

api.MapPost("/projects", async (HttpContext ctx, ProjectStore projects) =>
{
    try
    {
        long id = projects.Create(await Body(ctx), Current(ctx).Id);
        return Results.Ok(new { id, rev = 1 });
    }
    catch (ProjectException e) { return Err(400, e.Message); }
}).RequireAuthorization("Builder");

api.MapGet("/projects/{id:long}", (long id, int? rev, HttpContext ctx, ProjectStore projects) =>
{
    var p = projects.Get(id, rev);
    if (p is null) return Results.NotFound();
    ctx.Response.Headers["X-Revision"] = p.Value.Rev.ToString();
    return Results.Bytes(p.Value.Json, "application/json");
}).RequireAuthorization("Viewer");

api.MapPut("/projects/{id:long}", async (long id, int baseRev, HttpContext ctx, ProjectStore projects) =>
{
    try { return Results.Ok(new { rev = projects.Save(id, baseRev, await Body(ctx), Current(ctx).Id) }); }
    catch (ProjectConflictException e) { return Results.Json(new { error = e.Message, latest = e.Latest, by = e.By, at = e.At }, statusCode: 409); }
    catch (ProjectException e) { return Err(e.Message == "No such project." ? 404 : 400, e.Message); }
}).RequireAuthorization("Builder");

api.MapGet("/projects/{id:long}/history", (long id, ProjectStore projects) => Results.Ok(projects.History(id))).RequireAuthorization("Viewer");

api.MapDelete("/projects/{id:long}", (long id, ProjectStore projects) =>
    projects.Delete(id) ? Results.Ok(new { ok = true }) : Results.NotFound()).RequireAuthorization("Admin");

var buildLocks = new ConcurrentDictionary<long, SemaphoreSlim>();
api.MapPost("/projects/{id:long}/build", async (long id, BuildRequest body, HttpContext ctx, ProjectStore projects, MapRegistry maps, BlockStore blocks, LiveHub hub) =>
{
    var stored = projects.Get(id);
    if (stored is null) return Results.NotFound();
    var gate = buildLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    if (!await gate.WaitAsync(0)) return Err(409, "This project is already being built.");
    try
    {
        var project = ProjectStore.Parse(stored.Value.Json);
        int facet = body.Facet ?? GuessFacet(project.Width, project.Height);
        if (facet is < 0 or > 5) return Err(400, "Facet must be 0-5.");
        string mapId = $"project-{id}", dir = Path.Combine(maps.BuiltMapsDir, mapId);
        var me = Current(ctx);
        RebaseResult? kept = null;
        int discarded = 0;
        await Task.Run(() =>
        {
            var built = project.Build(facet, TransitionTable.BuiltIn, ShardForge.Core.Data.MultiData.TryLoad(options.ClientPath));
            // Walk edits on the previous build are kept: rebased onto the new map when the size is the same.
            var old = maps.Get(mapId);
            if (old is not null && old.Width == built.Width && old.Height == built.Height)
                kept = blocks.Rebase(mapId, FacetLoader.Load(old.Dir, old.Facet, old.FacetInfo), built, me.Id);
            else if (old is not null) discarded = blocks.Clear(mapId);
            if (old is not null && old.Facet != facet) foreach (var f in Directory.GetFiles(dir)) if (Path.GetFileName(f).Contains(old.Facet.ToString())) File.Delete(f);
            Directory.CreateDirectory(dir);
            FacetWriter.WriteAll(built, dir, facet);
            project.WriteLayout(dir);
            File.Delete(Path.Combine(dir, "places.json")); // older builds
            File.WriteAllText(Path.Combine(dir, "map.json"), JsonSerializer.Serialize(new BuiltMapMeta(
                project.Name, facet, project.Width, project.Height, id, stored.Value.Rev, me.Username, Db.Now())));
        });
        await hub.PublishRebuilt(mapId, me.Username);
        return Results.Ok(new { mapId, facet, rev = stored.Value.Rev, discardedEdits = discarded,
            keptEdits = kept is null ? null : new { statics = kept.StaticBlocks, land = kept.LandBlocks, droppedStatics = kept.DroppedStatics } });
    }
    catch (ProjectException e) { return Err(400, e.Message); }
    catch (InvalidDataException e) { return Err(400, e.Message); }
    finally { gate.Release(); }
}).RequireAuthorization("Builder");

// ============================================================================ house designs (prefabs)
api.MapGet("/prefabs", (PrefabStore prefabs) => Results.Ok(prefabs.All().Select(p => new
{
    id = p.Id, name = p.Name, data = p.Data, createdBy = p.CreatedByName, created = p.Created, updated = p.Updated,
}))).RequireAuthorization("Viewer");

api.MapPost("/prefabs", async (HttpContext ctx, PrefabStore prefabs, LiveHub live) =>
{
    try
    {
        var (name, data, bytes) = await PrefabBody(ctx);
        var id = prefabs.Create(name, data, bytes, Current(ctx).Id);
        await live.PublishPrefabs(Current(ctx).Username, "saved", id, name.Trim());
        return Results.Ok(new { id });
    }
    catch (PrefabException e) { return Err(400, e.Message); }
    catch (JsonException) { return Err(400, "Send { name, data } as JSON."); }
}).RequireAuthorization("Builder");

api.MapPut("/prefabs/{id:long}", async (long id, HttpContext ctx, PrefabStore prefabs, LiveHub live) =>
{
    try
    {
        var (name, data, bytes) = await PrefabBody(ctx);
        prefabs.Update(id, name, data, bytes, Current(ctx).Id);
        await live.PublishPrefabs(Current(ctx).Username, "updated", id, name.Trim());
        return Results.Ok(new { id });
    }
    catch (PrefabException e) { return Err(e.Message == "No such design." ? 404 : 400, e.Message); }
    catch (JsonException) { return Err(400, "Send { name, data } as JSON."); }
}).RequireAuthorization("Builder");

api.MapDelete("/prefabs/{id:long}", async (long id, HttpContext ctx, PrefabStore prefabs, LiveHub live) =>
{
    var p = prefabs.Get(id);
    if (p is null) return Results.NotFound();
    var me = Current(ctx);
    if (me.Role < Role.Admin && p.CreatedBy != me.Id) return Err(403, "Only its maker or an admin can delete a design.");
    prefabs.Delete(id);
    await live.PublishPrefabs(me.Username, "deleted", id, p.Name);
    return Results.Ok(new { ok = true });
}).RequireAuthorization("Builder");

async Task<(string Name, JsonElement Data, int Bytes)> PrefabBody(HttpContext ctx)
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    if (ms.Length > PrefabStore.MaxBytes) throw new PrefabException("That design is too big (over 4 MB).");
    using var doc = JsonDocument.Parse(ms.ToArray());
    var root = doc.RootElement;
    string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
    var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
    return (name, data, (int)ms.Length);
}

// ============================================================================ start
app.Lifetime.ApplicationStarted.Register(() =>
{
    var users = app.Services.GetRequiredService<UserStore>();
    var maps = app.Services.GetRequiredService<MapRegistry>();
    app.Logger.LogInformation("ShardForge Server {Version}", version);
    app.Logger.LogInformation("Data: {Data}", dataPath);
    var client = ServerSettings.Check(options.ClientPath);
    app.Logger.LogInformation("Client: {Client} ({State})", options.ClientPath, client.Ok ? $"{maps.All().Count} maps found" : "not set: choose it in the browser");
    string url = (app.Urls.FirstOrDefault() ?? "http://localhost:2595").Replace("://0.0.0.0", "://localhost").Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://[::]", "://localhost");
    Console.WriteLine();
    Console.WriteLine($"  ShardForge is running: open {url} in your browser.");
    Console.WriteLine(users.Count() == 0 ? "  First run: create the admin account and pick your UO client folder there." : "  Keep this window open while you work; close it (or press Ctrl+C) to stop the server.");
    Console.WriteLine();
    if (options.OpenBrowser && OperatingSystem.IsWindows() && Environment.UserInteractive)
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { app.Logger.LogDebug(e, "Couldn't open a browser"); }
});
app.Run();

// ============================================================================ helpers
static Role Level(ClaimsPrincipal p) => Enum.TryParse<Role>(p.FindFirstValue("level"), out var r) ? r : 0;

static async Task SignIn(HttpContext ctx, User u)
{
    var id = new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.Username),
        new Claim("level", ((int)u.Role).ToString()), new Claim("stamp", u.SecurityStamp),
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id), new AuthenticationProperties { IsPersistent = true });
}

static User Current(HttpContext ctx) => ctx.Items["user"] as User
    ?? ctx.RequestServices.GetRequiredService<UserStore>().Get(long.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!))!;

static object Me(User u) => new { id = u.Id, username = u.Username, role = u.Role.ToString(), level = (int)u.Role };

static IResult Err(int status, string message) => Results.Json(new { error = message }, statusCode: status);

static bool IsLocal(HttpContext ctx) => ctx.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);

static async Task<byte[]> Body(HttpContext ctx)
{
    using var ms = new MemoryStream();
    var buffer = new byte[81920];
    int n;
    while ((n = await ctx.Request.Body.ReadAsync(buffer)) > 0)
    {
        ms.Write(buffer, 0, n);
        if (ms.Length > ProjectStore.MaxProjectBytes) throw new ProjectException("Projects must be under 64 MB.");
    }
    return ms.ToArray();
}

static int GuessFacet(int w, int h) => (w, h) switch
{
    (1448, 1448) => 4, (2304, 1600) => 2, (2560, 2048) => 3, (1280, 4096) => 5, (7168, 4096) => 0, _ => 0,
};

record Credentials(string? Username, string? Password, string? ClientPath = null);
record PathBody(string? Path);
record SettingsBody(string? ClientPath);
record PasswordChange(string? Current, string? Password);
record NewUser(string? Username, string? Password, string? Role);
record UserPatch(string? Role, string? Password, bool? Disabled);
record BlocksBody(Dictionary<string, string?>? Blocks, Dictionary<string, string?>? Land = null, string? Cid = null);
record BuildRequest(int? Facet);

public partial class Program;
