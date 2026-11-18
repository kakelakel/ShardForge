# ShardForge Server

Host ShardForge from your own PC so your team can build together in the browser: accounts and user levels,
shared projects with history, and live co-building on maps.

## What it does

- **Accounts and user levels**
  - **Viewer**: open maps and projects, walk around, see everyone.
  - **Builder**: also edit and save projects, place and remove statics, build maps.
  - **Admin**: also manage users, delete projects and publish.
- **The editor and Walk, served to everyone.** Your client's art, tiledata and maps are streamed only to
  signed-in users, and only the parts each page needs (HTTP range requests). Nobody needs their own copy
  of the client files to take part.
- **Shared projects**: every save is a revision with author and time. If two people save the same project,
  the second gets a choice instead of silently overwriting: load the other version, or save theirs as a copy.
- **Build & walk**: the server builds a project into real map files (soft terrain edges, crags, and a layout of the project's places and building plots for Walk)
  and opens it in Walk for the whole team.
- **Live co-building in Walk**: everyone on a map sees each other with name tags, and every static placed or
  removed appears for everyone at once. Each change is saved on the server with its author; the original map
  files on disk are never modified.
- **Export / publish**: Builders download the merged `staidx#.mul` + `statics#.mul` as a zip; Admins publish
  them into `data/published/<map>/`.

## Run it

### Windows, no install

Publish a single self-contained program (needs the .NET 10 SDK on the machine that builds it):

```sh
dotnet publish src/ShardForge.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Copy the `publish` folder anywhere, edit `appsettings.json` (below), and double-click `shardforge-server.exe`.
Windows may warn that the program isn't signed.

### From source

```sh
dotnet run --project src/ShardForge.Server
```

### First run

Open <http://localhost:2595> **on the PC the server runs on** and create the admin account. (Setup is refused from
other machines.) Then add people on the **Users** page. New users get a starting password from you and can change it
on the dashboard.

## Settings (`appsettings.json`)

```json
{
  "Urls": "http://localhost:2595",
  "ShardForge": {
    "ClientPath": "C:\\Games\\Electronic Arts\\Ultima Online Classic",
    "DataPath": "data",
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
| `ClientPath` | Your UO client folder: art, tiledata, animdata and the original facets (offered as maps). |
| `DataPath` | The database, built maps and published files. **Back this folder up.** |
| `Maps` | Extra map folders to offer, such as a `shardforge build` output. `Width`/`Height` for custom sizes. |
| `LoginAttemptsPerMinute` | Sign-in attempts allowed per address per minute (default 10). |

Every setting can also be given on the command line, e.g. `--ShardForge:ClientPath="D:\UO"`.

## Letting others in

- **Same network**: set `Urls` to `http://0.0.0.0:2595`, allow port 2595 in Windows Firewall, and share
  `http://<your-pc-ip>:2595`.
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
| `PUT /maps/{id}/blocks` | Builder | Replace whole 8x8 blocks; broadcast live |
| `GET /maps/{id}/export` | Builder | Zip of merged statics |
| `POST /maps/{id}/publish` | Admin | Write merged statics to `data/published/{id}/` |
| `GET /live/{id}` (WebSocket) | Viewer | Presence, positions, edits as they happen |
| `GET/POST /projects`, `GET/PUT /projects/{id}`, `GET /projects/{id}/history` | Viewer / Builder | Projects; `PUT` needs `?baseRev=` and returns 409 on conflict |
| `POST /projects/{id}/build` | Builder | Build into `data/maps/project-{id}/` and offer it as a map |
| `DELETE /projects/{id}` | Admin | Delete a project (revisions are kept in the database) |

## Not yet

- Live co-editing of terrain in the editor (projects are shared by save/load with conflict detection today).
- Ask Claude in the server-hosted editor (it needs the Claude app; a server-side key is planned).
- One-click publish to the shard: copy the merged files into ServUO's data folder and release a client patch
  together (see [PATCHER.md](PATCHER.md) for patches today).
