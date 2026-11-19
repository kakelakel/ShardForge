# ShardForge

*Forge your own shard.*

A complete worldbuilder for Ultima Online shards (ServUO / ModernUO / ClassicUO).

Paint your world as images and get real UO map files out:

```
terrain.png  ─┐
height.png   ─┼─►  ShardForge   ─►  map#.mul / statics#.mul / staidx#.mul (or .uop)
statics.png  ─┘   (tiles + transitions + scattered statics)
```

Then build and decorate by hand in **Walk**, ShardForge's own walk-around map editor, alone or with your team at
the same time. Walk is meant to replace a separate tool like [CentrED#](https://github.com/kaczy93/centredsharp)
for this. It places and removes statics and edits the land; whole houses (multis), hues and
select/move/copy are next (roadmap item 10).

> Status: 0.4 (early). Paint and generate worlds, lay out towns, build real UO map files with soft terrain edges,
> and walk and decorate them together through the self-hosted server. Not yet tested in a live client.

## Download

One package holds everything: the **server** with the **editor** and **Walk** built in, and the command-line tool.
Get **ShardForge-<version>-win-x64.zip** (or the Linux tarball) from
[Releases](https://github.com/kakelakel/ShardForge/releases), unzip it, start **ShardForge.exe**, and the browser
opens: create the admin account and pick your UO client folder (it's found for you). Nothing to install.
See [docs/SERVER.md](docs/SERVER.md) for sharing it with your team.

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
- **Settlements**: the Town tool (T) founds a settlement where you click, or press and drag out to size it (`[` `]`
  resize); a circle under the cursor shows it. Size can be Auto (hamlet, village, town or city from the radius).
  The Settlements panel sets its name, size, radius, style (rustic, fishing,
  frontier, town, city, desert, mountain), the material of its main roads, lanes and square (dirt, cobblestone,
  sand, grass, farmland), walls (cities only, always, never), how densely plots fill it, a **setting** that shapes the
  land around it (farmland ring, forest clearing, hilltop, waterfront with a dock plot, winter, desert oasis with a
  pond) and which **decorations** to place, and how many: street lamps, market stalls, square furnishings (benches,
  trees, a statue), signposts, street life (barrels, crates, water troughs, hitching posts), flowers and
  flowerpots, gardens, fields and hay, a graveyard. **Reroll** lays the
  last town out again with the same settings. Nothing is on a grid: an irregular square,
  main roads that wander out following the land (some fork partway), lanes that branch at odd angles and sometimes
  loop back into another road, and an irregular edge. Along the roads it lays out **building plots** (levelled,
  cleared ground facing its road, larger toward the outskirts) for you to build on by hand in Walk: an inn,
  shops, a smithy, a chapel and a hall near the square, homes, and farms with fenced fields on the outskirts.
  Trees stay between the plots; squares get a few market stalls, benches, lamps and a tree; signposts stand where
  roads leave; there are gardens and a graveyard. Cities get stone walls with long straight runs, jogs and gates.
- **Populate world** fills a whole map at once: it scores sites (flat land, near water), founds cities, towns,
  villages and hamlets with styles that fit their surroundings, joins them with country roads from exit to exit
  (routed around mountains and lakes; where one must cross water it stops at the banks for you to bridge), and adds named camps, ruins and
  shrines in the wilderness. A Trammel-size map (7168 x 4096) gets ~140 settlements and ~1,250 plots in about six
  seconds. Claude can do all of this too ("a walled city in the central valley and two fishing villages on the coast").
- **Export** a `.shardforge.json` project (layers gzip-compressed; a full 7168 x 4096 world is about 1 MB).
- Sizes from 512 x 512 up to 7168 x 4096, including every standard facet size.

Shortcuts: B paint, G fill, R raise, L lower, S smooth, F flatten, C/M fewer/more plants, I pick, T town, `[` `]` brush
size, Ctrl+Z / Ctrl+Y, right-drag or space-drag to pan, wheel to zoom.

## Walk

`walk/index.html` is an ultralight client for walking a map at ground level, with your own client's art:

- Open your UO client folder (built maps in subfolders, like `ShardForge-test`, are found too), pick a map, load.
- Isometric view with stretched, lit terrain and statics; arrow keys / WASD to walk, Shift to run.
- UO-like movement: impassable land (water, mountain rock), trees and walls block; you step up 2 onto items
  and climb gentle slopes. Fly mode goes anywhere.
- **Builder palette**: ~36,000 statics with art in a category tree (Nature, Building, Furniture, Lighting, Decor,
  Crafting, Items, Ships & siege, Magic & special, Other) built from tiledata names and flags; search by name or
  hex id; Favourites and Recent (kept in your browser); an Animated tab.
- **Animated statics** (fires, water, forges...) animate in the world and in the palette, from `animdata.mul`.
- **Placement modes**: single, line, rectangle fill, scatter brush (with density) and erase brush. Select several
  items and each placement picks one at random. Height offset (PgUp/PgDn), Alt-click picks up an item from the
  world, ghosts preview exactly what will be placed, and every click or stroke is one undo step.
- **Building plots**: maps built from a project show its plots on the ground (tinted, the road side drawn solid,
  labelled with their purpose and size) and list its towns, camps, ruins and shrines under Known places.
- **Land editing**: paint terrain (edges blend exactly like a map build; where two terrains can't blend, a
  strip of dirt goes between), raise, lower, flatten, smooth and set the ground with a soft brush. Statics
  resting on the ground move with it. Shared live and undoable like statics.
- Export the edited `staidx#.mul` + `statics#.mul` as a zip (with `map#.mul` and `map#LegacyMUL.uop` when the
  land was edited).

Everything is read locally in the browser; only the art file, tiledata and one map are read.

## Server

`src/ShardForge.Server` hosts the editor and Walk from your own PC for cooperative building: accounts with Viewer,
Builder and Admin levels, shared projects with revision history and conflict detection, **Build & walk** on the
server, and live co-building in Walk (everyone sees each other and every edit at once).

```sh
dotnet run --project src/ShardForge.Server     # then open http://localhost:2595 on this PC to create the admin
```

See [docs/SERVER.md](docs/SERVER.md) for settings, sharing it with your team, security and the API.

## Patches

Custom maps must be on the server **and** every player's client. `shardforge patch publish` uploads your shard's
files to GitHub Releases (only what changed), and the **ShardForge Patcher** brings each player's client up to date
before they play: checksummed downloads, backups of the originals, data files only. See [docs/PATCHER.md](docs/PATCHER.md).

## Roadmap

1. ✅ **Reader + renderer**: map, statics, tiledata, radarcol (MUL and UOP), facet to PNG.
2. ✅ **Writer**: map#.mul, map#LegacyMUL.uop, staidx/statics; verified by reading back.
3. ✅ **Editor prototype** with Claude assistance and project export; `build` turns a project into map files.
4. ✅ **Transition tiles**: edge and corner tiles between terrains, learned from the client's own maps
   (`mine-transitions`), filtered by art so only true blends are used. **Soft edges only**: every land border
   blends. Terrains that hardly blend (cobblestone and grass, forest and sand) get a strip of one that blends with
   both, speckles are smoothed, and corner shapes the client has no tile for use the nearest blend. The build
   reports the result ("no hard edges").
5. ✅ **Craggy mountains**: rock and snow get UO-like relief at build time; rivers carve valleys.
6. ✅ **Full-size maps**: the editor handles 7168 x 4096 (chunked undo, compressed projects).
7. **In-game test**: load a built facet in ClassicUO + ServUO.
8. **Real tile art in the editor** when zoomed in (the art reader already exists in Core).
9. **Coastlines**: shallow-water statics along shores, like the original maps.
10. ✅ **Walk mode** (first version): walk at ground level with real art, place/remove statics, export.
    Builder with categories, favourites, animation and placement modes.
    ✅ Land editing (paint with soft blends, raise, lower, flatten, smooth, set) and Walk edits kept when a
    project is rebuilt. Next, so Walk can replace CentrED: multis (whole houses, sized to plots),
    select/move/copy/delete with nudge and z adjust, hues, a minimap.
11. ✅ **Settlements** (first version): organic towns, villages and walled cities laid out as roads, squares and
    building plots; country roads; wilderness sites; Populate world; plots and places in Walk. Next: regions,
    guard zones and spawners exported for ServUO, docks and harbours, dungeon entrances, a plot picker in Walk that
    sizes a house or multi to the plot.
12. ✅ **ShardForge Server** (first version): self-hosted, accounts and user levels, shared projects with history,
    server-side build, live co-building in Walk. Next: live terrain co-editing in the editor, Ask Claude via a
    server key.
13. ✅ **Patches** (first version): publish to GitHub Releases, ShardForge Patcher for players.
    Next: one-click "publish to shard" from the server (copy files to ServUO's data folder **and** release a client
    patch together), signed manifests, a windowed patcher.
14. **Live ServUO mode**: place decorations, doors, signs and spawners as ServUO world items instead of statics,
    so they appear for players instantly without a client patch (statics stay best for terrain and bulk scenery).

## Requirements

- A legally obtained Ultima Online Classic client (no game assets are included in this repo or the download)
- To build from source: the .NET 10 SDK (`scripts/package.sh` makes the download packages)

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

`samples/populated-tokuno.shardforge.json` is an island continent filled by **Populate world**: a walled city,
a town, villages, hamlets, roads, building plots, camps, ruins and shrines (build with `--facet 4`; the build
writes `layout.json` next to the map so Walk shows the plots).

## Layout

| Path | What |
|---|---|
| `editor` | Browser world editor (single HTML file) |
| `walk` | Browser walk-around client (single HTML file) |
| `src/ShardForge.Server` | Self-hosted server for cooperative building |
| `src/ShardForge.Patcher` | Player patcher (single-file exe) |
| `docs` | Server and patcher guides |
| `src/ShardForge.Core` | File formats (UOP, map, statics, tiledata, radarcol), project build, rendering, PNG output |
| `src/ShardForge.Cli` | `shardforge` command-line tool |
| `tests/ShardForge.Tests` | Unit tests (synthetic data, no game files needed) |

## License

MIT
