#!/usr/bin/env bash
set -euo pipefail

directory="$1"
version="$2"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$ ]]
cd -- "$directory"

expected=(
  "RelayLink-Agent-linux-x64-$version.tar.gz"
  "RelayLink-Agent-osx-arm64-$version.tar.gz"
  "RelayLink-Agent-osx-x64-$version.tar.gz"
  "RelayLink-Agent-win-x64-framework-dependent-$version.exe"
  "RelayLink-Agent-win-x64-framework-dependent-$version.zip"
  "RelayLink-Agent-win-x64-self-contained-$version.exe"
  "RelayLink-Agent-win-x64-self-contained-$version.zip"
  "RelayLink-Server-linux-x64-$version.tar.gz"
  "RelayLink-Server-win-x64-$version.zip"
)

for asset in "${expected[@]}"; do
  if [[ ! -f "$asset" || ! -s "$asset" || -L "$asset" ]]; then
    printf 'Missing or invalid release asset: %s\n' "$asset" >&2
    exit 1
  fi
done

shopt -s nullglob dotglob
for asset in *; do
  [[ "$asset" == SHA256SUMS && -f "$asset" && ! -L "$asset" ]] && continue
  match=false
  for name in "${expected[@]}"; do
    if [[ "$asset" == "$name" ]]; then match=true; break; fi
  done
  if [[ "$match" == false ]]; then
    printf 'Unexpected release asset: %s\n' "$asset" >&2
    exit 1
  fi
done

sha256sum -- "${expected[@]}" > SHA256SUMS
sha256sum --check SHA256SUMS
