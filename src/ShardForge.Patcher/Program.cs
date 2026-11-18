using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShardForge.Core.Patching;
using ShardForge.Patcher;

// ShardForge Patcher: brings a player's Ultima Online folder up to date with their shard's files.
//   ShardForge-Patcher                 update, then start the game if patcher.json says how
//   ShardForge-Patcher --check         only report what would change
//   ShardForge-Patcher --restore       put back the original files
//   ShardForge-Patcher --client <dir>  use this client folder (remembered)
//   ShardForge-Patcher --yes           don't wait for Enter at the end

var flags = new HashSet<string>(args.Where(a => a.StartsWith("--")), StringComparer.OrdinalIgnoreCase);
string? Arg(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool interactive = !flags.Contains("--yes") && !Console.IsInputRedirected;

string configPath = Path.Combine(AppContext.BaseDirectory, "patcher.json");
var config = PatcherConfig.Load(configPath);
var ui = new ConsoleUi(Console.IsOutputRedirected);

ui.Title($"ShardForge Patcher{(string.IsNullOrWhiteSpace(config.Shard) ? "" : " · " + config.Shard)}");
int code = 0;
try
{
    string manifestUrl = Arg("--manifest") ?? config.ManifestUrl;
    if (string.IsNullOrWhiteSpace(manifestUrl))
        throw new InvalidOperationException("patcher.json has no manifestUrl. Get the patcher from your shard's download page.");

    string? client = Arg("--client") ?? ClientFinder.Find(config.ClientPath, AppContext.BaseDirectory);
    if (client is null && interactive) client = ClientFinder.Ask();
    if (client is null) throw new InvalidOperationException("Couldn't find your Ultima Online folder. Run with --client \"C:\\path\\to\\Ultima Online\".");
    if (!string.Equals(config.ClientPath, client, StringComparison.OrdinalIgnoreCase)) { config.ClientPath = client; config.Save(configPath); }
    ui.Line($"Client folder: {client}");

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("ShardForge-Patcher/0.1");
    var patcher = new PatchClient(http, client);

    if (flags.Contains("--restore"))
    {
        var restored = patcher.Restore();
        ui.Done(restored.Count == 0 ? "Nothing to restore." : $"Restored {restored.Count} file(s): {string.Join(", ", restored)}");
    }
    else
    {
        ui.Line("Checking for updates…");
        var manifest = await patcher.FetchManifest(manifestUrl);
        ui.Line($"Latest release: {manifest.Release}{(manifest.Notes is { Length: > 0 } n ? " · " + n : "")}");
        var progress = new Progress<PatchProgress>(ui.Show);
        if (flags.Contains("--check"))
        {
            var outdated = patcher.Outdated(manifest, progress);
            ui.ClearProgress();
            ui.Done(outdated.Count == 0 ? "Up to date." : $"{outdated.Count} file(s) to update ({outdated.Sum(f => f.DownloadSize) / 1048576.0:F1} MB): {string.Join(", ", outdated.Select(f => f.Name))}");
        }
        else
        {
            var result = await patcher.Update(manifest, progress);
            ui.ClearProgress();
            ui.Done(result.Updated.Count == 0 ? $"Up to date with {result.Release}." :
                $"Updated {result.Updated.Count} file(s) to {result.Release} ({result.BytesDownloaded / 1048576.0:F1} MB downloaded): {string.Join(", ", result.Updated)}");
            if (!string.IsNullOrWhiteSpace(config.Launch) && !flags.Contains("--no-launch"))
            {
                string exe = Path.IsPathRooted(config.Launch) ? config.Launch : Path.Combine(client, config.Launch);
                ui.Line($"Starting {Path.GetFileName(exe)}…");
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = client, UseShellExecute = true });
                interactive = false;
            }
        }
    }
}
catch (Exception e) when (e is InvalidOperationException or InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
{
    ui.ClearProgress();
    ui.Error(e is UnauthorizedAccessException
        ? "Windows refused to write to the client folder. Close the game if it's running, or run the patcher as administrator."
        : e.Message);
    code = 1;
}
if (interactive) { Console.Write("Press Enter to close."); Console.ReadLine(); }
return code;

namespace ShardForge.Patcher
{
    public sealed class PatcherConfig
    {
        [JsonPropertyName("shard")] public string Shard { get; set; } = "";
        [JsonPropertyName("manifestUrl")] public string ManifestUrl { get; set; } = "";
        [JsonPropertyName("clientPath")] public string ClientPath { get; set; } = "";
        /// <summary>Program to start after patching, relative to the client folder (e.g. ClassicUO.exe).</summary>
        [JsonPropertyName("launch")] public string Launch { get; set; } = "";

        public static PatcherConfig Load(string path)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJson.Default.PatcherConfig) ?? new() : new(); }
            catch (JsonException) { return new(); }
        }

        public void Save(string path)
        {
            try { File.WriteAllText(path, JsonSerializer.Serialize(this, ConfigJson.Default.PatcherConfig)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* remembering the folder is a convenience */ }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PatcherConfig))]
    public partial class ConfigJson : JsonSerializerContext;

    public static class ClientFinder
    {
        public static bool LooksLikeClient(string? dir) =>
            !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) &&
            (File.Exists(Path.Combine(dir, "tiledata.mul")) || File.Exists(Path.Combine(dir, "client.exe")) || Directory.GetFiles(dir, "*LegacyMUL.uop").Length > 0);

        public static string? Find(string? configured, string exeDir)
        {
            if (LooksLikeClient(configured)) return Path.GetFullPath(configured!);
            if (LooksLikeClient(exeDir)) return Path.GetFullPath(exeDir);
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Electronic Arts\EA Games\Ultima Online Classic", @"SOFTWARE\Electronic Arts\EA Games\Ultima Online Classic" })
                    if (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)?.GetValue("InstallDir") is string d) candidates.Add(d);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                candidates.AddRange([Path.Combine(pf86, "Electronic Arts", "Ultima Online Classic"), Path.Combine(pf, "Electronic Arts", "Ultima Online Classic"),
                    @"C:\Games\Electronic Arts\Ultima Online Classic", @"C:\UO", @"C:\Ultima Online"]);
            }
            return candidates.FirstOrDefault(LooksLikeClient);
        }

        public static string? Ask()
        {
            Console.WriteLine("Where is Ultima Online installed? Paste the folder path (the one with client.exe or tiledata.mul):");
            for (int i = 0; i < 3; i++)
            {
                Console.Write("> ");
                string? dir = Console.ReadLine()?.Trim().Trim('"');
                if (string.IsNullOrEmpty(dir)) return null;
                if (LooksLikeClient(dir)) return Path.GetFullPath(dir);
                Console.WriteLine("That folder doesn't look like an Ultima Online client. Try again (or press Enter to stop).");
            }
            return null;
        }
    }

    public sealed class ConsoleUi(bool plain)
    {
        private int _lastLength;
        private long _lastDraw;

        public void Title(string text)
        {
            if (!plain) try { Console.Title = text; } catch (IOException) { } catch (PlatformNotSupportedException) { }
            Console.WriteLine(text);
            Console.WriteLine(new string('─', Math.Min(60, text.Length)));
        }

        public void Line(string text) { ClearProgress(); Console.WriteLine(text); }

        public void Done(string text)
        {
            if (!plain) Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ " + text);
            if (!plain) Console.ResetColor();
        }

        public void Error(string text)
        {
            if (!plain) Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("✗ " + text);
            if (!plain) Console.ResetColor();
        }

        public void Show(PatchProgress p)
        {
            string? line = p.Step switch
            {
                PatchStep.Hashing => $"Checking your files [{p.Index}/{p.Count}] {p.File}",
                PatchStep.Downloading => $"[{p.Index}/{p.Count}] {p.File}  {Bar(p.Done, p.Total)}  {p.Done / 1048576.0:F1}/{p.Total / 1048576.0:F1} MB",
                PatchStep.Installing => $"[{p.Index}/{p.Count}] Installing {p.File}",
                _ => null,
            };
            if (line is null) return;
            if (plain)
            {
                if (p.Step != PatchStep.Downloading) Console.WriteLine(line);
                return;
            }
            long now = Environment.TickCount64;
            if (p.Step == PatchStep.Downloading && now - _lastDraw < 100) return;
            _lastDraw = now;
            Console.Write("\r" + line.PadRight(_lastLength));
            _lastLength = line.Length;
        }

        public void ClearProgress()
        {
            if (_lastLength == 0) return;
            Console.Write("\r" + new string(' ', _lastLength) + "\r");
            _lastLength = 0;
        }

        private static string Bar(long done, long total)
        {
            const int width = 24;
            int fill = total <= 0 ? 0 : (int)Math.Clamp(done * width / total, 0, width);
            return "[" + new string('█', fill) + new string('░', width - fill) + $"] {(total <= 0 ? 0 : done * 100 / total),3}%";
        }
    }
}
