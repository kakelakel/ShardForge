#!/usr/bin/env bash
# Builds the ShardForge download: one folder per platform with the server (editor and Walk built in),
# the command-line tool, the samples and a quick-start guide, zipped for Windows and tarred for Linux.
#
#   scripts/package.sh                 # win-x64 and linux-x64 into dist/
#   scripts/package.sh win-x64         # just Windows
#
# Needs the .NET 10 SDK, zip and tar. Self-contained: players and builders don't need .NET installed.
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(win-x64 linux-x64)
mkdir -p dist

for rid in "${rids[@]}"; do
  name="ShardForge-$version-$rid"
  out="dist/$name"
  tmp="dist/.build-$rid"
  rm -rf "$out" "$tmp"
  echo "== $name"
  common=(-c Release -r "$rid" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
          -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false --nologo -v quiet)
  dotnet publish src/ShardForge.Server "${common[@]}" -o "$tmp/server"
  dotnet publish src/ShardForge.Cli "${common[@]}" -o "$tmp/cli"

  ext=""; [[ "$rid" == win-* ]] && ext=".exe"
  mkdir -p "$out/samples"
  cp "$tmp/server/shardforge-server$ext" "$out/ShardForge$ext"
  cp "$tmp/cli/shardforge$ext" "$out/shardforge-cli$ext"
  cp src/ShardForge.Server/appsettings.json "$out/appsettings.json"
  cp samples/*.shardforge.json "$out/samples/"
  cp LICENSE "$out/LICENSE.txt"
  echo "$version" > "$out/VERSION"
  # The Manager: start, stop and update from GitHub releases (a window on Windows, a script on Linux).
  mkdir -p "$out/manager"
  cp manager/ShardForge-Manage.ps1 manager/Manager-Action.ps1 manager/ShardForge-Manager.ps1 manager/manager.json "$out/manager/"
  if [[ "$rid" == win-* ]]; then cp manager/ShardForge-Manager.bat "$out/ShardForge-Manager.bat"
  else cp manager/shardforge-manager.sh "$out/shardforge-manager.sh"; chmod +x "$out/shardforge-manager.sh"; fi
  sed "s/{{VERSION}}/$version/g; s/{{EXE}}/$ext/g" scripts/QUICKSTART.txt > "$out/QUICKSTART.txt"
  [[ "$rid" == win-* ]] && sed -i 's/$/\r/' "$out/QUICKSTART.txt" "$out/LICENSE.txt" "$out/manager/"*.ps1
  rm -rf "$tmp"

  if [[ "$rid" == win-* ]]; then
    (cd dist && rm -f "$name.zip" && zip -qr9 "$name.zip" "$name")
    echo "   dist/$name.zip ($(du -h "dist/$name.zip" | cut -f1))"
  else
    chmod +x "$out/ShardForge" "$out/shardforge-cli"
    (cd dist && tar -czf "$name.tar.gz" "$name")
    echo "   dist/$name.tar.gz ($(du -h "dist/$name.tar.gz" | cut -f1))"
  fi
done

# Checksums for every package, so the Manager can verify what it downloads.
(cd dist && sha256sum ShardForge-"$version"-*.zip ShardForge-"$version"-*.tar.gz 2>/dev/null > SHA256SUMS.txt || true)
echo "   dist/SHA256SUMS.txt"
