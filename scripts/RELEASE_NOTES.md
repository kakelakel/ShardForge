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
