using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ShardForge.Server.Tests;

/// <summary>A stand-in for OpenAI's, Anthropic's and Ollama's APIs: answers like them and remembers what it got.</summary>
public sealed class FakeAiApi : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Url { get; }
    public List<(string Path, string Auth, string ApiKey, JsonNode Body)> Seen { get; } = new();
    public string Reply { get; set; } = "{\"summary\":\"Did it.\",\"ops\":[]}";

    public FakeAiApi()
    {
        int port = new Random().Next(20000, 40000);
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            string path = ctx.Request.Url!.AbsolutePath;
            lock (Seen) Seen.Add((path, ctx.Request.Headers["Authorization"] ?? "", ctx.Request.Headers["x-api-key"] ?? "", JsonNode.Parse(body)!));
            object answer = path switch
            {
                "/v1/chat/completions" => new { choices = new[] { new { message = new { role = "assistant", content = Reply } } } },
                "/v1/messages" => new { content = new[] { new { type = "text", text = Reply } } },
                "/api/chat" => new { message = new { role = "assistant", content = Reply } },
                _ => new { error = new { message = "no such path" } },
            };
            if (ctx.Request.Headers["Authorization"] == "Bearer wrong") { ctx.Response.StatusCode = 401; answer = new { error = new { message = "Incorrect API key provided" } }; }
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer));
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose() { _listener.Stop(); _listener.Close(); }
}

public class AiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient Anon()
    {
        var c = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-ShardForge", "1");
        return c;
    }

    private async Task<HttpClient> As(string user)
    {
        var c = Anon();
        var status = await c.GetFromJsonAsync<JsonElement>("/api/status");
        var res = status.GetProperty("needsSetup").GetBoolean() && user == "admin"
            ? await c.PostAsJsonAsync("/api/setup", new { username = user, password = "correct horse" })
            : await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = "correct horse" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        c.DefaultRequestHeaders.Add("Cookie", res.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        return c;
    }

    private async Task Ensure(string name, string role)
    {
        var admin = await As("admin");
        var users = await admin.GetFromJsonAsync<JsonElement[]>("/api/users");
        if (users!.Any(u => u.GetProperty("username").GetString() == name)) return;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/users", new { username = name, password = "correct horse", role })).StatusCode);
    }

    [Fact]
    public async Task AdminsSetUpChatGptAndClaudeAndEveryoneCanAskWithoutSeeingTheKey()
    {
        Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1,localhost");
        using var fake = new FakeAiApi();
        await Ensure("deco", "Decorator");
        await Ensure("vera", "Viewer");
        var admin = await As("admin");
        var deco = await As("deco");
        var viewer = await As("vera");

        // Nothing set up yet; only admins may change it.
        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>("/api/ai")).GetProperty("providers").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await deco.PutAsJsonAsync("/api/ai/settings", new { provider = "openai", enabled = true, key = "sk-x" })).StatusCode);

        // ChatGPT (OpenAI's API) and Claude (Anthropic's), both pointed at the stand-in.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/ai/settings", new { provider = "openai", enabled = true, key = "sk-test-secret-1234", url = fake.Url + "/v1", model = "gpt-test" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/ai/settings", new { provider = "anthropic", enabled = true, key = "sk-ant-secret-9876", url = fake.Url + "/v1" })).StatusCode);

        // The key is never sent back, not even to admins: only its last four characters.
        string adminView = await admin.GetStringAsync("/api/ai/settings");
        Assert.DoesNotContain("secret", adminView);
        Assert.Contains("…1234", adminView);
        string everyone = await viewer.GetStringAsync("/api/ai");
        Assert.Contains("ChatGPT", everyone); Assert.Contains("Claude", everyone); Assert.DoesNotContain("sk-", everyone);

        // Viewers can't ask; decorators can, and the request reaches each API in its own format with the key.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/ai/ask", new { provider = "openai", prompt = "hi" })).StatusCode);
        fake.Reply = "{\"summary\":\"A tavern.\",\"ops\":[{\"op\":\"place\",\"id\":\"0x0B4F\",\"x\":1,\"y\":2}]}";
        var r = await deco.PostAsJsonAsync("/api/ai/ask", new { provider = "openai", prompt = "Furnish it", request = "furnish this as a tavern", where = "Walk" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var a = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("A tavern", a.GetProperty("text").GetString());
        Assert.Equal("gpt-test", a.GetProperty("model").GetString());
        var openAi = fake.Seen.Last(s => s.Path == "/v1/chat/completions");
        Assert.Equal("Bearer sk-test-secret-1234", openAi.Auth);
        Assert.Equal("Furnish it", openAi.Body["messages"]![1]!["content"]!.ToString());
        Assert.Equal("json_object", openAi.Body["response_format"]!["type"]!.ToString());

        Assert.Equal(HttpStatusCode.OK, (await deco.PostAsJsonAsync("/api/ai/ask", new { provider = "anthropic", prompt = "Furnish it" })).StatusCode);
        var claude = fake.Seen.Last(s => s.Path == "/v1/messages");
        Assert.Equal("sk-ant-secret-9876", claude.ApiKey);
        Assert.Equal("claude-sonnet-5-5", claude.Body["model"]!.ToString());

        // Logged per person, with what was asked.
        var audit = await admin.GetStringAsync("/api/audit?action=ai.ask");
        Assert.Contains("furnish this as a tavern", audit); Assert.Contains("deco", audit);

        // A refused key comes back as a plain message; the admin's Test button says so.
        await admin.PutAsJsonAsync("/api/ai/settings", new { provider = "openai", key = "wrong" });
        var bad = await admin.PostAsJsonAsync("/api/ai/test", new { provider = "openai" });
        Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        Assert.Contains("refused the API key", await bad.Content.ReadAsStringAsync());

        // The daily limit.
        await admin.PutAsJsonAsync("/api/ai/settings", new { provider = "openai", dailyLimit = 1 });
        var over = await deco.PostAsJsonAsync("/api/ai/ask", new { provider = "anthropic", prompt = "again" });
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        await admin.PutAsJsonAsync("/api/ai/settings", new { provider = "openai", dailyLimit = 200 });
    }
}
