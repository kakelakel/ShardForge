## ShardForge 0.8.9

- **Caves made the way the client's own are.** The 0.8.7 caves were cut into the mountains and covered with a flat
  lid, which still looked like a cutout from above. Now a cave is built like the one north of Britain: the
  mountain's land stays exactly where it was, and the cave is cave-floor statics laid underneath it at the height
  of the ground in front of the mouth, with rounded edges chosen from a table mined from Trammel and Ilshenar. You
  walk straight in from the mouth, along a passage, into the cave. Inside, the client hides the mountain over your
  head and you see the cave and darkness; from outside the mountain is whole. The land over a cave is raised where
  needed so there's always room to stand. Mouths into a dungeon map still teleport.
- **Walk hides the mountain over you too.** Standing under land at least 16 above you, Walk now hides everything
  from there up, land included, as the client does, so you can walk around inside these caves (and the client's
  own) and see them.

## ShardForge 0.8.8

- **Shores the way the client's own coasts are made.** Water land tiles next to higher ground stretch into a
  sawtooth, which is what made rivers, coasts, lakes and oases look jagged. Now, out to three tiles from any shore,
  the build lays the bed low (z -15) so the bank slopes down into it, and covers it with flat water statics at sea
  level. The tiles right along the edge get the client's foamed, shaped shore pieces, chosen by which neighbours are
  water, from a table mined from Trammel's coasts. Further out, open water is the usual water tiles. Dry ground
  next to a shore is kept just above the water. Fishing and boats work the same (the water statics are wet), and
  trees no longer grow on the underwater bank. Rebuild a map to get the new shores.

## ShardForge 0.8.7

- **Walk into the caves.** The caves and mines that World features carves inside mountains are now reached on
  foot: behind each mouth a tunnel winds down through the rock to the cave, no teleporter. (A mouth falls back to
  teleporters only when there's no way through the rock; mouths into a dungeon map still teleport, and Walk's
  Cave mouth tool is still the way to put a hidden teleporter to a dungeon on another facet.)
- **Caves are covered.** The cave lies deep, and a hidden marker over every floor tile tells the client there is a
  roof, so inside you see the cave and the dark round it, not the mountainside, snow and sky above. From outside,
  a flat lid of snow or stone at the mountain's height closes the mountain over the cave and tunnel, and the void
  that used to be left round a cave goes back to mountain, so there's no black hole on the radar.
- Caves are placed deeper inside the mountains (at least 7 tiles in), so the lid has mountain all round it.

## ShardForge 0.8.6

- Fixed the install steps in the export folder's README, the ServUO package and the docs: current ServUO compiles
  its scripts ahead of time, so after copying in `ShardForgeImport.cs` you rebuild ServUO once
  (`Compile.WIN - Release.bat` or `dotnet build -c Release`) before `[SFImport` and `[SFClear` exist.

## ShardForge 0.8.5

- **Cave mouths in Walk.** Build → Land → **Cave mouth**: click the ground in front of a mountain side that faces
  south or east, and a mouth opens into the slope, made from the client's own mouth tiles (like Destard and
  Despise). The slope round it rises to meet it in a rocky outcrop that blends into what's there, the ground in
  front is levelled and cleared, and the dark middle gets an invisible floor to stand on. A preview shows where it
  goes (green fits, red: no mountain side there); **Faces** can force south or east. It's one undo step. Put a
  Teleporter in the dark middle to lead into a dungeon.

## ShardForge 0.8.4

- **No more signposts in towns.** Towns no longer put a signpost where each main road leaves (they could end up on
  the road or in a wall), and roads no longer get a "To …" signpost at each end. Signposts at crossroads out in the
  country stay. The town signposts are still there as a tick (Town → Decorations → *Signposts where roads leave*),
  off by default.

## ShardForge 0.8.3

- **Plain names for sites.** Generated sites are called what they are: *Bandit camp*, *Ruins*, *Shrine*, *Cave*,
  *Cave entrance*, *Castle*, *Fort*, *Watchtower*, *Lighthouse*, *Water mill*, *Shipwreck*, *Oasis*, *Standing
  stones*. Towns keep their names.
- **Places list in the editor.** Every named place on the map, with a filter: type a new name and press Enter. A
  town's region, signs with the old name nearby and the moongates that lead there follow. Undo takes it back.
- Cave mouths into a dungeon map find the right area even when several share a name.
- Fixed: 0.8.2 broke town squares in Populate world and the Town tool (the statue and hitching-post lists went
  missing). Sorry about that.

## ShardForge 0.8.2

- **Town decorations when generating.** New map → generated world has a **Town decorations** choice: *Tidy* (no
  street clutter: no barrels, crates, troughs, hay or hitching posts by the roads; the default), *Bare* (only lamps
  and signposts), *Full*, or *As ticked under Town → Decorations*. **Populate world** uses the ticks under
  Town → Decorations. The choices are remembered.
- Fixed: water troughs were laid with their two halves the wrong way round, so they looked like half troughs.
- Fixed: a fast brush stroke at low zoom left a string of separate blobs (a painted river came out beaded). Strokes
  now fill in between pointer positions.

## ShardForge 0.8.1

- **Export folder.** Walk → Server items → **Put in export folder** writes the map into `export\` in the
  ShardForge folder: `client\` (map and statics files) and `ServUO\` (items, regions and the import script), ready
  to copy into the client folder and over ServUO. One map per facet: an overworld and a dungeon map sit side by
  side, and a newer export for the same facet replaces the older files. A `README.txt` lists what's in it.
- Fixed: a map built from a project now always takes its own land file along in the ServUO package (before, only
  when the land had been edited in Walk).

## ShardForge 0.8.0

- **Castles on crags.** A plateau rises sheer out of the land, grass on top and rock cliffs down its sides with
  scree round the foot. The only way up is a ledge that climbs round the cliff to the gate. On top: a curtain wall
  along the edge, towers, a gatehouse, a keep with a tall donjon, a hall and a courtyard, and a path down to the
  nearest road. Populate world places them near towns (World features → Castles on crags), or pick
  **Castle on a crag** as the Town tool's setting and click where you want one.

## ShardForge 0.7.1

- **Piers** are laid like the client's own docks: a solid plank floor and posts standing in the water off the
  edges, rising above the deck. No more gaps.
- **Cave mouths** sit in the slope: a rocky outcrop round each (also on snowy mountains), the ground rising to meet
  the mouth and fading into the mountain at the sides, and a clear way in with no trees in front.

## ShardForge 0.7.0

- **World features.** Populate world (or **Add features to this map**) now adds, each one switchable:
  - Water: piers with moored ships at harbour towns, waterfalls off high ground into rivers and lakes, fords and
    stepping stones where small roads cross streams, lighthouses on headlands, fishing huts on lake shores.
  - Roads and land: signposts at crossroads naming the towns each way (and at each road's start), watchtowers and
    forts at mountain passes, stone walls and hedges along roads near towns, scarecrows in fields, water mills.
  - Caves: mouths spread over the mountains, built from the client's own mouth tiles, leading by teleporters both
    ways into caves and mines carved inside the mountains, or into a dungeon map's areas (linked when you build);
    ruins near dungeon mouths.
  - Coasts and wilds: shipwrecks and driftwood, desert oases, standing stones on hilltops.
  - Towns that work on ServUO: guarded regions, bankers, town criers, vendors at stalls and plots, and moongates
    linking the towns in a ring.
- Projects can set land tiles directly (`objects.land`), for things like cave mouths.
- Fixed: rebuilding a project no longer drops the traps and spawners it generated.

## ShardForge 0.6.1

- **AI allowances by level.** Admin → AI assistants → *Requests per person per day, by level*: a daily number for
  Decorators, Worldbuilders and Admins, 0 to keep a level out, or no limit. Defaults: Decorator 30, Worldbuilder
  100, Admin no limit. The editor and Walk show how many requests each person has left today.

## ShardForge 0.6.0

**AI assistants: ChatGPT, Claude or a local model, in the editor and in Walk.**
- Admin → **AI assistants**: paste an OpenAI key (ChatGPT), an Anthropic key (Claude), or point at Ollama for a
  free local model, and press **Test**. Keys stay on the server (`data/ai.json`) and never reach a browser.
- **Editor**: the Ask panel now works on your server, with a picker when more than one assistant is on.
- **Walk**: a new **Ask AI** window (Build menu). "Furnish this house as a tavern", "a market square with stalls
  and lamps", "an orc camp with a spawner on the hill": the assistant sees the area round you (or your plot), the
  catalogue and the house designs, and builds with your tools. One step: Undo takes it all back; everyone sees it
  live.
- Every request is logged in Activity; a daily limit per person (default 200). See docs/AI.md.

## ShardForge 0.5.10

**Five more bridges**, eleven in all:
- **Stone arcade** (Ilshenar), copied piece for piece: posts, capped arches and buttressed piers with columns down
  into the water; along north-south rivers an arched wall on a stone base.
- **Stone with iron railings**: grey pavers, wrought-iron rails and lamps at both ends; the city bridge.
- **Gargish**: teal Ter Mur flooring, dark blue marble pillars with gold, low crystal walls and lamps.
- **Crystal**: dark crystal floor between crystal walls, for places of magic.
- **Log**: logs laid side by side, log railings, on piles.
Towns get the grand ones, the wilds the wooden ones. Piles under wooden bridges now reach the deck and stand in
the water (they stopped short before).

## ShardForge 0.5.9

**Bridges in six materials**, each built after bridges in the client's own world: **wooden** (Trinsic), **stone**
(Ilshenar) and **brick** (Luna) with walls, piers and arches beneath, **sandstone** (Trinsic, Nujel'm), **marble**
causeways (Malas: kerbs, a checkered floor with a blue line down the middle) and **rustic** plank bridges with low
rails. Big roads and towns get masonry, sandy country sandstone, the wilds wood and planks. Every style climbs on
its own stairs or ramps and is walkable. `populate_world` takes `"bridges": "stone"` (or any style) for one style
everywhere.

## ShardForge 0.5.8

**Dungeons with something in them.** The Underground tool has themes:
- **Lava**: rivers and lakes of lava with the crusted rims of the client's own fire dungeons, eruptions, embers,
  fire fields, a pentagram with fire columns at its heart; fire traps; lava lizards, fire elementals, hell hounds.
- **Acid**: festering pools and streams with slimy rims, poison clouds, bones and skulls; gas traps; acid
  elementals, slimes, boglings.
- **Ice**: snow floors, crystals, everfrost, icicles along the walls; frost trolls, ice elementals.
- **Spiders**: webs everywhere, egg sacs round a nest; giant spiders, dread spiders, black widows.
- **Undead**: bones, blood, skull piles, candles, a pentagram and an altar; spike, saw and stone face traps;
  skeletons, ghouls, liches.
Every part stays reachable (causeways of rock cross the liquid where needed). The traps are real ServUO traps and
the spawners real spawners: they come with the project when it's built and export with the other server items.
Lava and acid painted by hand get the same rims when the map is built.

## ShardForge 0.5.7

- **Beautiful bridges**: generated roads cross rivers on proper wooden bridges built like the Trinsic bridges:
  straight and symmetric along the road, a board deck well above the water, ramps up at both ends, railings all
  along both sides, piles down into the water and a beam under the front edge. The road meets them squarely,
  nothing grows under them, and two roads crossing at the same place share one bridge.
- **Walk** no longer draws the client's invisible "no draw" tiles.

## ShardForge 0.5.6

Generated worlds, round two:
- **Deltas**: big rivers fan out into several channels over low land built out into the sea, with sandbars.
- **Lakes drain**: every lake spills over into a river that runs on to the sea; hollows inland fill up into
  lakes instead of becoming landlocked seas.
- **Rivers bend** smoothly (no more ruler-straight stretches across flat land) and always reach the coast.
- **Roads** between towns follow valleys and cross ranges at passes, go round lakes, and cross rivers where
  they're narrow on **wooden bridges** you can walk over (plank decks like the client's own, level with the banks).

Regenerate a world (or make a new one) to get these.

## ShardForge 0.5.5

- **Walk workspace**: the tools are windows over the world, opened from the top menu (World, Build, Houses,
  Server). Drag them by the title, resize from the corner, double-click to fold; they snap to edges and each
  other, and your layout is remembered. Layout → *Reset to the standard layout* puts them back.
- **Rivers in generated worlds** run as one channel down to the sea or a lake, bending as they go, instead of a
  trail of separate pools. Tiny "lakes" and puddles along coasts are gone. (Applies to newly generated worlds.)

## ShardForge 0.5.4

- **Decorate inside buildings**: Walk hides roofs and upper floors over your head, like the game (Movement →
  *Above you*: Auto, Hide or Show; **R** cycles). Hidden things can't be clicked, removed or built on, so new
  items land on your floor, not the roof. In Fly, **[** and **]** move a floor down or up.

## ShardForge 0.5.3

- **Manager on Windows finds updates again**: Windows PowerShell 5.1 read GitHub's release list wrong and said
  "No releases ... yet". Coming from 0.5.0–0.5.2 on Windows, update once from a console in the ShardForge folder:
  `powershell -ExecutionPolicy Bypass -File manager\ShardForge-Manage.ps1 -Action Update -Version 0.5.3`.
  After that the Update button works.
- **Stop is quick**: open Walk tabs no longer hold the server up for 30 seconds.

## ShardForge 0.5.2

- **Trees look right in Walk**: leaves are drawn over their trunks and branches, the way the client draws them
  (Walk now orders things on a tile the client's way, which also settles other "behind/in front" oddities).

Also in 0.5.x: use ShardForge from another PC on your network (Admin → Server → *Who can connect*), and no
more holes in Walk's ground and buildings. Updating: press **Update** in the Manager.

## ShardForge 0.5.1

- **Use it from another PC**: Admin → Server → *Who can connect* → *Other computers on my network too*, then
  Restart and **Firewall** in the Manager. Admin lists the addresses to open on the other PC.
- **No more holes in Walk**: ground and buildings no longer go missing in patches when the browser drops some of
  the many small picture requests (it now asks a few at a time and retries).

Updating from 0.5.0: press **Update** in the Manager.

## ShardForge 0.5.0

**New: the ShardForge Manager.** `ShardForge-Manager.bat` (Windows) or `./shardforge-manager.sh` (Linux) starts and
stops ShardForge without a console window, shows its log, and updates it from these releases: the download is
checked against `SHA256SUMS.txt`, your data and settings stay, and you can roll back. From this version on, updating
is one click. (Coming from 0.4.0: download this one by hand once and copy your `data` folder and `appsettings.json` in.)

- **Generated worlds**: plates, mountain ranges, wind and rain shadows, rivers, lakes and valleys, land cover from
  climate; six shapes, four climates, any seed. Several worlds can share one map.
- **Dungeons**: an Underground start for a dungeon facet; caves, caverns, mines, crypts and lairs with rock and
  dungeon walls the way the client's own are made; teleporters between maps with the way back placed for you.
- **Houses**: 66 ready designs from huts to castles, a house designer in Walk, and a shared house library.
- **Server items**: doors, signs, working items, teleporters, friendly spawners (packs, suggestions, area fill)
  and regions, exported with a ServUO import script (`[SFImport`); see docs/SERVUO.md.
- **User levels**: Viewer, Decorator, Worldbuilder, Admin, enforced by the server; a per-person activity log.
- **Backups**: automatic every 6 hours (a week and more kept), restore everything, one map, the projects or the library.

Downloads: `ShardForge-0.5.0-win-x64.zip` (Windows) and `ShardForge-0.5.0-linux-x64.tar.gz` (Linux). No .NET needed.
