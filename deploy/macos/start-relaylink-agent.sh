#!/bin/sh
set -eu
program_directory=/Library/RelayLink/Agent
configuration='/Library/Application Support/RelayLink/Agent/agent.json'
"$program_directory/RelayLink.Agent.ConfigMigrator" --config "$configuration"
exec "$program_directory/RelayLink.Agent" --config "$configuration"
