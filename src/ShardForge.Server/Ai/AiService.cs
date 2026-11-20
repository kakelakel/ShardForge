using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ShardForge.Server.Ai;

public sealed class AiException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The AI assistants the editor and Walk can ask: ChatGPT (OpenAI's API, or any service that speaks it), Claude
/// (Anthropic's API) and local models through Ollama. The admin sets them up on the Admin page; API keys are kept
/// in data/ai.json on the server and never sent to a browser. Each request is one prompt in, one JSON plan out:
/// the pages turn the plan into the same edits a person would make, so everything stays undoable.
/// </summary>
public sealed class AiService(string dataPath, ILogger<AiService> log)
{
    public sealed class Provider
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        /// <summary>Where to send requests (blank: the provider's own address). OpenAI-compatible services and
        /// Ollama on another machine are set here.</summary>
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    public sealed class Config
    {
        [JsonPropertyName("openai")] public Provider OpenAi { get; set; } = new() { Model = Defaults["openai"].Model };
        [JsonPropertyName("anthropic")] public Provider Anthropic { get; set; } = new() { Model = Defaults["anthropic"].Model };
        [JsonPropertyName("ollama")] public Provider Ollama { get; set; } = new() { Model = Defaults["ollama"].Model, Url = Defaults["ollama"].Url };
        /// <summary>Old single limit (before limits per level); read once and moved into <see cref="Limits"/>.</summary>
        [JsonPropertyName("dailyLimit")] public int? DailyLimit { get; set; }
        /// <summary>Requests per person per day, by level: 0 = can't use it, -1 = no limit.</summary>
        [JsonPropertyName("limits")] public Dictionary<string, int>? Limits { get; set; }
    }

    /// <summary>Levels that can build (viewers only look, so they never get the assistant).</summary>
    public static readonly string[] Levels = ["Decorator", "Worldbuilder", "Admin"];
    public static readonly IReadOnlyDictionary<string, int> DefaultLimits = new Dictionary<string, int> { ["Decorator"] = 30, ["Worldbuilder"] = 100, ["Admin"] = -1 };

    /// <summary>Today's allowance for a level: 0 none, -1 no limit.</summary>
    public int LimitFor(ShardForge.Server.Auth.Role role)
    {
        if (role < ShardForge.Server.Auth.Role.Decorator) return 0;
        var l = Current.Limits ??= new(DefaultLimits);
        return l.TryGetValue(role.ToString(), out var v) ? v : DefaultLimits[role.ToString()];
    }
    public int UsedToday(long userId) => _used.TryGetValue((userId, DateOnly.FromDateTime(DateTime.UtcNow)), out var n) ? n : 0;

    /// <summary>A person's allowance today, for the pages.</summary>
    public object Allowance(ShardForge.Server.Auth.User u)
    {
        int limit = LimitFor(u.Role), used = UsedToday(u.Id);
        return new { limit, used, left = limit < 0 ? -1 : Math.Max(0, limit - used) };
    }

    private static Config Migrate(Config c)
    {
        if (c.Limits is null)
        {
            c.Limits = new(DefaultLimits);
            // An old single limit applied to everyone: keep it for decorators and worldbuilders.
            if (c.DailyLimit is int d) { int v = d == 0 ? -1 : d; c.Limits["Decorator"] = v; c.Limits["Worldbuilder"] = v; }
        }
        c.DailyLimit = null;
        foreach (var lvl in Levels) c.Limits.TryAdd(lvl, DefaultLimits[lvl]);
        return c;
    }

    public static readonly IReadOnlyDictionary<string, (string Name, string Model, string Url)> Defaults = new Dictionary<string, (string, string, string)>
    {
        ["openai"] = ("ChatGPT", "gpt-5.6-sol", "https://api.openai.com/v1"),
        ["anthropic"] = ("Claude", "claude-sonnet-5-5", "https://api.anthropic.com/v1"),
        ["ollama"] = ("Local (Ollama)", "llama3.1", "http://localhost:11434"),
    };

    private readonly object _gate = new();
    private string FilePath => Path.Combine(dataPath, "ai.json");
    private Config? _config;
    private readonly ConcurrentDictionary<(long User, DateOnly Day), int> _used = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(4) };

    public Config Current
    {
        get
        {
            lock (_gate)
            {
                if (_config is not null) return _config;
                try { _config = File.Exists(FilePath) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) : null; }
                catch (JsonException) { _config = null; }
                return _config = Migrate(_config ?? new Config());
            }
        }
    }

    public Provider? Get(string id) => id switch { "openai" => Current.OpenAi, "anthropic" => Current.Anthropic, "ollama" => Current.Ollama, _ => null };

    public bool Ready(string id) => Get(id) is { Enabled: true } p && (id == "ollama" ? !string.IsNullOrWhiteSpace(p.Url) : !string.IsNullOrWhiteSpace(p.Key));

    /// <summary>What everyone may see: which assistants are ready, by name and model, and their own allowance. No keys.</summary>
    public object Public(ShardForge.Server.Auth.User me) => new
    {
        providers = Defaults.Keys.Where(Ready).Select(id => new { id, name = Defaults[id].Name, model = Get(id)!.Model ?? Defaults[id].Model }).ToArray(),
        you = Allowance(me),
    };

    /// <summary>For the Admin page: everything but the keys themselves (their last four characters only).</summary>
    public object ForAdmin() => new
    {
        providers = Defaults.Keys.Select(id =>
        {
            var p = Get(id)!;
            return new
            {
                id, name = Defaults[id].Name, enabled = p.Enabled, model = p.Model ?? Defaults[id].Model, url = p.Url ?? "",
                defaultModel = Defaults[id].Model, defaultUrl = Defaults[id].Url,
                keySet = !string.IsNullOrWhiteSpace(p.Key), keyHint = string.IsNullOrWhiteSpace(p.Key) ? "" : "…" + p.Key[^Math.Min(4, p.Key.Length)..],
                ready = Ready(id),
            };
        }).ToArray(),
        limits = Levels.ToDictionary(l => l, l => Current.Limits![l]),
    };

    /// <summary>Changes a provider. A blank key keeps the old one; "-" removes it.</summary>
    public void Update(string id, bool? enabled, string? key, string? model, string? url, Dictionary<string, int>? limits)
    {
        lock (_gate)
        {
            if (limits is not null)
                foreach (var (lvl, v) in limits)
                {
                    if (!Levels.Contains(lvl)) throw new AiException($"Limits are for {string.Join(", ", Levels)}.", 400);
                    Current.Limits![lvl] = Math.Clamp(v, -1, 100_000);
                }
            if (string.IsNullOrEmpty(id)) { Save(); return; }
            var p = Get(id) ?? throw new AiException("Unknown assistant: " + id, 400);
            if (enabled is bool e) p.Enabled = e;
            if (key is not null) { key = key.Trim(); if (key == "-") p.Key = null; else if (key.Length > 0) p.Key = key; }
            if (model is not null) p.Model = string.IsNullOrWhiteSpace(model) ? Defaults[id].Model : model.Trim();
            if (url is not null)
            {
                url = url.Trim().TrimEnd('/');
                if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new AiException("That isn't an address (http://… or https://…).", 400);
                p.Url = url.Length == 0 ? (id == "ollama" ? Defaults[id].Url : null) : url;
            }
            Save();
        }
    }

    private void Save()
    {
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Counts a request against the person's allowance for their level (throws when there's none left).</summary>
    public int Charge(ShardForge.Server.Auth.User u)
    {
        int limit = LimitFor(u.Role);
        if (limit == 0) throw new AiException($"{u.Role}s can't use the AI assistant on this server. An admin can change that in Admin → AI assistants.", 403);
        var key = (u.Id, DateOnly.FromDateTime(DateTime.UtcNow));
        int n = _used.AddOrUpdate(key, 1, (_, v) => v + 1);
        if (limit > 0 && n > limit) { _used.AddOrUpdate(key, 0, (_, v) => v - 1); throw new AiException($"You've used today's {limit} requests (the allowance for {u.Role}s). It resets at midnight (UTC).", 429); }
        return n;
    }

    public sealed record Answer(string Text, string Provider, string Model, long Ms);

    /// <summary>Asks one assistant. <paramref name="system"/> sets the scene; the answer is the model's text.</summary>
    public async Task<Answer> Ask(string id, string system, string prompt, CancellationToken ct)
    {
        var p = Get(id) ?? throw new AiException("Unknown assistant: " + id, 400);
        if (!Ready(id)) throw new AiException($"{Defaults[id].Name} isn't set up. An admin can add it on the Admin page.", 400);
        string model = string.IsNullOrWhiteSpace(p.Model) ? Defaults[id].Model : p.Model!;
        string url = string.IsNullOrWhiteSpace(p.Url) ? Defaults[id].Url : p.Url!;
        var sw = Stopwatch.StartNew();
        string text = id switch
        {
            "openai" => await OpenAi(url, p.Key!, model, system, prompt, ct),
            "anthropic" => await Anthropic(url, p.Key!, model, system, prompt, ct),
            _ => await Ollama(url, model, system, prompt, ct),
        };
        log.LogInformation("AI {Provider} {Model}: {In} characters in, {Out} out, {Ms} ms", id, model, prompt.Length, text.Length, sw.ElapsedMilliseconds);
        return new Answer(text, id, model, sw.ElapsedMilliseconds);
    }

    private static async Task<JsonNode> Send(HttpRequestMessage req, string who, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AiException($"{who} didn't answer in time.", 504); }
        catch (HttpRequestException e) { throw new AiException($"Couldn't reach {who}: {e.Message}", 502); }
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string detail = "";
            try { detail = JsonNode.Parse(body)?["error"] is JsonNode e ? (e["message"]?.ToString() ?? e.ToString()) : ""; } catch (JsonException) { }
            throw res.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiException($"{who} refused the API key{(detail.Length > 0 ? ": " + detail : ".")}", 502),
                HttpStatusCode.TooManyRequests => new AiException($"{who} says too many requests right now (or the account is out of credit). {detail}".Trim(), 429),
                HttpStatusCode.NotFound => new AiException($"{who} doesn't know that model or address. {detail}".Trim(), 502),
                _ => new AiException($"{who} answered {(int)res.StatusCode}. {detail}".Trim(), 502),
            };
        }
        try { return JsonNode.Parse(body) ?? throw new AiException($"{who} sent an empty answer."); }
        catch (JsonException) { throw new AiException($"{who} sent something that isn't JSON."); }
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    /// <summary>OpenAI's Chat Completions (also spoken by Azure OpenAI, OpenRouter, LM Studio and others).</summary>
    private static async Task<string> OpenAi(string url, string key, string model, string system, string prompt, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url + "/chat/completions")
        {
            Content = Json(new
            {
                model,
                messages = new object[] { new { role = "system", content = system }, new { role = "user", content = prompt } },
                response_format = new { type = "json_object" },
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var j = await Send(req, "ChatGPT", ct);
        return j["choices"]?[0]?["message"]?["content"]?.ToString() ?? throw new AiException("ChatGPT's answer had no text.");
    }

    private static async Task<string> Anthropic(string url, string key, string model, string system, string prompt, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url + "/messages")
        {
            Content = Json(new { model, max_tokens = 16000, system, messages = new object[] { new { role = "user", content = prompt } } }),
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var j = await Send(req, "Claude", ct);
        var parts = j["content"]?.AsArray().Where(c => c?["type"]?.ToString() == "text").Select(c => c!["text"]!.ToString());
        string text = parts is null ? "" : string.Concat(parts);
        return text.Length > 0 ? text : throw new AiException("Claude's answer had no text.");
    }

    private static async Task<string> Ollama(string url, string model, string system, string prompt, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url + "/api/chat")
        {
            Content = Json(new { model, stream = false, format = "json", messages = new object[] { new { role = "system", content = system }, new { role = "user", content = prompt } } }),
        };
        var j = await Send(req, "Ollama", ct);
        return j["message"]?["content"]?.ToString() ?? throw new AiException("Ollama's answer had no text.");
    }
}
