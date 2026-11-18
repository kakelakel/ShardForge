# Patches and the ShardForge Patcher

A custom map has to be on **both** sides: ServUO reads it to decide where players can walk, build and spawn,
and every player's client reads it to draw the world. If they differ, players get stuck on things they can't see.
The patcher keeps every player's client in step with the shard.

## How it works

- Your shard's files live in **GitHub Releases** of a public repository (one release per update).
  Release downloads are free and unmetered for public repositories, and big files don't bloat git history.
- Each release has a `manifest.json` listing every file players need, with size and SHA-256.
  Unchanged files keep pointing at the release that first shipped them, so each update uploads only what changed.
- Files are stored gzip-compressed (map files shrink a lot).
- The patcher always reads `https://github.com/<owner>/<repo>/releases/latest/download/manifest.json`, compares it with
  the player's folder, and downloads only what differs.

Only your shard's own files belong in the patch repository. The Ultima Online client itself is EA's and stays
with each player's own installation.

## Publishing (shard staff)

Put the files players need in one folder, for example the output of `shardforge build` or a published map from
ShardForge Server:

```
my-shard-files/
  map4LegacyMUL.uop
  staidx4.mul
  statics4.mul
```

Then:

```sh
shardforge patch publish my-shard-files --repo you/YourShard-Patches --shard "Your Shard" --notes "New Tokuno" --patcher ShardForge-Patcher.exe
```

- Needs a GitHub token with write access to the repository: `--token`, `GITHUB_TOKEN`, or a signed-in GitHub CLI
  (`gh auth login`).
- The release is uploaded as a draft and only published (and marked latest) when every upload succeeded, so players
  never see a half-finished update.
- An empty repository gets a README first (releases need a commit).
- `--patcher` attaches `ShardForge-Patcher.zip`: the patcher plus a `patcher.json` already pointing at your repository.
  Players download that one zip.
- Publishing the same files again uploads nothing.

To host files somewhere else, `shardforge patch make <files> <out> --base-url https://your.host/patches/<release>`
writes the gzipped files and `manifest.json` for you to upload yourself.

Remember to put the same files in the folder ServUO reads (its data path) and restart the shard.

## Playing (players)

1. Download **ShardForge-Patcher.zip** from the shard's latest release and unzip it, ideally into the Ultima Online folder.
2. Run **ShardForge-Patcher.exe** before playing.

The patcher finds the client folder by itself (its own folder, the registry, the usual install folders) or asks once
and remembers. Originals are backed up to `ShardForge-backup` the first time they're replaced.

| Option | What it does |
|---|---|
| `--check` | Only report what would change |
| `--restore` | Put back the original files and remove added ones |
| `--client <folder>` | Use this client folder (remembered) |
| `--yes` | Don't wait for Enter at the end |
| `--no-launch` | Don't start the game afterwards |

`patcher.json` next to the patcher:

```json
{
  "shard": "Your Shard",
  "manifestUrl": "https://github.com/you/YourShard-Patches/releases/latest/download/manifest.json",
  "clientPath": "",
  "launch": "ClassicUO.exe"
}
```

`launch` (optional) starts the game after a successful update, relative to the client folder.

## Safety

- Downloads are checked against the manifest's size and SHA-256 before anything is replaced; a damaged download is
  retried and never installed.
- A patch can only write **client data files** (`.mul`, `.uop`, `.idx`, `.def`, `.enu`, `.cfg`, `.txt`) directly
  into the client folder: no programs, scripts, libraries or subfolders, even if the manifest were tampered with.
- Manifests must use HTTPS URLs.
- The program to launch comes only from the player's own `patcher.json`, never from the manifest.

## Not yet

- Signed manifests (so a compromised repository couldn't push even data files).
- A windowed version of the patcher, and self-update.
- Removing files from players' folders when a release drops them.
