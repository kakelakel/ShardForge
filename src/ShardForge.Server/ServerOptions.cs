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

    /// <summary>PBKDF2 iterations for password hashes (lower only in tests).</summary>
    public int PasswordIterations { get; set; } = 210_000;

    public string ResolvedDataPath(string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(DataPath) ? DataPath : Path.Combine(contentRoot, DataPath));
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
