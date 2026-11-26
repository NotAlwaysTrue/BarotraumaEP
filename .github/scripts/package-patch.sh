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

if [[ "$variant" == workshop ]]; then
  script_dir="$(dirname "${BASH_SOURCE[0]}")"
  files=(
    DedicatedServer.dll DedicatedServer.deps.json DedicatedServer.runtimeconfig.json
    BarotraumaCore.dll Farseer.NetStandard.dll
    Data/campaignsettings.xml Data/permissionpresets.xml
    HttpContent/index.html HttpContent/app.js HttpContent/management.js
    HttpContent/performance.js HttpContent/styles.css
  )
  case "$platform" in
    windows)
      executable=DedicatedServer.exe
      files+=(hostfxr.dll hostpolicy.dll coreclr.dll steam_api64.dll)
      ;;
    linux)
      executable=DedicatedServer
      files+=(libhostfxr.so libhostpolicy.so libcoreclr.so libsteam_api64.so liblinux_steam_env.so)
      ;;
    macos)
      executable=DedicatedServer
      files+=(libhostfxr.dylib libhostpolicy.dylib libcoreclr.dylib libsteam_api64.dylib)
      ;;
  esac
  for file in "$executable" "${files[@]}"; do
    if [[ ! -s "$publish_dir/$file" ]]; then
      echo "::error::Required Workshop server file is missing or empty: $file" >&2
      exit 1
    fi
  done
  cp "$script_dir/workshop" "$publish_dir/"
  # GitHub Actions artifact downloads reset the executable bits.
  chmod +x "$publish_dir/$executable"
  mkdir -p "$(dirname "$archive")"
  tar -czf "$archive" --owner=0 --group=0 -C "$publish_dir" -- .
  exit 0
fi

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

for file in "${files[@]}"; do
  if [[ ! -s "$publish_dir/$file" ]]; then
    echo "::error::Required patch file is missing or empty: $file" >&2
    exit 1
  fi
done

mkdir -p "$(dirname "$archive")"
tar -czf "$archive" --owner=0 --group=0 -C "$publish_dir" -- "${files[@]}"
