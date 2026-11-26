#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 4 || $# -gt 5 ]]; then
  echo "Usage: $0 <publish-directory> <windows|linux|macos> <Client|Server> <archive> [standard|workshop]" >&2
  exit 1
fi

publish_dir="$1"
platform="$2"
component="$3"
archive="$4"
variant="${5:-standard}"

if [[ "$variant" == workshop && "$component" != Server ]]; then
  echo "::error::Workshop packages are only supported for Server." >&2
  exit 1
fi

case "$variant" in
  standard|workshop) ;;
  *) echo "::error::Unsupported package variant: $variant" >&2; exit 1 ;;
esac

case "$platform" in
  windows) monogame_platform=Windows ;;
  linux) monogame_platform=Linux ;;
  macos) monogame_platform=MacOS ;;
  *) echo "::error::Unsupported package platform: $platform" >&2; exit 1 ;;
esac

# Overlay the existing game installation, following LuaCsForBarotrauma's patch
# packaging approach. Keep modified libraries and symbols, not the stock runtime,
# launchers, configuration, game content, or unrelated dependencies.
files=(
  BarotraumaCore.dll BarotraumaCore.pdb
  Farseer.NetStandard.dll Farseer.NetStandard.pdb
  DedicatedServer.dll DedicatedServer.deps.json DedicatedServer.pdb
  HttpContent/index.html HttpContent/app.js HttpContent/performance.js HttpContent/styles.css
)

case "$component" in
  Client)
    files+=(
      Barotrauma.dll Barotrauma.deps.json Barotrauma.pdb
      "MonoGame.Framework.$monogame_platform.NetStandard.dll"
      "MonoGame.Framework.$monogame_platform.NetStandard.pdb"
    )
    ;;
  Server) ;;
  *) echo "::error::Unsupported package component: $component" >&2; exit 1 ;;
esac

# Both Linux entry points call setLinuxEnv() through this native library.
if [[ "$platform" == linux ]]; then
  files+=(liblinux_steam_env.so)
fi

if [[ "$variant" == workshop ]]; then
  marker="$(dirname "${BASH_SOURCE[0]}")/workshop"
  if [[ ! -s "$marker" ]]; then
    echo "::error::Workshop marker is missing or empty: $marker" >&2
    exit 1
  fi
  cp "$marker" "$publish_dir/workshop"
  files+=(workshop)
fi

for file in "${files[@]}"; do
  if [[ ! -s "$publish_dir/$file" ]]; then
    echo "::error::Required patch file is missing or empty: $file" >&2
    exit 1
  fi
done

mkdir -p "$(dirname "$archive")"
tar -czf "$archive" --owner=0 --group=0 -C "$publish_dir" -- "${files[@]}"
