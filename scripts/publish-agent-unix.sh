#!/usr/bin/env bash
set -euo pipefail

rid="${1:-}"
case "$rid" in
  linux-x64|osx-x64|osx-arm64) ;;
  *) echo "Expected linux-x64, osx-x64 or osx-arm64." >&2; exit 2 ;;
esac

root="$(cd -- "$(dirname -- "$0")/.." && pwd -P)"
cd "$root"
output="${2:-artifacts/publish/$rid}"
agent="$output/RelayLink.Agent"
migrator="$output/RelayLink.Agent.ConfigMigrator"

for component in RelayLink.Agent RelayLink.Agent.ConfigMigrator; do
  project="src/$component/$component.csproj"
  dotnet restore "$project" --runtime "$rid" --configfile NuGet.Config
  dotnet publish "$project" --configuration Release --runtime "$rid" \
    --self-contained true --no-restore --output "$output/$component"
done

cp -- "$migrator"/RelayLink.Agent.ConfigMigrator* "$agent/"
if [[ "$rid" == osx-* ]]; then
  install -m 0755 deploy/macos/start-relaylink-agent.sh "$agent/start-relaylink-agent.sh"
fi
