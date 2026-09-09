#!/usr/bin/env bash
#
# Append-only identity ledger for pg_stat_activity, for diagnosing the Postgres backend SIGPIPE crash.
#
# WHY THIS EXISTS. A Postgres backend in this dev stack dies of `signal 13: Broken pipe`, forcing the
# postmaster to terminate every other backend and reinitialize. Four occurrences are on record and the
# cause is unknown. Every one of the four dying PIDs emitted ZERO log lines of its own, so there is
# nothing on disk to map a PID to a process class or a client — which is why four crashes have produced
# no diagnosis. This script records that mapping continuously, so the next crash is diagnosable.
# Design: docs/specs/2026-09-09-postgres-backend-sigpipe-diagnosis-design.md
#
# WHAT IT RECORDS. Two files under --out-dir:
#   ledger.csv     one row per (pid, backend_start) first seen, from pg_stat_activity
#   containers.csv timestamped container name -> IP samples, because container IPs are DYNAMIC and an
#                  address captured today cannot be resolved after a restart
#
# READING IT AFTER A CRASH is spec section 5. Two things there are not optional: scope the lookup to the
# crash's postmaster epoch (the container has its own PID namespace, so PIDs collide across restarts and
# an unsampled backend otherwise resolves to a previous epoch's row -- a confident wrong answer), and
# resolve client_addr against the map AS RECORDED NEAREST THE CRASH, not against the current stack.
#
# RUNNING IT. Foreground by default. To leave it running:  setsid ./scripts/sigpipe-ledger.sh &
# The script writes its own PID to <out-dir>/poller.pid and prints the stop command on startup.
# Stopping it is the whole teardown -- it changes no Postgres state and creates no containers.
#
# state and query are recorded as of the FIRST tick that saw a backend, not as of the crash.

set -euo pipefail

CONTAINER="iverson-postgres"
OUT_DIR="${HOME}/iverson-sigpipe-ledger"
INTERVAL=1
MAP_EVERY=30

usage() {
    cat <<USAGE
Usage: $(basename "$0") [--container NAME] [--out-dir DIR] [--interval SECONDS] [--map-every TICKS]

  --container   Postgres container to poll        (default: ${CONTAINER})
  --out-dir     where ledger.csv/containers.csv go (default: ${OUT_DIR})
  --interval    seconds between ticks              (default: ${INTERVAL})
  --map-every   sample the container map every N ticks (default: ${MAP_EVERY})
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --container) CONTAINER="$2"; shift 2 ;;
        --out-dir)   OUT_DIR="$2";   shift 2 ;;
        --interval)  INTERVAL="$2";  shift 2 ;;
        --map-every) MAP_EVERY="$2"; shift 2 ;;
        -h|--help)   usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# A non-positive --map-every divides by zero inside the guarded (( … )) below, which is swallowed
# exactly as the modulo bug was.
[[ "$MAP_EVERY" =~ ^[1-9][0-9]*$ ]] || { echo "--map-every must be a positive integer" >&2; exit 2; }

mkdir -p "$OUT_DIR"
LEDGER="${OUT_DIR}/ledger.csv"
MAP="${OUT_DIR}/containers.csv"

[[ -s "$LEDGER" ]] || echo "sampled_at,pid,backend_start,backend_type,usename,datname,client_addr,client_port,application_name,state,query" > "$LEDGER"
[[ -s "$MAP" ]]    || echo "sampled_at,container,ip" > "$MAP"

# regexp_replace collapses whitespace in `query`: without it a query containing a newline produces a
# multi-line CSV record, and the line-based dedup below silently mis-parses it.
# host() strips the netmask that raw client_addr carries (10.89.0.10/32 -> 10.89.0.10).
read -r -d '' QUERY <<'SQL' || true
copy (select now(), pid, backend_start, backend_type,
             coalesce(usename,''), coalesce(datname,''),
             coalesce(host(client_addr),''), coalesce(client_port::text,''),
             coalesce(application_name,''), coalesce(state,''),
             regexp_replace(coalesce(query,''), '\s+', ' ', 'g')
      from pg_stat_activity) to stdout with csv
SQL

declare -A SEEN

# Write poller PID and print stop command
PID_FILE="${OUT_DIR}/poller.pid"
echo $$ > "$PID_FILE"
echo "poller PID $$ — stop with: kill \$(cat ${PID_FILE})"

# Prime SEEN from existing ledger to avoid duplicates on relaunch. Field-split via `read` itself
# -- no `cut` fork per row (measured ~0.5 ms/row vs ~14 ms/row for two `cut` forks per row, on a
# 20k-row synthetic ledger) -- and skip application_name=sigpipe-poller rows outright: every poller
# tick opens a new connection, so a poller row's (pid, backend_start) can never recur, and poller
# rows are ~98% of the ledger, so skipping them removes almost all of what work remains. The printed
# counts make priming visible on stdout instead of a silent gap between "poller PID" above and
# "polling ..." below that otherwise reads as an already-healthy poller while it is still working
# through the backlog.
if [[ -s "$LEDGER" ]]; then
    to_prime=$(( $(wc -l < "$LEDGER") - 1 ))
    (( to_prime < 0 )) && to_prime=0
    echo "[sigpipe-ledger] priming SEEN from ${to_prime} existing ledger row(s)…"
    while IFS=, read -r _ pid bstart _ _ _ _ _ appname _rest; do
        [[ -z "$pid" ]] && continue
        [[ "$appname" == "sigpipe-poller" ]] && continue
        SEEN["${pid}|${bstart}"]=1
    done < <(tail -n +2 "$LEDGER")
    echo "[sigpipe-ledger] priming complete: ${#SEEN[@]} row(s) primed"
fi

echo "[sigpipe-ledger] polling ${CONTAINER} every ${INTERVAL}s -> ${LEDGER}"
echo "[sigpipe-ledger] container map every ${MAP_EVERY} ticks -> ${MAP}"

tick=0
while true; do
    tick=$(( tick + 1 ))

    # if-guarded ON PURPOSE. Under `set -e` an unguarded failing command exits the script, and a failing
    # tick is the NORMAL case: while the postmaster recovers it refuses every connection for 2-9 seconds.
    # An unguarded loop would die during exactly the event this script exists to observe.
    if snapshot=$(docker exec -e PGAPPNAME=sigpipe-poller "$CONTAINER" \
                    psql -U iverson -d postgres -tA -c "$QUERY" 2>/dev/null); then
        while IFS= read -r row; do
            [[ -z "$row" ]] && continue
            # Fields 1-3 are now(), pid, backend_start -- none can contain a comma, so cut is safe here
            # even though later fields are CSV-quoted.
            key="$(cut -d, -f2 <<<"$row")|$(cut -d, -f3 <<<"$row")"
            if [[ -z "${SEEN[$key]+set}" ]]; then
                SEEN[$key]=1
                printf '%s\n' "$row" >> "$LEDGER"
            fi
        done <<<"$snapshot"
    fi

    # (tick - 1) % MAP_EVERY, NOT tick % MAP_EVERY: the latter is never true for MAP_EVERY=1,
    # so --map-every 1 silently wrote no container map at all.
    if (( (tick - 1) % MAP_EVERY == 0 )); then
        if ids=$(docker ps -q) && [[ -n "$ids" ]]; then
            now="$(date -Is)"
            docker inspect -f '{{.Name}} {{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $ids 2>/dev/null \
              | while read -r name ip; do printf '%s,%s,%s\n' "$now" "${name#/}" "$ip" >> "$MAP"; done || true
        fi
    fi

    sleep "$INTERVAL"
done
