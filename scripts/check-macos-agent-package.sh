#!/usr/bin/env bash
set -euo pipefail

program_directory="$1"
architecture="$2"
case "$architecture" in
  arm64|x86_64) ;;
  *) echo 'Expected arm64 or x86_64.' >&2; exit 2 ;;
esac

for executable in RelayLink.Agent RelayLink.Agent.ConfigMigrator; do
  test -x "$program_directory/$executable"
  test "$(lipo -archs "$program_directory/$executable")" = "$architecture"
done
test -x "$program_directory/start-relaylink-agent.sh"
test -s "$program_directory/wwwroot/index.html"

if [[ "$architecture" == x86_64 && "$(uname -m)" == arm64 ]] &&
    ! /usr/bin/arch -x86_64 /usr/bin/true 2>/dev/null; then
  echo 'Intel package architecture verified. Execution requires Rosetta or an Intel Mac.'
  exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
umask 077
configuration="$work/agent.json"
printf '%s\n' '{"dashboardPort":0,"servers":[]}' > "$configuration"

"$program_directory/RelayLink.Agent.ConfigMigrator" --config "$configuration"
"$program_directory/RelayLink.Agent" --config "$configuration" --check-config
echo "Agent and configuration migrator executed successfully ($architecture)."
