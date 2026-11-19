#!/usr/bin/env bash
# ShardForge Manager for Linux: start, stop and update ShardForge from GitHub releases.
#
#   ./shardforge-manager.sh status | start | stop | restart | check | update [vX.Y.Z] | rollback | logs [lines]
#
# Updates replace only the program files: the data folder (accounts, projects, maps, edits, backups, logs) and
# your appsettings.json are never touched. The database is copied aside before every update, the download is
# checked against the release's SHA256SUMS.txt, and the replaced version is kept for rollback.
# Needs curl, tar and sha256sum. (On Windows, use ShardForge-Manager.bat.)
set -euo pipefail
# The whole script is one block, read before anything runs: an update replaces this file while it runs.
{
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"
exe="$root/ShardForge"
data="$root/data"
control="$data/control/server.json"
updates="$root/updates"
previous="$updates/previous"
cfg="$root/manager/manager.json"
programs=(ShardForge shardforge-cli VERSION QUICKSTART.txt LICENSE.txt samples manager shardforge-manager.sh ShardForge-Manager.bat appsettings.default.json)

say() { printf '%s\n' "$*"; }
die() { printf 'Error: %s\n' "$*" >&2; exit 1; }
json() { sed -n "s/.*\"$1\" *: *\"\{0,1\}\([^\",}]*\).*/\1/p" "${2:-/dev/stdin}" | head -1; }
setting() { [ -f "$cfg" ] && json "$1" "$cfg" || true; }
repo="$(setting repository)"; repo="${repo:-kakelakel/ShardForge}"
api="$(setting apiBase)"; api="${api:-https://api.github.com}"
pre="$(setting includePrereleases)"
installed() { [ -f "$root/VERSION" ] && tr -d '[:space:]' < "$root/VERSION" || echo unknown; }
vnum() { echo "${1#v}" | sed 's/-.*//' | awk -F. '{ printf "%05d%05d%05d", $1, $2, $3 }'; }

# The running server: the process in the control file, if alive and this folder's program.
server_pid() {
  [ -f "$control" ] || return 1
  local pid exe_in
  pid="$(json pid "$control")"; exe_in="$(json exe "$control")"
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null || return 1
  [ -z "$exe_in" ] || [ "$(dirname "$exe_in")" = "$root" ] || return 1
  echo "$pid"
}

start() {
  if server_pid >/dev/null; then say "ShardForge is already running."; return; fi
  [ -x "$exe" ] || die "No ShardForge program in $root."
  nohup "$exe" --ShardForge:OpenBrowser=false --ShardForge:Detached=true </dev/null >/dev/null 2>&1 9>&- &  # (9: the lock stays with the Manager, not the server)
  local child=$! i
  for i in $(seq 1 110); do
    sleep 0.4
    kill -0 "$child" 2>/dev/null || die "ShardForge stopped right away. See: $0 logs"
    if [ "$(server_pid || true)" = "$child" ]; then say "ShardForge $(json version "$control") is running: $(json url "$control") (process $child)."; return; fi
  done
  die "ShardForge started but did not report in within 45 seconds. See: $0 logs"
}

stop() {
  local pid url token i
  pid="$(server_pid || true)"
  if [ -z "$pid" ]; then say "ShardForge is not running."; return; fi
  url="$(json url "$control")"; token="$(json token "$control")"
  curl -fsS -X POST -H "X-ShardForge: 1" -H "X-ShardForge-Control: $token" "${url%/}/api/control/stop" >/dev/null || say "The server did not answer the stop request."
  for i in $(seq 1 60); do kill -0 "$pid" 2>/dev/null || { say "ShardForge stopped."; return; }; sleep 0.5; done
  if [ "${FORCE:-}" = 1 ]; then kill -9 "$pid"; say "ShardForge was ended (forced)."; else die "ShardForge did not stop within 30 seconds. Run with FORCE=1 to end it (the database is safe either way)."; fi
}

# Newest release by version, skipping drafts (and pre-releases unless configured). Needs python3 for the JSON.
release_pick=$(cat <<'PY'
import json, sys, re
pre = sys.argv[1].lower() == "true"
rs = [r for r in json.load(sys.stdin) if not r.get("draft") and (pre or not r.get("prerelease"))]
key = lambda r: [int(x) for x in re.findall(r"\d+", r["tag_name"].split("-")[0])[:3]]
if not rs: sys.exit("No releases yet.")
print(json.dumps(max(rs, key=key)))
PY
)
release() {
  local tag="${1:-}"
  if [ -n "$tag" ]; then curl -fsSL -H "User-Agent: ShardForge-Manager" "$api/repos/$repo/releases/tags/v${tag#v}"; return; fi
  curl -fsSL -H "User-Agent: ShardForge-Manager" "$api/repos/$repo/releases?per_page=30" | python3 -c "$release_pick" "${pre:-false}"
}
asset_url() { python3 -c 'import json,sys; r=json.load(sys.stdin); n=sys.argv[1]; print(next((a["browser_download_url"] for a in r.get("assets",[]) if (a["name"]==n or (n.startswith("*") and a["name"].endswith(n[1:])))),""))' "$1"; }

copy_programs() { # from to
  mkdir -p "$2"
  local n; for n in "${programs[@]}"; do [ -e "$1/$n" ] && cp -a "$1/$n" "$2/" || true; done
}
remove_programs() { local n; for n in "${programs[@]}"; do [ "$n" = manager ] && continue; rm -rf "${1:?}/$n"; done; }

update() {
  local want="${1:-}" rel tag url sums file stage new was=0 cur stamp snap
  rel="$(release "$want")"
  tag="$(json tag_name <<<"$rel")"
  cur="$(installed)"
  if [ -z "$want" ] && [ "$(vnum "$tag")" \< "$(vnum "$cur")" -o "$(vnum "$tag")" = "$(vnum "$cur")" ] && [ "${FORCE:-}" != 1 ]; then say "You have the newest version ($cur; newest on GitHub: $tag)."; return; fi
  url="$(asset_url '*-linux-x64.tar.gz' <<<"$rel")"; [ -n "$url" ] || die "Release $tag has no Linux package."
  sums="$(asset_url SHA256SUMS.txt <<<"$rel")"
  mkdir -p "$updates/downloads"; file="$updates/downloads/$(basename "$url")"
  say "Downloading $(basename "$url") from $tag..."
  curl -fsSL -o "$file" -H "User-Agent: ShardForge-Manager" "$url"
  if [ -n "$sums" ]; then
    curl -fsSL -H "User-Agent: ShardForge-Manager" "$sums" | grep " \*\?$(basename "$url")\$" > "$updates/downloads/sum.txt" || die "SHA256SUMS.txt doesn't list $(basename "$url")."
    (cd "$updates/downloads" && sed 's/ \*/  /' sum.txt | sha256sum -c --quiet -) || { rm -f "$file"; die "The download doesn't match its checksum: not installing it."; }
    say "Checksum verified."
  elif [ "${FORCE:-}" != 1 ]; then die "Release $tag has no SHA256SUMS.txt. Run with FORCE=1 to install it unverified."; fi
  stage="$updates/staging"; rm -rf "$stage"; mkdir -p "$stage"; tar -xzf "$file" -C "$stage"
  new="$(find "$stage" -mindepth 1 -maxdepth 1 -type d | head -1)"
  [ -x "$new/ShardForge" ] || die "The download has no ShardForge program in it."
  [ -f "$new/VERSION" ] || echo "${tag#v}" > "$new/VERSION"
  [ -f "$new/appsettings.json" ] && mv "$new/appsettings.json" "$new/appsettings.default.json"
  server_pid >/dev/null && { was=1; stop; }
  stamp="$(date +%Y%m%d-%H%M%S)"; snap="$updates/before-$cur-$stamp"; mkdir -p "$snap"
  for f in shardforge.db shardforge.db-wal shardforge.db-shm settings.json; do [ -f "$data/$f" ] && cp -a "$data/$f" "$snap/"; done
  [ -f "$root/appsettings.json" ] && cp -a "$root/appsettings.json" "$snap/"
  say "Database and settings copied to updates/$(basename "$snap")."
  rm -rf "$previous"; copy_programs "$root" "$previous"; [ -f "$previous/VERSION" ] || echo "$cur" > "$previous/VERSION"
  local keepcfg=""; [ -f "$cfg" ] && keepcfg="$(cat "$cfg")"   # the Manager's own settings stay yours
  if ! { remove_programs "$root" && copy_programs "$new" "$root"; }; then
    say "Installing failed, putting the previous version back."; remove_programs "$root"; copy_programs "$previous" "$root"; exit 1
  fi
  [ -n "$keepcfg" ] && printf '%s\n' "$keepcfg" > "$cfg"
  [ -f "$root/appsettings.json" ] || cp "$root/appsettings.default.json" "$root/appsettings.json"
  chmod +x "$root/ShardForge" "$root/shardforge-cli" "$root/shardforge-manager.sh" 2>/dev/null || true
  rm -rf "$stage"
  say "Updated: $cur -> $(installed). The previous version is kept for rollback."
  [ "$was" = 1 ] && start || true
}

rollback() {
  [ -x "$previous/ShardForge" ] || die "There is no previous version to go back to."
  local cur was=0 swap="$updates/rollback-swap"
  cur="$(installed)"
  server_pid >/dev/null && { was=1; stop; }
  local keepcfg=""; [ -f "$cfg" ] && keepcfg="$(cat "$cfg")"
  rm -rf "$swap"; copy_programs "$root" "$swap"
  remove_programs "$root"; copy_programs "$previous" "$root"
  [ -n "$keepcfg" ] && printf '%s\n' "$keepcfg" > "$cfg"
  rm -rf "$previous"; mv "$swap" "$previous"
  say "Rolled back: $cur -> $(installed). Your data was not changed."
  [ "$was" = 1 ] && start || true
}

status() {
  local pid; pid="$(server_pid || true)"
  if [ -n "$pid" ]; then say "ShardForge $(json version "$control") is running at $(json url "$control") (process $pid)."; else say "ShardForge is not running."; fi
  say "Installed: $(installed) in $root$( [ -f "$previous/VERSION" ] && printf '; previous for rollback: %s' "$(tr -d '[:space:]' < "$previous/VERSION")")"
}

check() {
  local tag; tag="$(release | json tag_name)" || die "Couldn't reach GitHub."
  if [ "$(vnum "$tag")" \> "$(vnum "$(installed)")" ]; then say "A new version is out: $tag (you have $(installed)). Run: $0 update"; else say "You have the newest version ($(installed))."; fi
}

# One action at a time per install.
exec 9>"$root/.manager.lock"
case "${1:-status}" in
  status) status ;;
  start) flock -n 9 || die "Another Manager action is running."; start ;;
  stop) flock -n 9 || die "Another Manager action is running."; stop ;;
  restart) flock -n 9 || die "Another Manager action is running."; stop; start ;;
  check) check ;;
  update) flock -n 9 || die "Another Manager action is running."; update "${2:-}" ;;
  rollback) flock -n 9 || die "Another Manager action is running."; rollback ;;
  logs) f="$(ls -t "$data"/logs/server-*.log 2>/dev/null | head -1)"; [ -n "$f" ] && tail -n "${2:-200}" "$f" || say "No log yet." ;;
  *) say "Usage: $0 status | start | stop | restart | check | update [vX.Y.Z] | rollback | logs [lines]"; exit 2 ;;
esac
exit
}
