# ShardForge Server

Host ShardForge from your own PC so your team can build together in the browser: accounts and user levels,
shared projects with history, and live co-building on maps.

## What it does

- **Accounts and user levels**
  - **Viewer**: open maps and projects, walk around, see everyone.
  - **Decorator**: also decorate and build in Walk: statics, houses, doors, signs, working items, spawners, and
    designs in the house library. Can remove or change only what they placed themselves.
  - **Worldbuilder**: also remove and change anything, edit the land, place regions and teleporters, and create,
    save and build projects in the editor.
  - **Admin**: also every save to disk and every export (map files, ServUO packages, project downloads, the
    activity log), publishing, deleting projects, people and settings.

  The server enforces this on every request (the pages only hide what you can't use). Existing databases are
  upgraded on start: Builders become Worldbuilders, Admins stay Admins.
- **Activity log**: every change (statics added and removed with their exact tiles, land, server items, library
  designs, projects), every sign-in and failed sign-in, every refused attempt and every export, per person, with
  time, place and address. Admins filter it by person, kind, map or project, dates and text on the Admin page,
  open any spot straight in Walk, and download it as CSV. The app never deletes from it.
- **Backups**: automatic every 6 hours, kept as the newest 8, one a day for a week and one a week for a month
  (manual and before-restore backups for 30 days). Each holds the database (projects with every revision, Walk
  edits with history, server items, the library, people, the log), the settings and the built maps. Admins back
  up now, download, or restore everything, one map, the projects or the library; people and the activity log
  always stay as they are, a backup of the moment before is made first, and Walk reloads for everyone. Keep them
  on another drive with `"Backups": { "Path": "D:/ShardForgeBackups" }` in appsettings.json (also `EveryHours`,
  `KeepRecent`, `KeepDaily`, `KeepWeekly`, `KeepManualDays`, `IncludeMaps`).
- **The editor and Walk, served to everyone.** Your client's art, tiledata and maps are streamed only to
  signed-in users, and only the parts each page needs (HTTP range requests). Nobody needs their own copy
  of the client files to take part.
- **Shared projects**: every save is a revision with author and time. If two people save the same project,
  the second gets a choice instead of silently overwriting: load the other version, or save theirs as a copy.
- **Rebuilding keeps Walk's work**: statics placed or removed and land edited in Walk are reapplied on top of
  each new build of the project (what was added and removed, and the cells that were changed), so you can go back
  to the editor, change the land and rebuild without losing your decorating.
- **Build & walk**: the server builds a project into real map files (soft terrain edges, crags, and a layout of the project's places and building plots for Walk)
  and opens it in Walk for the whole team.
- **Live co-building in Walk**: everyone on a map sees each other with name tags, and every static placed or
  removed appears for everyone at once. Each change is saved on the server with its author; the original map
  files on disk are never modified.
- **Export / publish** (Admins only): download the merged map files or a ServUO package as a zip, or publish
  them into `data/published/<map>/`.

## Run it

### Download (Windows or Linux)

Get **ShardForge-<version>-win-x64.zip** (or `-linux-x64.tar.gz`) from the
[Releases page](https://github.com/kakelakel/ShardForge/releases), unzip it anywhere and start **ShardForge.exe**.
The browser opens <http://localhost:2595>. .NET is included; nothing to install. Windows may warn that the program
isn't signed. The download also holds `shardforge-cli.exe`, the samples and QUICKSTART.txt.

To build the same packages yourself: `scripts/package.sh` (needs the .NET 10 SDK, zip and tar). Pushing a version
tag (`git tag v0.4.0 && git push origin v0.4.0`) makes GitHub build them and publish a release.

### From source

```sh
dotnet run --project src/ShardForge.Server
```

### First run

On **the PC the server runs on** (setup is refused from other machines), create the admin account and choose your
UO client folder. ShardForge suggests the folders it finds (EA's install locations, the registry, the folder the
program is in) and checks that the art and tiledata are there; it only ever reads the client. You can skip it and
choose later on the **Admin** page, which also manages people: new users get a starting password from you and can
change it on the dashboard.

## Settings (`appsettings.json`)

```json
{
  "Urls": "http://localhost:2595",
  "ShardForge": {
    "ClientPath": "",
    "DataPath": "data",
    "OpenBrowser": true,
    "Maps": [
      { "Id": "test-isles", "Name": "ShardForge Test Isles", "Facet": 4,
        "Path": "C:\\Games\\Electronic Arts\\Ultima Online Classic\\ShardForge-test" }
    ]
  }
}
```

| Setting | Meaning |
|---|---|
| `Urls` | Where the server listens. `http://localhost:2595` is this PC only. Use `http://0.0.0.0:2595` to accept your network. |
| `ClientPath` | Your UO client folder: art, tiledata, animdata and the original facets (offered as maps). Usually chosen in the browser instead (saved in `data/settings.json`, which wins over this); empty means "find it". |
| `OpenBrowser` | Open the dashboard in the default browser when the server starts (Windows). |
| `DataPath` | The database, built maps and published files. **Back this folder up.** |
| `Maps` | Extra map folders to offer, such as a `shardforge build` output. `Width`/`Height` for custom sizes. |
| `LoginAttemptsPerMinute` | Sign-in attempts allowed per address per minute (default 10). |

Every setting can also be given on the command line, e.g. `--ShardForge:ClientPath="D:\UO"`.

## Letting others in

- **Same network** (a browser on another PC in your house or office): in **Admin → Server → Who can connect**
  pick *Other computers on my network too*, then **Restart** in the Manager. Admin then lists the addresses to
  open on the other PC, like `http://192.168.1.20:2595`. Windows asks once whether to let ShardForge through the
  firewall: allow it on private networks, or press **Firewall** in the Manager. The choice is saved in
  `data/settings.json` (`"listen": "network"`) and logged in Activity. Everyone still signs in; creating the first
  admin only works on the server PC itself.
- **Over the internet**: put the server behind HTTPS. Passwords and session cookies must not cross the internet
  unencrypted. The simplest way is a reverse proxy such as [Caddy](https://caddyserver.com) (`caddy reverse-proxy
  --from forge.example.com --to localhost:2595`), which gets a certificate automatically. Keep `Urls` on localhost
  so only the proxy can reach the server directly.

## Security

- Passwords: PBKDF2-SHA256 (210,000 iterations) with per-user salt.
- Sessions: HttpOnly, SameSite=Strict cookies; every request re-checks the account, so disabling someone or changing
  their role or password signs them out everywhere at once.
- Changes require a custom `X-ShardForge` header (a CSRF guard browsers won't send cross-site); sign-in and setup
  are rate limited; pages send a strict Content-Security-Policy.
- The last admin can't be demoted, disabled or deleted. Deleted accounts are disabled and renamed, so history keeps
  its authors.
- Only signed-in users can read client files, and only an allow-list of them (art, tiledata, animdata, hues,
  radarcol and the map files).

## API

All under `/api`; changes need `X-ShardForge: 1`.

| Method & path | Who | What |
|---|---|---|
| `GET /status` | anyone | Server name, version, whether setup is needed |
| `POST /setup` | local, once | Create the first admin |
| `POST /auth/login`, `POST /auth/logout`, `GET /me`, `POST /me/password` | | Sessions and own account |
| `GET/POST /users`, `PATCH/DELETE /users/{id}` | Admin | Manage people |
| `GET /data`, `GET /data/{file}` | Viewer | Client files (range requests) |
| `GET /maps`, `GET /maps/{id}/files/{file}` | Viewer | Maps and their files |
| `GET /maps/{id}/blocks` | Viewer | Shared statics edits (base64, 7 bytes per static) |
| `PUT /maps/{id}/blocks` | Decorator | Replace whole 8x8 blocks; broadcast live. Decorators: only additions and their own statics; land needs Worldbuilder |
| `GET /maps/{id}/mine` | Decorator | The statics you placed on a map |
| `GET /maps/{id}/export` | Admin | Zip of merged map files |
| `POST /maps/{id}/publish` | Admin | Write merged statics to `data/published/{id}/` |
| `GET /live/{id}` (WebSocket) | Viewer | Presence, positions, edits as they happen |
| `GET/POST /projects`, `GET/PUT /projects/{id}`, `GET /projects/{id}/history` | Viewer / Worldbuilder | Projects; `PUT` needs `?baseRev=` and returns 409 on conflict |
| `POST /projects/{id}/build` | Worldbuilder | Build into `data/maps/project-{id}/` and offer it as a map |
| `DELETE /projects/{id}` | Admin | Delete a project (revisions are kept in the database) |
| `GET /maps/{id}/items` | Viewer | Server items (doors, signs, items, teleporters, spawners, regions) |
| `PUT /maps/{id}/items` | Decorator | `{ put: [...], remove: [uid...] }`; broadcast live. Decorators: their own items only, no regions or teleporters |
| `GET /maps/{id}/servuo` | Admin | ServUO package: map files, items, import script (see SERVUO.md) |
| `GET /audit`, `GET /audit/people`, `GET /audit.csv` | Admin | Activity log: `?user=&action=&target=&since=&until=&q=&before=&limit=` |
| `GET /permissions` | Viewer | What each level may do |
| `GET/POST /backups`, `POST /backups/{name}/restore`, `GET /backups/{name}/download` | Admin | Backups; restore takes `{ scope: "all" \| "map:<id>" \| "projects" \| "library" }` |
| `GET /prefabs` | Viewer | The shared house library: `{ id, name, data, createdBy, created, updated }` |
| `POST /prefabs`, `PUT /prefabs/{id}` | Decorator (own) / Worldbuilder | Save `{ name, data }`; `data` is `{ plan }` (editable) or `{ parts: [[id, x, y, z, hue?]] }`; broadcast live |
| `DELETE /prefabs/{id}` | Decorator (own) / Worldbuilder | Remove a library entry |

## Not yet

- Live co-editing of terrain in the editor (projects are shared by save/load with conflict detection today).
- Ask Claude in the server-hosted editor (it needs the Claude app; a server-side key is planned).
- One-click publish to the shard: copy the merged files into ServUO's data folder and release a client patch
  together (see [PATCHER.md](PATCHER.md) for patches today).
