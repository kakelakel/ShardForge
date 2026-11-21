# From ShardForge to a ServUO shard

ShardForge makes two kinds of things, and they reach ServUO in two different ways:

| What | Made in | Ends up as | Who needs it |
|---|---|---|---|
| Land, statics, houses (walls, roofs, floors), decoration | Editor (build) and Walk | Client files: `map#`, `staidx#`, `statics#` (`.mul` or `.uop`) | ServUO **and every player** (same files) |
| Doors, signs, working items (forge, anvil, moongate…), teleporters, spawners | Walk → Server items | Live objects created by `[SFImport` | ServUO only |
| Regions (towns, guarded or not) | Walk → Server items (or "Regions for the towns") | Regions registered at every start | ServUO only |

Statics never open, talk or spawn: anything that has to *do* something is a server item. Houses placed from
ShardForge designs, the library or the designer bring their doors along as server items.

## The flow

1. **Build the world** in the editor: terrain, towns, roads, plots. On the ShardForge server press **Build**: it
   becomes a map everyone can open in Walk. Pick the facet number you'll replace on the shard (0 Felucca,
   1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 Ter Mur).
2. **Decorate in Walk**, together: houses, statics, land touch-ups, then server items (doors come with houses;
   add signs, spawners, teleporters and regions). Everything is saved on the ShardForge server as you go.
3. **Download the ServUO package**: Walk → Server items → **ServUO package (.zip)**. It holds
   - `client/` the finished map and statics files,
   - `ServUO/Data/ShardForge/<map>.sfitems` every server item and region,
   - `ServUO/Scripts/Custom/ShardForge/ShardForgeImport.cs` the import script,
   - `README.txt` these steps in short.
   On the server there's a shortcut: **Put in export folder** (next to the package button, admins) writes the
   same things into `export\` in the ShardForge folder: `client\` and `ServUO\`, ready to copy, plus a
   `README.txt` listing what's there. Each map takes its facet's slot, so a dungeon map on another facet sits
   beside the overworld, and exporting a newer map for the same facet replaces the older one's files. The
   folder can be moved with `ShardForge:ExportPath` in `appsettings.json`.
4. **Install on the shard** (first time):
   1. Stop ServUO and back up its `Saves/` folder and the client files you're about to replace.
   2. Copy `client/*` into the client folder ServUO reads (the folder in `Config/DataPath.cfg`, or wherever
      ServUO finds the client on your machine).
   3. If the map isn't the facet's standard size, set its width and height for that facet in
      `Scripts/Misc/MapDefinitions.cs` (`RegisterMap(...)`). ShardForge's build warns when the size differs.
   4. Copy the package's `ServUO/` folder over your ServUO folder (it only adds `Data/ShardForge/` and
      `Scripts/Custom/ShardForge/`).
   5. Rebuild ServUO so the script is in: run `Compile.WIN - Release.bat` in the ServUO folder (or
      `dotnet build -c Release`). Current ServUO compiles its scripts ahead of time, so a new script does nothing
      until you rebuild. Then start ServUO: the console says how many regions ShardForge registered.
   6. Log in as an Administrator and type **`[SFImport`**. It creates every door, sign, item, teleporter and
      spawner, and tells you how many.
5. **Give players the same client files**: the ShardForge patcher sends `client/*` to their clients. A player
   with old map files sees walls where there are none (and the other way round).

## Dungeons on a facet of their own

Make a **New map** in the editor and start it as **Underground**: a black void. With the **Underground** tool (U)
drag rectangles to carve caves, great caverns, mine tunnels, crypts (rooms and corridors) and lairs; paint
**Cave floor**, **Dungeon floor** or **Void** to reshape them. The build raises rock walls around cave floor and
stone walls around dungeon floor, the way the client's own dungeons are made. Build it on a facet slot you don't
use for anything else (Ilshenar, Malas, Tokuno or Ter Mur), and set that facet's size in `MapDefinitions.cs`.

In Walk, put a **Teleporter** at the cave mouth on the overworld, choose the dungeon map under **Leads to** and one
of its ways in, and click: it places the teleporter there and, with **Two-way** on, the one back in the dungeon.
Both maps' packages go to the shard the same way (copy both `.sfitems`, then one `[SFImport`); players walk into a
cave mouth and are in the dungeon, and never see that it's another facet.

## Changing things later

Keep working in Walk, download a new package, then:

| You changed | On the shard | Players |
|---|---|---|
| Only server items or regions | Copy the new `.sfitems` into `Data/ShardForge/`, type `[SFImport` (no restart) | Nothing |
| Statics, houses or land | Stop ServUO, copy `client/*`, start, `[SFImport` | Patch their client |

`[SFImport` first deletes what the previous import made (it keeps a list in `Data/ShardForge/imported.txt`)
and then creates everything again, so it's safe to run as often as you like. It never touches items it didn't
create: houses players own, things GMs placed by hand and loot stay. `[SFClear` removes the import without
creating anything.

## Good to know

- One `.sfitems` file per map; the script loads every file in `Data/ShardForge/`. Its `map` line says which
  facet it's for: custom facet numbers default to Felucca, so edit that line if needed.
- Regions are registered at every start and by `[SFImport`; they aren't saved with the world, so the file is
  the truth.
- Spawners carry a count per creature (`Orc:4,OrcishMage:1`). New ServUO spawners get one spawn entry per
  creature with its count; older ones (a plain list of names) get each name as often as its share, so the mix
  stays about right.
- Doors are ServUO classes (`DarkWoodDoor`, `StrongWoodDoor`, `IronGate`…), items are any ServUO class by name
  (`Forge`, `Anvil`, `PublicMoongate`…), spawners take ServUO creature class names (`Orc, OrcishMage`). A name
  ServUO doesn't know is skipped and reported in the console.
- Replacing a stock facet on an existing shard: its stock decoration (`[Decorate`) and spawns still stand on top
  of your new map. On a new shard just don't run them for that facet.
- The import script was written against ServUO's public API and handles both old and new spawner styles, but it
  hasn't yet been run on a live ServUO by us: tell us what the console says the first time.


### Cave mouths from Populate world

**World features** puts cave mouths in the mountains (the client's own mouth tiles, facing south or east). Each
leads by teleporters, both ways, into a cave or mine carved inside a mountain on the same map, or, if you pick a
dungeon map under **Cave mouths also lead into** (on a server, once that map is built), into one of its areas:
half the mouths go there, one area each. Building the overworld points them at the areas and places the
teleporters back out on the dungeon map (ids start with `link-`); rebuilding replaces them. If an area can't be
found (the dungeon map was rebuilt with other names), the build says so and leaves that mouth unlinked.

## Generated towns, traps and spawners

Populated towns come with what a ServUO town needs: a region with the town's name (guarded, except hamlets), a
banker and a town crier on the square, a vendor at each market stall, and one in each plot by what it's for
(innkeeper and barkeeper at the inn, smiths at the smithy, a healer at the chapel, a fisherman and shipwright at the
docks…). Towns and cities get a moongate on the square; together they make a ring round the map, each gate named
for where it goes (a teleporter shown as a moongate). Missing classes are skipped with a note in the console.



The editor's underground themes (lava, acid, ice, spiders, undead) place real ServUO traps where the passages
narrow (`FireColumnTrap`, `FlameSpurtTrap`, `GasTrap`, `MushroomTrap`, `SpikeTrap`, `SawTrap`, `StoneFaceTrap`)
and spawners with fitting monsters in the far chambers. They travel with the project: building it puts them on
the map as server items (their ids start with `gen-`), replacing the previous build's; anything you placed by
hand in Walk stays. Export and `[SFImport` them like any other server items.
