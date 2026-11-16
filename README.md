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

> Status: early prototype. Read/render existing maps, paint a new world in the editor, ask Claude to shape it,
> and build it into UO map files with proper terrain transitions. Not yet tested in a live client.

## Why

[CentrED#](https://github.com/kaczy93/centredsharp) is great for hand-editing an existing map, but
building a whole new facet tile by tile is impractical. Older paint-to-map tools (Dragon, UO Landscaper)
haven't kept up with modern clients. This project tries to fill that gap.

## Editor

`editor/index.html` is a single-page world editor (open it in a browser, or use the hosted copy on claude.ai):

- **Paint** 12 terrains (water, grass, forest, jungle, sand, dirt, mountain rock, snow, lava, cobblestone road,
  farmland, cave floor), **sculpt** altitude (raise, lower, smooth, flatten) and **thin or thicken vegetation**.
- **Preview population**: trees, rocks, grasses and flowers are placed by a deterministic hash shared with the
  exporter, so the preview is exactly what gets built.
- **Ask Claude**: describe a change ("a snowy range in the north with a river to the sea") and Claude turns it
  into edit operations (island, mountain, ellipse, polygon, path, patches, density, smooth, beaches, fill).
  Every change can be undone.
- **Export** a `.shardforge.json` project (layers gzip-compressed; a full 7168 x 4096 world is about 1 MB).
- Sizes from 512 x 512 up to 7168 x 4096, including every standard facet size.

Shortcuts: B paint, G fill, R raise, L lower, S smooth, F flatten, C/M fewer/more plants, I pick, `[` `]` brush
size, Ctrl+Z / Ctrl+Y, right-drag or space-drag to pan, wheel to zoom.

## Walk

`walk/index.html` is an ultralight client for walking a map at ground level, with your own client's art:

- Open your UO client folder (built maps in subfolders, like `ShardForge-test`, are found too), pick a map, load.
- Isometric view with stretched, lit terrain and statics; arrow keys / WASD to walk, Shift to run.
- UO-like movement: impassable land (water, mountain rock), trees and walls block; you step up 2 onto items
  and climb gentle slopes. Fly mode goes anywhere.
- Search statics by name or hex id, click to place on the ground, Delete/right-click to remove, Ctrl+Z to undo.
- Export the edited `staidx#.mul` + `statics#.mul` as a zip.

Everything is read locally in the browser; only the art file, tiledata and one map are read.

## Roadmap

1. ✅ **Reader + renderer**: map, statics, tiledata, radarcol (MUL and UOP), facet to PNG.
2. ✅ **Writer**: map#.mul, map#LegacyMUL.uop, staidx/statics; verified by reading back.
3. ✅ **Editor prototype** with Claude assistance and project export; `build` turns a project into map files.
4. ✅ **Transition tiles**: edge and corner tiles between terrains, learned from the client's own maps
   (`mine-transitions`), filtered by art so only true blends are used.
5. ✅ **Craggy mountains**: rock and snow get UO-like relief at build time; rivers carve valleys.
6. ✅ **Full-size maps**: the editor handles 7168 x 4096 (chunked undo, compressed projects).
7. **In-game test**: load a built facet in ClassicUO + ServUO.
8. **Real tile art in the editor** when zoomed in (the art reader already exists in Core).
9. **Coastlines**: shallow-water statics along shores, like the original maps.
10. ✅ **Walk mode** (first version): walk at ground level with real art, place/remove statics, export.
    Next: multis (whole houses), hues, animated character, live sync with a server.
11. **Places**: towns, dungeon entrances, regions and spawners exported for ServUO.
12. **ShardForge Server**: host the editor and Walk from your own PC (a small self-hosted server alongside
    ServUO) for complete web-based cooperative world building: accounts and user levels (viewer, builder,
    admin), shared projects and maps, several people editing at once with live updates, change history
    with per-user undo/review, and one-click publish of a reviewed map to the shard.

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

## Samples

`samples/test-isles.shardforge.json` is the in-game test world: a Tokuno-sized map (build with `--facet 4`)
with cobblestone plazas at the exact positions and altitudes of Tokuno's three moongate destinations, so
players arriving through a public moongate land on solid ground.

## Layout

| Path | What |
|---|---|
| `editor` | Browser world editor (single HTML file) |
| `walk` | Browser walk-around client (single HTML file) |
| `src/ShardForge.Core` | File formats (UOP, map, statics, tiledata, radarcol), project build, rendering, PNG output |
| `src/ShardForge.Cli` | `shardforge` command-line tool |
| `tests/ShardForge.Tests` | Unit tests (synthetic data, no game files needed) |

## License

MIT
