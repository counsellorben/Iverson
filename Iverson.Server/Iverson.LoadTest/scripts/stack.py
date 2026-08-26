#!/usr/bin/env python3
"""Bring up (or tear down) the Docker Compose container tier the direct-Qdrant benchmark
scripts need, without paying for the full stack's cold start.

BenchmarkIngestScenario writes straight to Qdrant, bypassing the gRPC/Kafka write path, so
it needs far fewer containers than a normal `docker compose up`. Two tiers:

    ingest  qdrant, ollama
            everything ingest.py touches: the vector store and the embedding model.

    query   qdrant, ollama, postgres, redis, authentik-server, iverson-api
            adds what report.py needs to issue real SearchSimilar/SearchChunks calls
            through the gRPC read path.

Usage:
    python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py ingest
    python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py query
    python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py down
    python3 Iverson.Server/Iverson.LoadTest/scripts/stack.py query --timeout 300

`ingest`/`query` run `docker compose up -d --no-deps <tier services>`. --no-deps is load
-bearing: iverson-api's compose entry declares `depends_on: starrocks (service_healthy),
kafka (service_healthy), jaeger (service_healthy), ollama-init (service_completed
_successfully)`, none of which the direct-Qdrant path touches. Without --no-deps, `query`
would start and then wait out StarRocks's 60s+ cold-start gate for a dependency this
benchmark never queries.

Any *running* container whose name starts with `iverson-` but is not part of the requested
tier is then stopped, so a previous `query` run doesn't leave iverson-api and its
dependencies idling underneath a later `ingest` run. Only `iverson-`-prefixed containers are
ever touched by this script: `Iverson.Api.Tests` spins up Testcontainers with randomly
generated names (e.g. `heuristic_hofstadter`) on the same Docker daemon, possibly from a
concurrent test run in another worktree, and those must never be stopped by an unrelated
benchmark run. What was stopped and what was left alone are both printed.

`down` stops every running `iverson-`-prefixed container. It starts nothing and waits for no
readiness -- there is no tier left running afterward to wait on.

`ingest`/`query` then poll for readiness: Qdrant's `GET /readyz`, Ollama's `GET /api/tags`,
and for iverson-api a raw TCP connect to 127.0.0.1:8080 -- port 8080 is h2c/gRPC-only
(Kestrel configured Http2-only) and refuses HTTP/1.1, so an HTTP probe against it hangs or
errors rather than reporting readiness. Postgres/redis/authentik-server have no readiness
check of their own here: iverson-api's own startup runs its bootstrap DDL against them
before Kestrel ever binds 8080 (see docker-compose.yml's comment on iverson-api's
healthcheck), so a successful connect to 8080 already implies they were reachable.

Exit status is nonzero, with a diagnostic on stderr, if `docker compose up`/`docker stop`
fails or a tier does not reach readiness within --timeout seconds -- so a caller can chain
`stack.py query && dotnet run ...` without a manual sleep.
"""

import argparse
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

# This file lives at Iverson.Server/Iverson.LoadTest/scripts/stack.py; docker-compose.yml
# lives at Iverson.Server/docker-compose.yml. `docker compose` must be invoked with that
# directory as cwd, not the script's own directory.
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
COMPOSE_DIR = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))

DEFAULT_TIMEOUT = 180  # seconds; iverson-api's own compose healthcheck allows up to ~170s
                        # (30 retries * 5s interval + 20s start_period) on a cold container.

# Tiers name docker-compose *service* names -- what `docker compose up` takes.
TIERS = {
    "ingest": ["qdrant", "ollama"],
    "query": ["qdrant", "ollama", "postgres", "redis", "authentik-server", "iverson-api"],
}

# Service name -> container_name, copied from docker-compose.yml. `docker stop`/`docker ps`
# operate on container names, which differ from service names for every entry but qdrant,
# ollama and iverson-api.
CONTAINER = {
    "qdrant": "iverson-qdrant",
    "ollama": "iverson-ollama",
    "postgres": "iverson-postgres",
    "redis": "iverson-redis",
    "authentik-server": "iverson-authentik-server",
    "iverson-api": "iverson-api",
}


def run_compose_up(services):
    cmd = ["docker", "compose", "up", "-d", "--no-deps", *services]
    result = subprocess.run(cmd, cwd=COMPOSE_DIR)
    if result.returncode != 0:
        sys.exit(f"'{' '.join(cmd)}' (cwd={COMPOSE_DIR}) exited {result.returncode}")


def list_running_containers():
    result = subprocess.run(
        ["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True
    )
    if result.returncode != 0:
        sys.exit(f"'docker ps' exited {result.returncode}: {result.stderr.strip()}")
    return [name for name in result.stdout.splitlines() if name.strip()]


def stop_containers(names):
    if not names:
        return
    result = subprocess.run(["docker", "stop", *names])
    if result.returncode != 0:
        sys.exit(f"'docker stop' exited {result.returncode} for: {', '.join(names)}")


def stop_out_of_tier(tier):
    """Stop every RUNNING iverson-prefixed container not in `tier`. Never touches a
    container without the iverson- prefix -- see the module docstring on Testcontainers."""
    keep = {CONTAINER[service] for service in TIERS[tier]}
    running = list_running_containers()
    to_stop = sorted(
        name for name in running if name.startswith("iverson-") and name not in keep
    )
    left_alone = sorted(name for name in running if not name.startswith("iverson-"))

    stop_containers(to_stop)

    print(f"stopped ({len(to_stop)}): {', '.join(to_stop) if to_stop else '(none)'}")
    print(
        f"left alone, non-iverson- ({len(left_alone)}): "
        f"{', '.join(left_alone) if left_alone else '(none)'}"
    )


def wait_tcp(host, port, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with socket.create_connection((host, port), timeout=2):
                return True
        except OSError:
            time.sleep(1)
    return False


def wait_http_200(url, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as resp:
                if resp.status == 200:
                    return True
        except (urllib.error.URLError, OSError):
            pass
        time.sleep(1)
    return False


# service -> readiness probe. Services absent here (postgres, redis, authentik-server) are
# not polled directly -- see the module docstring for why that is sound.
READY_CHECKS = {
    "qdrant": lambda timeout: wait_http_200("http://127.0.0.1:6333/readyz", timeout),
    "ollama": lambda timeout: wait_http_200("http://127.0.0.1:11434/api/tags", timeout),
    "iverson-api": lambda timeout: wait_tcp("127.0.0.1", 8080, timeout),
}


def wait_for_tier(tier, timeout):
    for service in TIERS[tier]:
        check = READY_CHECKS.get(service)
        if check is None:
            continue
        print(f"waiting for {service} ...", end=" ", flush=True)
        if check(timeout):
            print("ready")
        else:
            sys.exit(f"{service} did not become ready within {timeout}s")


def cmd_tier(tier, timeout):
    run_compose_up(TIERS[tier])
    stop_out_of_tier(tier)
    wait_for_tier(tier, timeout)
    containers = ", ".join(CONTAINER[service] for service in TIERS[tier])
    print(f"{tier} tier ready: {containers}")


def cmd_down():
    running = list_running_containers()
    to_stop = sorted(name for name in running if name.startswith("iverson-"))
    left_alone = sorted(name for name in running if not name.startswith("iverson-"))

    stop_containers(to_stop)

    print(f"stopped ({len(to_stop)}): {', '.join(to_stop) if to_stop else '(none)'}")
    print(
        f"left alone, non-iverson- ({len(left_alone)}): "
        f"{', '.join(left_alone) if left_alone else '(none)'}"
    )


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("action", choices=[*TIERS.keys(), "down"])
    ap.add_argument(
        "--timeout",
        type=int,
        default=DEFAULT_TIMEOUT,
        help="seconds to wait for each service to become ready (default: %(default)s)",
    )
    args = ap.parse_args()

    if args.action == "down":
        cmd_down()
    else:
        cmd_tier(args.action, args.timeout)


if __name__ == "__main__":
    main()
