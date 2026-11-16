namespace ShardForge.Core.Map;

/// <summary>A land tile: the terrain graphic and altitude of one map cell.</summary>
public readonly record struct LandCell(ushort TileId, sbyte Z);

/// <summary>A static item baked into the map (tree, rock, wall...). X/Y are 0-7 within the block.</summary>
public readonly record struct StaticTile(ushort TileId, byte X, byte Y, sbyte Z, ushort Hue);

/// <summary>Known facet sizes. Map files carry no dimensions, so they come from here or from staidx size.</summary>
public sealed record FacetInfo(int Index, string Name, int Width, int Height)
{
    public int BlockWidth => Width / 8;
    public int BlockHeight => Height / 8;
    public int BlockCount => BlockWidth * BlockHeight;

    public static readonly IReadOnlyList<FacetInfo> Known =
    [
        new(0, "Felucca", 7168, 4096),
        new(1, "Trammel", 7168, 4096),
        new(2, "Ilshenar", 2304, 1600),
        new(3, "Malas", 2560, 2048),
        new(4, "Tokuno", 1448, 1448),
        new(5, "TerMur", 1280, 4096),
    ];

    public static FacetInfo ForIndex(int index) =>
        Known.FirstOrDefault(f => f.Index == index)
        ?? throw new ArgumentOutOfRangeException(nameof(index), $"Unknown facet {index}; pass explicit dimensions.");
}

/// <summary>
/// An entire facet in memory: land cells plus statics, organised by 8x8 block like the client files.
/// Blocks are indexed column-major (blockIndex = bx * BlockHeight + by), matching the MUL layout.
/// </summary>
public sealed class Facet
{
    public FacetInfo Info { get; }
    public int Width => Info.Width;
    public int Height => Info.Height;

    /// <summary>Land cells, row-major: index = x + y * Width.</summary>
    public LandCell[] Land { get; }

    /// <summary>4-byte header preceding each land block (unused by the client; kept for round-tripping).</summary>
    public uint[] LandBlockHeaders { get; }

    /// <summary>Statics per block, column-major block index.</summary>
    public StaticTile[][] Statics { get; }

    /// <summary>The "extra" field of each staidx entry (kept for round-tripping).</summary>
    public int[] StaticIndexExtra { get; }

    public Facet(FacetInfo info)
    {
        Info = info;
        Land = new LandCell[info.Width * info.Height];
        LandBlockHeaders = new uint[info.BlockCount];
        Statics = new StaticTile[info.BlockCount][];
        Array.Fill(Statics, Array.Empty<StaticTile>());
        StaticIndexExtra = new int[info.BlockCount];
    }

    public int BlockIndex(int bx, int by) => bx * Info.BlockHeight + by;

    public LandCell GetLand(int x, int y) => Land[x + y * Width];
    public void SetLand(int x, int y, LandCell cell) => Land[x + y * Width] = cell;

    public StaticTile[] GetBlockStatics(int bx, int by) => Statics[BlockIndex(bx, by)];

    public int StaticCount => Statics.Sum(s => s.Length);
}
