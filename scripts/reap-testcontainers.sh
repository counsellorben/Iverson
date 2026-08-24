#!/usr/bin/env bash
#
# Remove leaked Testcontainers containers, volumes and networks.
#
# WHY THIS EXISTS. Testcontainers normally reaps its own containers through a sidecar called Ryuk,
# which watches the test process and cleans up when it dies — including when it is KILLED, which a
# fixture's DisposeAsync cannot cover. On this project's dev box Ryuk is DISABLED
# (TESTCONTAINERS_RYUK_DISABLED=true in ~/.bashrc, ryuk.disabled=true in ~/.testcontainers.properties),
# so a killed or crashed test run leaves every container it started running forever. That has bitten
# twice already: once wedging the machine at load average 1329 (15 leaked containers, 9 of them
# StarRocks clusters), and once producing 61 spurious test failures in an unrelated suite whose
# containers were competing with the leaked ones for the box.
#
# Ryuk is NOT broken here — measured, not assumed. With TESTCONTAINERS_RYUK_DISABLED=false it starts
# correctly against the podman socket, and killing a test run mid-flight reaps every container plus
# Ryuk itself within 20 seconds. Re-enabling it is a one-line change to ~/.bashrc and is the real
# fix; this script is what to run until then, and on any machine where Ryuk genuinely cannot run.
#
# SAFETY. Only containers carrying the `org.testcontainers=true` label are touched, plus Ryuk's own
# container. The docker-compose dev stack (iverson-postgres, iverson-starrocks, ...) carries no such
# label and is never matched. Run with --dry-run to see the list first.

set -euo pipefail

DRY_RUN=0
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=1

docker_cmd() { command -v docker >/dev/null 2>&1 && echo docker || echo podman; }
DOCKER="$(docker_cmd)"

mapfile -t CONTAINERS < <(
  { $DOCKER ps -aq --filter "label=org.testcontainers=true"
    $DOCKER ps -aq --filter "name=testcontainers-ryuk"
  } | sort -u
)

if [[ ${#CONTAINERS[@]} -eq 0 ]]; then
  echo "No Testcontainers containers found."
else
  echo "Testcontainers containers:"
  $DOCKER ps -a --filter "label=org.testcontainers=true" --format '  {{.Names}}  {{.Image}}  {{.Status}}'
  $DOCKER ps -a --filter "name=testcontainers-ryuk" --format '  {{.Names}}  {{.Image}}  {{.Status}}'

  if [[ $DRY_RUN -eq 1 ]]; then
    echo "(--dry-run: nothing removed)"
  else
    echo "Removing ${#CONTAINERS[@]}..."
    $DOCKER rm -f "${CONTAINERS[@]}" >/dev/null
    echo "Removed."
  fi
fi

# Volumes and networks outlive their containers and accumulate silently; a leaked network in
# particular can exhaust the address pool and make later runs fail to start for a reason that looks
# nothing like a leak.
if [[ $DRY_RUN -eq 0 ]]; then
  $DOCKER volume prune -f --filter "label=org.testcontainers=true" >/dev/null 2>&1 || true
  $DOCKER network prune -f --filter "label=org.testcontainers=true" >/dev/null 2>&1 || true
fi

echo
echo "Still running (the dev stack is expected here; nothing below was touched):"
$DOCKER ps --format '  {{.Names}}  {{.Image}}' || true
