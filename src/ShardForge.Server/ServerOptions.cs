namespace ShardForge.Server;

/// <summary>Settings from the "ShardForge" section of appsettings.json (or command-line overrides).</summary>
public sealed class ServerOptions
{
    /// <summary>Ultima Online client folder: art, tiledata, animdata and the original facets.</summary>
    public string ClientPath { get; set; } = "";

    /// <summary>Where the server keeps its database, built maps and published files.</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>Extra folders holding map files to offer (for example a ShardForge build output).</summary>
    public List<MapSource> Maps { get; set; } = new();

    /// <summary>First-run admin setup is only accepted from this PC unless this is true.</summary>
    public bool AllowRemoteSetup { get; set; }

    /// <summary>Sign-in and setup attempts allowed per minute from one address.</summary>
    public int LoginAttemptsPerMinute { get; set; } = 10;

    /// <summary>Open the dashboard in the default browser when the server starts (Windows).</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>PBKDF2 iterations for password hashes (lower only in tests).</summary>
    public int PasswordIterations { get; set; } = 210_000;

    /// <summary>Automatic backups of everything the team made.</summary>
    public BackupOptions Backups { get; set; } = new();

    public string ResolvedDataPath(string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(DataPath) ? DataPath : Path.Combine(contentRoot, DataPath));
}

public sealed class BackupOptions
{
    /// <summary>Where backups go; empty means data/backups. Another drive is safer.</summary>
    public string Path { get; set; } = "";
    /// <summary>A backup every this many hours (and at start when the last one is older).</summary>
    public double EveryHours { get; set; } = 6;
    /// <summary>The newest automatic backups kept, whatever their age.</summary>
    public int KeepRecent { get; set; } = 8;
    /// <summary>Then one per day for this many days.</summary>
    public int KeepDaily { get; set; } = 7;
    /// <summary>Then one per week for this many weeks.</summary>
    public int KeepWeekly { get; set; } = 4;
    /// <summary>Manual and before-restore backups are kept this many days.</summary>
    public int KeepManualDays { get; set; } = 30;
    /// <summary>Include the built map files (they can be rebuilt from their projects, but restoring is quicker).</summary>
    public bool IncludeMaps { get; set; } = true;
}

public sealed class MapSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Facet { get; set; }
    public string Path { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
}
