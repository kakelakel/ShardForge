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

> Status: early. Nothing usable yet.

## Why

[CentrED#](https://github.com/kaczy93/centredsharp) is great for hand-editing an existing map, but
building a whole new facet tile by tile is impractical. Older paint-to-map tools (Dragon, UO Landscaper)
haven't kept up with modern clients. This project tries to fill that gap.

## Roadmap

1. **Reader + renderer**: load `map#` / `statics#` / `staidx#` (MUL and UOP) plus `tiledata` and `radarcol`,
   and render a facet to PNG. Verify against real client files.
2. **Round-trip**: write files back byte-identical so we can trust the writer.
3. **Terrain converter**: color-coded terrain image + heightmap → land tiles, with correct
   transition tiles between terrain types.
4. **Static scatter**: rules-based trees, rocks, grass and so on per terrain type, written to statics.
5. **Painting front end** (maybe): a browser-based canvas for painting terrain, height and biomes.

## Requirements

- .NET 9 SDK
- A legally obtained Ultima Online Classic client (no game assets are included in this repo)

## License

MIT
