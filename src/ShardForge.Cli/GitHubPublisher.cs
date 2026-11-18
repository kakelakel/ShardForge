using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ShardForge.Core.Patching;

namespace ShardForge.Cli;

/// <summary>
/// Publishes a patch as a GitHub release: uploads only new or changed files plus manifest.json, as a draft
/// that becomes public (and "latest") only when every upload succeeded. Patchers read
/// https://github.com/{repo}/releases/latest/download/manifest.json.
/// </summary>
public sealed class GitHubPublisher(string repo, string token, TextWriter log)
{
    private readonly HttpClient _http = Create(token);

    public static string ManifestUrl(string repo) => $"https://github.com/{repo}/releases/latest/download/manifest.json";

    public async Task<string> Publish(string filesDir, string shard, string? release, string? notes, string? patcherExe)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")) throw new ArgumentException("--repo must look like owner/name.");
        release ??= DateTime.UtcNow.ToString("yyyy.MM.dd-HHmm");

        var previous = await LatestManifest();
        log.WriteLine(previous is null ? "No earlier release: publishing everything." : $"Latest release: {previous.Release} ({previous.Files.Count} files).");

        string outDir = Path.Combine(Path.GetTempPath(), "shardforge-patch-" + release);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var build = PatchMaker.Make(filesDir, outDir, shard, release,
            asset => $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(release)}/{Uri.EscapeDataString(asset)}", previous, notes);
        log.WriteLine($"Changed: {(build.Changed.Count == 0 ? "nothing" : string.Join(", ", build.Changed))}; unchanged: {build.Unchanged.Count}.");
        if (build.Changed.Count == 0 && previous is not null && patcherExe is null)
        {
            log.WriteLine("Nothing to publish: every file matches the latest release.");
            return previous.Release;
        }

        await EnsureRepoHasACommit(shard);
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(notes)) body.AppendLine(notes).AppendLine();
        body.AppendLine($"Updated files: {(build.Changed.Count == 0 ? "none" : string.Join(", ", build.Changed))}.");
        body.AppendLine().AppendLine($"Run the ShardForge Patcher to update. It reads `{ManifestUrl(repo)}`.");

        var created = await Api(HttpMethod.Post, $"repos/{repo}/releases", new
        {
            tag_name = release, name = $"{shard} {release}", body = body.ToString(), draft = true,
        });
        long id = created.GetProperty("id").GetInt64();
        try
        {
            foreach (var up in build.Uploads) await Upload(id, up.Name, up.Path, "application/gzip");
            if (patcherExe is not null) await Upload(id, "ShardForge-Patcher.zip", PatcherZip(patcherExe, shard, outDir), "application/zip");
            await Upload(id, "manifest.json", Path.Combine(outDir, "manifest.json"), "application/json");
            await Api(new HttpMethod("PATCH"), $"repos/{repo}/releases/{id}", new { draft = false, make_latest = "true" });
        }
        catch
        {
            log.WriteLine("Publishing failed; removing the unfinished draft. Players keep the previous release.");
            try { await _http.DeleteAsync($"https://api.github.com/repos/{repo}/releases/{id}"); } catch (HttpRequestException) { }
            throw;
        }
        log.WriteLine($"Published {release}: https://github.com/{repo}/releases/tag/{release}");
        return release;
    }

    public async Task<PatchManifest?> LatestManifest()
    {
        using var anon = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        anon.DefaultRequestHeaders.UserAgent.ParseAdd("ShardForge/0.1");
        using var res = await anon.GetAsync(ManifestUrl(repo));
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return PatchManifest.Parse(await res.Content.ReadAsStringAsync());
    }

    private async Task EnsureRepoHasACommit(string shard)
    {
        using var res = await _http.GetAsync($"https://api.github.com/repos/{repo}/commits?per_page=1");
        if (res.IsSuccessStatusCode) return;
        if (res.StatusCode != HttpStatusCode.Conflict) // 409 = empty repository
            throw new HttpRequestException($"Can't read {repo} ({(int)res.StatusCode}). Check the name and that the token may write to it.");
        string readme = $"""
            # {shard} patches

            Client patches for **{shard}**, published with [ShardForge](https://github.com/kakelakel/ShardForge).

            Players: download **ShardForge-Patcher.zip** from the [latest release](https://github.com/{repo}/releases/latest),
            unzip it into your Ultima Online folder and run it before playing. It downloads only what changed.

            The files here are the shard's own map data; the Ultima Online client itself is not included.
            """;
        await Api(HttpMethod.Put, $"repos/{repo}/contents/README.md", new
        {
            message = "Start the patch repository", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(readme)),
        });
    }

    private async Task Upload(long releaseId, string name, string path, string type)
    {
        long size = new FileInfo(path).Length;
        log.WriteLine($"  uploading {name} ({size / 1024.0 / 1024.0:F1} MB)");
        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Headers.ContentLength = size;
        using var res = await _http.PostAsync($"https://uploads.github.com/repos/{repo}/releases/{releaseId}/assets?name={Uri.EscapeDataString(name)}", content);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Upload of {name} failed ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
    }

    private async Task<JsonElement> Api(HttpMethod method, string path, object body)
    {
        using var req = new HttpRequestMessage(method, "https://api.github.com/" + path) { Content = JsonContent.Create(body) };
        using var res = await _http.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"GitHub said {(int)res.StatusCode} for {method} {path}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The patcher plus a patcher.json already pointing at this repository.</summary>
    private string PatcherZip(string exe, string shard, string outDir)
    {
        string zipPath = Path.Combine(outDir, "ShardForge-Patcher.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(exe, Path.GetFileName(exe), CompressionLevel.Optimal);
        var entry = zip.CreateEntry("patcher.json");
        using (var w = new StreamWriter(entry.Open()))
            w.Write(JsonSerializer.Serialize(new { shard, manifestUrl = ManifestUrl(repo), clientPath = "", launch = "" }, new JsonSerializerOptions { WriteIndented = true }));
        return zipPath;
    }

    private static HttpClient Create(string token)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ShardForge/0.1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>--token, then GITHUB_TOKEN / GH_TOKEN, then the GitHub CLI's login.</summary>
    public static string? FindToken(string? explicitToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken;
        foreach (var v in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } t) return t;
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) return null;
            string t = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && t.Length > 0 ? t : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
