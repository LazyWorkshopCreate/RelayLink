#!/bin/bash
set -euo pipefail

if [[ "${RUNNER_OS:-}" != "macOS" || "${RUNNER_ENVIRONMENT:-}" != "github-hosted" ]]; then
  echo "This destructive launchd test is restricted to GitHub-hosted macOS runners." >&2
  exit 2
fi

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <runtime-identifier> <expected-machine-architecture>" >&2
  exit 2
fi

runtime_identifier="$1"
expected_architecture="$2"
actual_architecture="$(uname -m)"
if [[ "$actual_architecture" != "$expected_architecture" ]]; then
  echo "Expected runner architecture $expected_architecture, found $actual_architecture." >&2
  exit 1
fi

repository_root="$(cd "$(dirname "$0")/.." && pwd)"
publish_root="$repository_root/artifacts/macos-validation"
publish_directory="$publish_root/$runtime_identifier/RelayLink.Agent"
agent_binary="$publish_directory/RelayLink.Agent"
service_label="com.relaylink.agent"
service_user="_relaylink-agent"
program_directory="/Library/RelayLink/Agent"
state_directory="/Library/Application Support/RelayLink/Agent"
log_directory="/Library/Logs/RelayLink"
launchd_plist="/Library/LaunchDaemons/$service_label.plist"
configuration_path="$state_directory/agent.json"

service_loaded=false
account_created=false
group_created=false

cleanup() {
  set +e
  if [[ "$service_loaded" == true ]]; then
    sudo launchctl bootout "system/$service_label" >/dev/null 2>&1
  fi
  sudo rm -f "$launchd_plist"
  sudo rm -rf "$program_directory" "$state_directory"
  sudo rm -f "$log_directory/agent.stdout.log" "$log_directory/agent.stderr.log"
  if [[ "$account_created" == true ]]; then
    sudo dscl . -delete "/Users/$service_user" >/dev/null 2>&1
  fi
  if [[ "$group_created" == true ]]; then
    sudo dscl . -delete "/Groups/$service_user" >/dev/null 2>&1
  fi
}
trap cleanup EXIT

if dscl . -read "/Users/$service_user" >/dev/null 2>&1 || dscl . -read "/Groups/$service_user" >/dev/null 2>&1; then
  echo "Refusing to overwrite an existing $service_user account or group." >&2
  exit 1
fi

pwsh "$repository_root/scripts/publish.ps1" \
  -RuntimeIdentifier "$runtime_identifier" \
  -Component Agent \
  -OutputRoot "$publish_root"

test -x "$agent_binary"
binary_architectures="$(lipo -archs "$agent_binary")"
if [[ "$binary_architectures" != "$expected_architecture" ]]; then
  echo "Expected a thin $expected_architecture executable, found: $binary_architectures" >&2
  exit 1
fi

service_id=""
for candidate in $(seq 499 -1 400); do
  if ! dscl . -list /Users UniqueID | awk '{print $2}' | grep -qx "$candidate" && \
     ! dscl . -list /Groups PrimaryGroupID | awk '{print $2}' | grep -qx "$candidate"; then
    service_id="$candidate"
    break
  fi
done
if [[ -z "$service_id" ]]; then
  echo "Unable to find an unused system UID/GID." >&2
  exit 1
fi

sudo dscl . -create "/Groups/$service_user"
sudo dscl . -create "/Groups/$service_user" PrimaryGroupID "$service_id"
sudo dscl . -create "/Groups/$service_user" Password '*'
group_created=true
sudo dscl . -create "/Users/$service_user"
sudo dscl . -create "/Users/$service_user" UniqueID "$service_id"
sudo dscl . -create "/Users/$service_user" PrimaryGroupID "$service_id"
sudo dscl . -create "/Users/$service_user" UserShell /usr/bin/false
sudo dscl . -create "/Users/$service_user" NFSHomeDirectory /var/empty
sudo dscl . -create "/Users/$service_user" RealName 'RelayLink Agent'
sudo dscl . -create "/Users/$service_user" IsHidden 1
sudo dscl . -create "/Users/$service_user" Password '*'
account_created=true

sudo install -d -o root -g wheel -m 0755 "$program_directory"
sudo ditto "$publish_directory" "$program_directory"
sudo chown -R root:wheel "$program_directory"
sudo chmod -R go-w "$program_directory"
sudo chmod 0755 "$program_directory/RelayLink.Agent"
sudo install -d -o "$service_user" -g "$service_user" -m 0700 "$state_directory"
sudo install -d -o "$service_user" -g "$service_user" -m 0750 "$log_directory"

configuration_file="$RUNNER_TEMP/relaylink-agent-ci.json"
cat > "$configuration_file" <<'JSON'
{
  "serverHost": "127.0.0.1",
  "serverPort": 65500,
  "dataPort": 65501,
  "useTls": false,
  "clientId": "macos-ci",
  "secret": "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
  "outboundPortRangeStart": 62000,
  "outboundPortRangeEnd": 62999,
  "dashboardPort": 18081,
  "reconnect": {
    "initialDelaySeconds": 1,
    "maxDelaySeconds": 2,
    "permanentErrorDelaySeconds": 3
  }
}
JSON
sudo install -o "$service_user" -g "$service_user" -m 0600 "$configuration_file" "$configuration_path"

sudo -u "$service_user" "$program_directory/RelayLink.Agent" --config "$configuration_path" --check-config
fingerprint_before="$(sudo -u "$service_user" "$program_directory/RelayLink.Agent" --config "$configuration_path" --show-e2e-fingerprint)"

sudo install -o root -g wheel -m 0644 \
  "$repository_root/deploy/macos/relaylink-agent.plist" "$launchd_plist"
sudo plutil -lint "$launchd_plist"
sudo launchctl bootstrap system "$launchd_plist"
service_loaded=true
sudo launchctl enable "system/$service_label"
sudo launchctl kickstart -k "system/$service_label"

for _ in $(seq 1 30); do
  if curl --fail --silent http://127.0.0.1:18081/api/status > "$RUNNER_TEMP/relaylink-agent-status.json"; then
    break
  fi
  sleep 1
done
grep -q '"clientId":"macos-ci"' "$RUNNER_TEMP/relaylink-agent-status.json"
grep -q '"online":false' "$RUNNER_TEMP/relaylink-agent-status.json"

launchd_state="$(sudo launchctl print "system/$service_label")"
grep -q 'state = running' <<< "$launchd_state"
first_pid="$(awk '/pid =/{print $3; exit}' <<< "$launchd_state")"
test -n "$first_pid"
sudo kill -KILL "$first_pid"

second_pid=""
for _ in $(seq 1 30); do
  second_pid="$(sudo launchctl print "system/$service_label" 2>/dev/null | awk '/pid =/{print $3; exit}')"
  if [[ -n "$second_pid" && "$second_pid" != "$first_pid" ]] && \
     curl --fail --silent http://127.0.0.1:18081/api/status >/dev/null; then
    break
  fi
  sleep 1
done
if [[ -z "$second_pid" || "$second_pid" == "$first_pid" ]]; then
  echo "launchd did not restart the Agent after an abnormal exit." >&2
  exit 1
fi

fingerprint_after="$(sudo -u "$service_user" "$program_directory/RelayLink.Agent" --config "$configuration_path" --show-e2e-fingerprint)"
if [[ "$fingerprint_before" != "$fingerprint_after" ]]; then
  echo "Agent identity changed after launchd restart." >&2
  exit 1
fi

[[ "$(stat -f '%Su:%Sg %Lp' "$program_directory")" == 'root:wheel 755' ]]
[[ "$(stat -f '%Su:%Sg %Lp' "$state_directory")" == "$service_user:$service_user 700" ]]
[[ "$(stat -f '%Su:%Sg %Lp' "$configuration_path")" == "$service_user:$service_user 600" ]]
[[ "$(stat -f '%Su:%Sg %Lp' "$launchd_plist")" == 'root:wheel 644' ]]

echo "macOS $expected_architecture Agent publish and launchd validation passed."
