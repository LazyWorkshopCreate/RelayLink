#!/usr/bin/env bash
# Run a command several times to absorb transient runner/test flakes.
# The first successful attempt exits 0; all failures are reported.
set +e
attempts=$1
shift
status=1
for attempt in $(seq 1 "$attempts"); do
  "$@"
  status=$?
  if [ $status -eq 0 ]; then
    exit 0
  fi
  echo "::warning::attempt $attempt/$attempts failed: $*"
done
exit $status
