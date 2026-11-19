# ShardForge Manager

Start, stop and update ShardForge without a console window, from GitHub releases. It comes with every download:

- **Windows**: `ShardForge-Manager.bat` opens the Manager window (Windows PowerShell 5.1, built into Windows).
- **Linux**: `./shardforge-manager.sh status | start | stop | restart | check | update [vX.Y.Z] | rollback | logs`.
- **Any console**: `manager/ShardForge-Manage.ps1 -Action Status | Start | Stop | Restart | Check | Update | Rollback | Logs | Open`
  (Windows PowerShell or PowerShell 7). The window runs exactly these actions, one at a time.

## What it does

- **Start** runs ShardForge in the background (no window, no browser popping up) and waits until it answers.
  The server then writes its log to `data/logs/server-YYYY-MM-DD.log` (kept two weeks); **Live log** shows it.
- **Stop** asks the server to stop cleanly. Only the Manager can: it sends the secret token the server writes to
  `data/control/server.json` at start, and the server only accepts it from this PC. If it doesn't stop within 30
  seconds, `-Force` (or `FORCE=1`) ends the process; the database is safe either way.
- **Check** compares your version (the `VERSION` file) with the newest release on GitHub. The window checks
  when it opens.
- **Update** (to the newest, or `-Version v0.6.0` for a given one):
  1. downloads the package for your system and checks it against the release's `SHA256SUMS.txt`; a mismatch is
     refused, and a release without checksums only installs with `-Force`;
  2. unpacks it beside the install and checks it holds the program;
  3. stops ShardForge if it's running;
  4. copies the database and settings to `updates/before-<version>-<time>/`;
  5. keeps the current program in `updates/previous/`, puts the new one in place (undone if anything fails);
  6. starts ShardForge again if it was running.

  Only program files are replaced: `data/` (accounts, projects, maps, edits, backups, logs), your
  `appsettings.json` and the Manager's `manager/manager.json` stay as they are. The new default settings arrive as
  `appsettings.default.json` for reference.
- **Rollback** goes back to the version before the last update (and again goes forward). Data isn't changed.

## Settings

`manager/manager.json`: `repository` (default `kakelakel/ShardForge`), `includePrereleases` (the window's
checkbox) and `apiBase` (the GitHub API).
