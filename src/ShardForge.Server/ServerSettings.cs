using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShardForge.Server;

/// <summary>
/// Settings changed from the web pages (first-run setup, Admin): stored in data/settings.json and applied over
/// appsettings.json, so nobody has to edit config files. Today: the UO client folder.
/// </summary>
public sealed class ServerSettings(ServerOptions options, string dataPath)
{
    private readonly object _gate = new();
    private string FilePath => Path.Combine(dataPath, "settings.json");

    private sealed class Stored
    {
        [JsonPropertyName("clientPath")] public string? ClientPath { get; set; }
        /// <summary>"network": other computers on the network can connect; "local" (default): only this PC.</summary>
        [JsonPropertyName("listen")] public string? Listen { get; set; }
    }

    /// <summary>Whether the server listens on the network (from settings.json; applies at the next start).</summary>
    public bool NetworkAccess { get; private set; }

    /// <summary>Applies saved settings over the configuration (called at startup).</summary>
    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            if (!string.IsNullOrWhiteSpace(s?.ClientPath)) options.ClientPath = s.ClientPath;
            NetworkAccess = s?.Listen == "network";
        }
        catch (JsonException) { /* a broken file: keep the configuration */ }
    }

    public void SetClientPath(string path)
    {
        lock (_gate) { options.ClientPath = path; Save(); }
    }

    /// <summary>Lets other computers in (or not). Takes effect when the server next starts.</summary>
    public void SetNetworkAccess(bool on)
    {
        lock (_gate) { NetworkAccess = on; Save(); }
    }

    private void Save()
    {
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored { ClientPath = string.IsNullOrWhiteSpace(options.ClientPath) ? null : options.ClientPath, Listen = NetworkAccess ? "network" : "local" },
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>This PC's addresses on the local network (what the team types in), private ranges first.</summary>
    public static List<string> LanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."))
                        list.Add(a.Address.ToString());
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        static bool Private(string ip) => ip.StartsWith("192.168.") || ip.StartsWith("10.") || System.Text.RegularExpressions.Regex.IsMatch(ip, @"^172\.(1[6-9]|2\d|3[01])\.");
        return list.Distinct().OrderBy(ip => Private(ip) ? 0 : 1).ToList();
    }

    /// <summary>What a folder holds, for the setup and settings pages.</summary>
    public sealed record ClientCheck(string Path, bool Ok, string Note, int Facets);

    public static ClientCheck Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new("", false, "No folder chosen.", 0);
        path = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path)) return new(path, false, "Use the full path, like C:\\Program Files (x86)\\Electronic Arts\\Ultima Online Classic.", 0);
        if (!Directory.Exists(path)) return new(path, false, "That folder doesn't exist on this PC.", 0);
        bool tiledata = Maps.MapRegistry.FindFile(path, "tiledata.mul") is not null;
        bool art = Maps.MapRegistry.FindFile(path, "artLegacyMUL.uop") is not null || Maps.MapRegistry.FindFile(path, "art.mul") is not null;
        int facets = Enumerable.Range(0, 6).Count(f => Maps.MapRegistry.FindFile(path, $"map{f}LegacyMUL.uop") is not null || Maps.MapRegistry.FindFile(path, $"map{f}.mul") is not null);
        if (!tiledata || !art)
            return new(path, false, "This isn't a UO client folder: " + (!tiledata && !art ? "tiledata.mul and the art files are missing." : !tiledata ? "tiledata.mul is missing." : "the art files are missing."), facets);
        return new(path, true, facets > 0 ? $"UO client found: {facets} facet{(facets == 1 ? "" : "s")}, art and tiledata." : "Art and tiledata found (no facets).", facets);
    }

    /// <summary>Client folders worth suggesting: where EA's installer and common setups put them.</summary>
    public static IReadOnlyList<ClientCheck> Detect(string? current)
    {
        var candidates = new List<string>();
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p)) candidates.Add(p); }
        Add(current);
        Add(Environment.GetEnvironmentVariable("SHARDFORGE_CLIENT"));
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Electronic Arts\EA Games\Ultima Online Classic", @"SOFTWARE\Electronic Arts\EA Games\Ultima Online Classic",
                                        @"SOFTWARE\WOW6432Node\Origin Worlds Online\Ultima Online\1.0", @"SOFTWARE\Origin Worlds Online\Ultima Online\1.0" })
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                    Add(k?.GetValue("InstallDir") as string);
                    if (k?.GetValue("ExePath") is string exe) Add(Path.GetDirectoryName(exe));
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var root in new[] { pf86, pf })
            {
                Add(Path.Combine(root, "Electronic Arts", "Ultima Online Classic"));
                Add(Path.Combine(root, "EA Games", "Ultima Online Classic"));
                Add(Path.Combine(root, "Ultima Online"));
            }
            foreach (var drive in new[] { "C:\\", "D:\\", "E:\\" })
            {
                Add(Path.Combine(drive, "Games", "Electronic Arts", "Ultima Online Classic"));
                Add(Path.Combine(drive, "Games", "Ultima Online Classic"));
                Add(Path.Combine(drive, "Ultima Online Classic"));
                Add(Path.Combine(drive, "UO"));
            }
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add(Path.Combine(home, ".wine", "drive_c", "Program Files (x86)", "Electronic Arts", "Ultima Online Classic"));
            Add(Path.Combine(home, "UO"));
        }
        // The package unzipped inside the client folder, or next to it.
        string here = AppContext.BaseDirectory;
        Add(here); Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(here)));
        return candidates.Select(p => { try { return Path.GetFullPath(p); } catch { return null; } })
            .Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => Check(p)).Where(c => c.Ok).ToList();
    }
}
