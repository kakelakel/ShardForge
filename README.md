# ShardForge

*Forge your own shard.*

A complete worldbuilder for Ultima Online shards (ServUO / ModernUO / ClassicUO).

Paint your world as images and get real UO map files out:

```
terrain.png  ─┐
height.png   ─┼─►  ShardForge   ─►  map#.mul / statics#.mul / staidx#.mul (or .uop)
statics.png  ─┘   (tiles + transitions + scattered statics)
```

Then polish towns and dungeons by hand in [CentrED#](https://github.com/kaczy93/centredsharp).

> Status: early. Step 1 (read + render existing maps) works.

## Why

[CentrED#](https://github.com/kaczy93/centredsharp) is great for hand-editing an existing map, but
building a whole new facet tile by tile is impractical. Older paint-to-map tools (Dragon, UO Landscaper)
haven't kept up with modern clients. This project tries to fill that gap.

## Roadmap

1. ✅ **Reader + renderer**: load `map#` / `statics#` / `staidx#` (MUL and UOP) plus `tiledata` and `radarcol`,
   and render a facet to PNG. Verify against real client files.
2. **Round-trip**: write files back byte-identical so we can trust the writer.
3. **Terrain converter**: color-coded terrain image + heightmap → land tiles, with correct
   transition tiles between terrain types.
4. **Static scatter**: rules-based trees, rocks, grass and so on per terrain type, written to statics.
5. **Painting front end** (maybe): a browser-based canvas for painting terrain, height and biomes.

## Requirements

- .NET 10 SDK
- A legally obtained Ultima Online Classic client (no game assets are included in this repo)

## Usage

```sh
dotnet build

# Summary of a facet: size, altitude range, most common land and static tiles
dotnet run --project src/ShardForge.Cli -- info "C:\Games\Electronic Arts\Ultima Online Classic" 1

# Render a facet top-down with the client's radar colours (add --scale 4 for a quarter-size preview)
dotnet run --project src/ShardForge.Cli -- render "C:\Games\Electronic Arts\Ultima Online Classic" 1 trammel.png
```

Facets: 0 Felucca, 1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 TerMur.
Land is read from `map#LegacyMUL.uop` (modern clients) or `map#.mul`; statics from `staidx#.mul` / `statics#.mul`.

## Layout

| Path | What |
|---|---|
| `src/ShardForge.Core` | File formats (UOP, map, statics, tiledata, radarcol), rendering, PNG output |
| `src/ShardForge.Cli` | `shardforge` command-line tool |
| `tests/ShardForge.Tests` | Unit tests (synthetic data, no game files needed) |

## License

MIT
