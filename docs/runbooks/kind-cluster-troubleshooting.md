# Kubernetes (kind) Deployment Troubleshooting

Every issue below was actually hit — most more than once — while bringing up this repo's `kind`
cluster from scratch or exercising it live, across multiple sessions. Organized by the order you'll
hit them: cluster/operator bootstrap → app chart install → NetworkPolicy → app-level bugs only
visible under live traffic → resource-constrained-node symptoms → testing pitfalls that look like
bugs but aren't → docker-compose-specific issues. Each entry has the symptom, root cause, and fix —
skip to the section matching where you are stuck.

**General principle that saved the most time across every one of these:** when a symptom doesn't
match what the config "should" do, don't retry the same workaround — rebuild the cluster from
scratch to rule out stale dataplane/state, then trace the actual failure with the tools in
"Diagnostic techniques" below instead of guessing.

## 1. Cluster and operator bootstrap

### 1.1 Calico (tigera-operator) fails on a fresh `helm upgrade --install`

**Symptom:** first-ever install of the `tigera-operator` chart fails Helm's manifest-validation pass
with unknown CRD kinds (`Installation`/`APIServer`/`Goldmane`/`Whisker`).

**Root cause:** the chart bundles the operator *and* its custom resources in one release, but the
CRDs those resources need are registered by the operator itself at runtime, not by Helm — a
single-shot install can never see them in time.

**Fix:** two-pass install. First pass renders only the operator + RBAC:
```bash
helm upgrade --install calico tigera-operator ... \
  --set installation.enabled=false --set apiServer.enabled=false \
  --set goldmane.enabled=false --set whisker.enabled=false
kubectl wait --for=condition=Established crd/installations.operator.tigera.io
```
Second pass installs the actual CRs — **must** include `--reset-values`, or Helm silently reuses the
first pass's `--set` values and the CRs never render:
```bash
helm upgrade --install calico tigera-operator ... --reset-values
```
`deploy/kind/setup.sh`/`setup.ps1` already do this — if you're hand-typing the install instead of
using the script, this is why it'll look broken.

### 1.2 Strimzi crash-loops with `Unknown feature gate`

**Symptom:** `strimzi-cluster-operator` crash-loops immediately after install.

**Root cause:** an old `--set featureGates="+KafkaNodePools,+UseKRaft"` flag targeting a Strimzi
version where those gates were still opt-in. The pinned operator image graduated both to GA and
deleted the flags entirely — passing them is now a hard error, not a no-op.

**Fix:** drop the `--set` entirely; both are default-on now. If this resurfaces after a chart/image
bump, check the operator's release notes for newly-deleted (not just newly-added) feature gates
before assuming the flag syntax is wrong.

### 1.3 StarRocks operator chart not found

**Symptom:** `helm install ... kube-starrocks-operator ...` fails, chart doesn't exist in the repo.

**Root cause:** the chart was renamed from `kube-starrocks-operator` to plain `operator` in
`https://starrocks.github.io/starrocks-kubernetes-operator`.

**Fix:** use the current chart name. `setup.sh`/`setup.ps1` are already patched — if this specific
chart-not-found error recurs, it means the operator's repo renamed it *again*; check the actual repo
index rather than re-guessing the old name.

### 1.4 `ErrImagePull` on `iverson-api`/`iverson-worker` after building locally

**Symptom:** `kubectl get pods` shows `ErrImagePull` / `pull access denied ...
docker.io/library/iverson-api:TAG` for a pod whose image you just built and loaded locally.

**Root cause:** on podman-backed `docker` CLI shims (common on WSL2), `docker build -t
iverson-api:TAG` auto-qualifies the image as `localhost/iverson-api:TAG` in the local store. The
Helm chart's `image.repository` is a bare name, which kind's containerd resolves to
`docker.io/library/iverson-api:TAG` by OCI convention — a reference the loaded image never had.
`kind load docker-image` then loads it under the wrong tag, kubelet's lookup misses, and it falls
through to a real registry pull that 403s.

**Fix:** use `Iverson.Server/deploy/kind/build-and-load-image.sh` (or `.ps1`), not hand-typed
`docker build`/`kind load docker-image`. It explicitly re-tags to the fully-qualified
`docker.io/library/...` reference before loading — a harmless no-op under real Docker, the actual
fix under podman, so it's correct regardless of which provider you're on. If this exact error
resurfaces, check whether the script's re-tag step was skipped (e.g. someone reverted to a manual
`docker build`), not whether `values.yaml`'s tag is wrong.

## 2. NetworkPolicy

These were the most time-consuming class of bug in this repo's history — a NetworkPolicy object that
*looks* syntactically correct can still enforce nothing, or block a real client, for four distinct
reasons found so far.

### 2.1 A NetworkPolicy port rule never matches, even though the object looks right

**Root cause:** Calico enforces NetworkPolicy in the iptables `FORWARD` chain, **downstream of
kube-proxy's Service DNAT**. A rule written against a Service's declared port (e.g. `port: 443` for
the `kubernetes` Service) never matches once the packet's destination port has already been
rewritten to the real backend port — on kind/kubeadm-style self-hosted control planes the real
apiserver port is **6443**, not 443. Confirmed to generalize beyond just the `kubernetes` Service
(`cnpg-webhook-service` :443→:9443, `ingress-nginx-controller-admission` :443→:8443 hit the same
pattern).

**Fix:** allow both the Service port and the real backend port wherever a rule targets a Service by
its external port, especially the Kubernetes API server (this affected both
`iverson-postgres-egress`, since CNPG's initdb job calls the apiserver directly, and
`iverson-kafka-egress`, since Strimzi's `KubernetesSecretConfigProvider` does too).

### 2.2 An entire NetworkPolicy section silently applies to zero pods

**Root cause:** the StarRocks NetworkPolicy selectors were written against
`app.starrocks.io/cluster`, a label the StarRocks Operator never actually applies. Its real runtime
labels are `app.starrocks.ownerreference/name: <cr>-fe|be` + `app.kubernetes.io/component: fe|be`
(the same labels the FE/BE PodDisruptionBudgets correctly use — the NetworkPolicy templates were
written independently and never cross-checked). A `kube-score/ignore: networkpolicy-targets-pod`
comment on this policy had wrongly attributed "kube-score can't verify this" to a kube-score
static-analysis blind spot, when the selector was actually just wrong.

**Fix:** `matchExpressions`/`In` against the operator's real labels (FE and BE need different
`ownerreference/name` values, so no single label selector covers both).

### 2.3 A service works for its primary port but times out on a secondary one

Two instances of this shape:
- **StarRocks BE→FE registration timed out** even after fix 2.2 above — `iverson-starrocks-egress`
  only allowed the MySQL query port (9030). BE actually registers with FE over `rpc_port` (9020),
  plus edit_log (9010)/http (8030) and BE's own ports (8040/8060/9050/9060).
- **Strimzi's `entity-operator` crash-looped with `TimeoutException: fetchMetadata`** even after the
  broker itself was healthy — `iverson-kafka-egress` only allowed the bootstrap service's
  `tcp-clientstls` port (9093), not `tcp-replication` (9091), which `entity-operator` needs to fetch
  cluster metadata.

**Fix:** don't assume "the primary port is open" means a component's other required ports are too —
check the actual multi-port protocol requirements (StarRocks FE↔BE coordination, Strimzi's internal
listener set) rather than just the client-facing port.

### 2.4 One-sided ingress/egress rule (real error code: `ETIMEDOUT`, not `ECONNREFUSED`)

**Symptom:** `mysql -h ... ` (or any client) fails with `ERROR 2003 ... (110)` — that's `ETIMEDOUT`,
a real connection timeout, not a refusal.

**Root cause:** the StarRocks `create-user` Job's own egress rule allowed it to *send* to FE on
9030, but `iverson-starrocks-ingress` had no matching `from:` rule to *accept* from the
`job-name: <release>-starrocks-create-user` pod. A one-sided policy pair — same shape existed
correctly for Postgres's ingress/egress, just missing here.

**Diagnostic signature worth remembering:** `ETIMEDOUT` with a real error code (not a silent hang,
not a clean refusal) is a strong tell for exactly this class of gap — the client's own network stack
never even got a RST, meaning packets left the pod and were dropped somewhere in between, which
matches "sender's egress allowed it out, receiver's ingress never let it in."

### 2.5 A stray, unmanaged NetworkPolicy-adjacent resource blocks a `helm upgrade`

**Symptom:** `helm upgrade --install` fails immediately with `Unable to continue with update: ...
exists and cannot be imported into the current release: invalid ownership metadata`.

**Root cause:** a resource with the same name the chart wants to own (in one real case, a
NetworkPolicy) was created by a direct `kubectl apply` at some point in the cluster's history — it's
missing the `app.kubernetes.io/managed-by: Helm` label and `meta.helm.sh/release-{name,namespace}`
annotations Helm requires to adopt an existing object into a release.

**Fix (non-destructive — adopt, don't delete):**
```bash
kubectl annotate <kind> <name> meta.helm.sh/release-name=<release> meta.helm.sh/release-namespace=<namespace> --overwrite
kubectl label <kind> <name> app.kubernetes.io/managed-by=Helm --overwrite
```
Only do this after confirming the resource actually matches what the chart's template would produce
(`helm template` it and diff) — this recovers a legitimate drift, it doesn't paper over a real
naming collision with something else.

### 2.6 A debug/one-off pod can't reach anything (`Resource temporarily unavailable` on connect)

**Symptom:** a throwaway debug pod (e.g. for manually running a CLI tool against the cluster) times
out connecting to *any* other pod, even ones with permissive-looking ingress rules.

**Root cause:** this chart's `networkPolicy.enabled: true` baseline includes a cluster-wide
`<release>-default-deny` NetworkPolicy with an empty `podSelector` (matches every pod) and no egress
rules at all. Every pod's *egress* is denied by default unless a matching `*-egress` policy exists
for its specific label — an ad hoc debug pod with no recognized `app:` label has literally no egress
path anywhere, regardless of what the destination's ingress policy allows (NetworkPolicy requires
**both** sides to agree: source's egress AND destination's ingress).

**Fix (temporary, always revert):** label the debug pod with a distinct `app:` value, then add a
narrowly-scoped temporary NetworkPolicy granting it exactly the DNS (port 53 UDP+TCP) + destination
port egress it needs, plus a matching temporary ingress allowance on the destination's own
NetworkPolicy for that same label. Diff the destination policy's `spec` against a saved copy
afterward to confirm your revert left it byte-identical (whitespace/`generation` differences are
fine, structural differences are not).

## 3. App-level bugs only visible under live/real traffic

These were never caught by unit tests because they only manifest when the app actually talks to a
real Postgres/StarRocks/Kubernetes control plane over the network — worth specifically testing for
in any *first* live smoke test of a new environment.

- **Npgsql crashed on every startup**: `ConnectionStrings__Postgres` was wired straight from CNPG's
  Secret's `uri` key (`postgresql://...`), but Npgsql only accepts ADO.NET `Key=Value;` syntax, never
  libpq-style URIs. Fix: compose `Host=...;Port=5432;Database=...;Username=...;Password=...` from the
  Secret's individual keys instead.
- **Readiness/liveness probes hit the wrong port and got a 400**: this app runs gRPC on one Kestrel
  endpoint (HTTP/2-only) and `/health`/`/health/live` on a separate HTTP/1.1 endpoint — but the
  chart's probes (and the ingress NetworkPolicy) only referenced the gRPC port. kubelet's HTTP/1.1
  probe client gets rejected by an HTTP/2-only endpoint. Fix: point probes at the correct port; add a
  NetworkPolicy ingress rule with no `from:` restriction for kubelet probes specifically (they
  originate from the node itself, not a pod/namespace, so no podSelector-based rule can cover them).
- **StarRocks `create-user` Job's `GRANT` syntax rejected**: MySQL-style `GRANT ... ON db.* TO ...`
  and a bare `CREATE` privilege aren't valid in StarRocks' SQL dialect — DDL privileges
  (`CREATE TABLE`/`DROP`/`ALTER`) must be scoped `ON DATABASE <db>`, not `ON db.*`, and `CREATE` must
  be written as the two-word `CREATE TABLE`. Fix: split into a table-level DML grant and a separate
  database-level DDL grant.
- **`worker`'s in-memory `SchemaRegistry` never learns about schemas registered after it started** —
  a real regression introduced by splitting `api`/`worker` into separate processes/pods. `api`
  updates its own in-memory copy + Postgres on `RegisterSchemaAsync`, but never notifies already
  running `worker` pods, which silently drop events for the new type until restarted. Fixed via a
  periodic (30s) re-poll in both roles.
- **`helm upgrade --install --wait` deadlocks on a genuinely fresh cluster** because `api`/`worker`
  readiness depends on a StarRocks user that a *post-install* hook creates — and Helm only runs
  post-install hooks after `--wait` on the main manifest (including those same Deployments'
  readiness) already succeeded. A real circular dependency, not a bug in this repo's hook
  configuration. Fixed by having the health check classify "StarRocks reachable but this user
  doesn't exist yet" (`MySqlErrorCode.AccessDenied`) as a distinct `AuthPending` status that's
  tolerated for k8s readiness while still honestly reporting `degraded` in the response body.
  **If you hit this on an older checkout without the fix**, the workaround is to install without
  `--wait` on the very first install only.

## 4. Resource-constrained node symptoms

This repo's `kind` node is consistently CPU/pid-constrained (WSL2 → podman → kind nested
virtualization, single node, ~4 vCPU). These symptoms are environment limits, not chart bugs — don't
spend time trying to "fix" them in the chart.

### 4.1 StarRocks BE crash-loops with `pthread_create` EAGAIN

**Symptom:** BE crashes with `std::system_error: Resource temporarily unavailable` inside
`StorageEngine::start_bg_threads()`.

**Root cause:** `pids.max = 307` on every container on this specific kind node, regardless of
workload or resource requests (confirmed via `crictl inspect` across a dozen unrelated containers —
all show the identical value). A fixed, node-wide systemd/containerd default baked into this nested
WSL2 stack. `securityContext.capabilities.add: ["SYS_RESOURCE"]` does **not** fix this — it was tried
and rejected outright by the namespace's `pod-security.kubernetes.io/enforce=baseline` label, and
even if allowed, `SYS_RESOURCE` only governs POSIX rlimits, not the cgroups `pids.max` ceiling
underneath them.

**Accepted as a known local-only limitation** — would not reproduce on real EKS/AKS/GKE nodes. Don't
re-attempt the `SYS_RESOURCE` route if this resurfaces; it's a dead end at the wrong layer entirely.

### 4.2 Every rollout requires manually freeing CPU for the new pod to schedule

**Symptom:** `kubectl rollout status` (or a plain `kubectl get pods`) shows the new ReplicaSet's pod
stuck `Pending` with `FailedScheduling: 0/1 nodes are available: 1 Insufficient cpu`, while the
*old* ReplicaSet's pod is still `Running` at full readiness.

**Root cause:** on a single, fully-CPU-committed node, a RollingUpdate's default
`maxSurge`/`maxUnavailable` budget can't be satisfied — Kubernetes won't scale the old ReplicaSet
down until the new one is healthy, but the new one can't schedule until the old one's CPU is freed. A
genuine chicken-and-egg deadlock specific to a resource-saturated single-node dev cluster.

**Fix:** manually scale the *old* ReplicaSet to 0 (not delete individual pods — that just gets
recreated by the same ReplicaSet and changes nothing):
```bash
kubectl get rs -n <ns> -l app=<app>          # find the old (non-current) ReplicaSet
kubectl scale rs -n <ns> <old-rs-name> --replicas=0
```
If there still isn't enough headroom (e.g. bringing up a brand-new component like StarRocks BE or
Ollama for the first time on an already-full node), temporarily scale down genuinely non-essential
components (jaeger/prometheus/worker are usually safe) and scale them back up once the target pod is
scheduled.

### 4.3 A brand-new .NET pod crashes on startup with an `inotify` error

**Symptom:**
```
Unhandled exception. System.IO.IOException: The configured user limit (128) on the number of
inotify instances has been reached ...
   at Microsoft.Extensions.FileProviders.Physical.PhysicalFilesWatcher.TryEnableFileSystemWatcher()
```

**Root cause:** ASP.NET Core's config system watches `appsettings.json` for hot-reload by default,
which needs an inotify instance. `fs.inotify.max_user_instances` is capped at 128 host-wide on this
sandbox and gets exhausted by the sheer number of processes/IDE language servers/build tools already
running — this is a kernel-level, per-user limit shared across every container on the host, not
something any single pod's resources can raise.

**Fix:** you generally can't raise the sysctl (needs root, often unavailable in a sandboxed dev
environment). Disable the config-reload file watcher instead — set the environment variable
`DOTNET_hostBuilder__reloadConfigOnChange=false` on the affected Deployment. This is a
sandbox-specific workaround; don't commit it to the chart as a permanent value unless the target
environment is known to have the same constraint.

## 5. Testing/verification pitfalls that look like auth or config bugs but aren't

### 5.1 A freshly-minted bearer token gets rejected with a generic 401, no useful log line

**Symptom:** you mint a client-credentials token straight from Authentik's `/application/o/token/`
endpoint via a `kubectl port-forward` (e.g. `localhost:19000`), pass it to the API, and get a bare
401 with nothing informative in the API's logs.

**Root cause:** Authentik derives the OIDC `issuer` claim from the **request's `Host` header**,
dynamically, per request — not from a fixed config value. A token minted by hitting
`http://localhost:19000/...` gets `iss: "http://localhost:19000/"` baked in. But the API validates
tokens using OIDC discovery metadata fetched via its own configured `Authority`
(`http://<release>-authentik:9000/...`, the in-cluster Service DNS name) — whose issuer is
`http://<release>-authentik:9000/`. These don't match, so ASP.NET Core's default issuer validation
silently rejects the token. This has **no effect on real deployments** (real callers always reach
Authentik via the same hostname the API's `Authority` is configured with) — it's purely an artifact
of testing through a local port-forward on a different hostname/port than the in-cluster one.

**Fix:** when manually minting a test token via `kubectl port-forward`, force the request's Host
header to match what the API actually uses for discovery:
```bash
curl -X POST "http://localhost:<forwarded-port>/application/o/token/" \
  -H "Host: <release>-authentik:9000" \
  -d "grant_type=client_credentials&client_id=...&client_secret=..."
```
Decode the resulting token's `iss` claim (base64-decode the JWT's middle segment) and confirm it
matches the API's actual `Authentication:Authority` host before concluding the auth pipeline itself
is broken. For a gRPC client under test (e.g. `Iverson.LoadTest`) that can't have its Host header
overridden this way, run it **inside the cluster** (a throwaway debug pod, see 2.6 above for the
NetworkPolicy allowances it'll need) so its DNS resolution and Host headers match the real in-cluster
names naturally, rather than fighting port-forward hostname mismatches.

### 5.2 `secretKeyRef`-sourced env vars look empty/stale after a Secret's data changes

**Symptom:** you update a Secret's data (e.g. via a Helm `lookup`-guard convergence pass), but the
already-running pod's behavior doesn't reflect the new value, and `kubectl get pod ... -o
jsonpath='{...env[*].value}'` shows the var as empty.

**Two separate traps here:**
1. `.value` in the Pod spec **never** shows a `secretKeyRef`-resolved value — only a literal
   `value:` field. The pod's *actual* resolved environment only exists inside the running container.
   Check it with `kubectl exec <pod> -- printenv`, not `kubectl get pod -o jsonpath`.
2. Even checked correctly, `secretKeyRef` values are resolved **once, at pod creation** — Kubernetes
   does not live-update a running container's environment when the backing Secret's data changes
   later. If a Deployment's pods were created before a Secret's value converged to its final state
   (e.g. Helm's two-pass `lookup`-guard pattern), you need `kubectl rollout restart deployment/<name>`
   to pick up the current Secret content.

### 5.3 A live smoke-test script's own curl command uses the wrong HTTP verb

If a documented smoke-test step curls an endpoint with `-X POST` and gets a 404/405 instead of the
expected 401/403, check the actual route registration (`grep "MapGet\|MapPost" Program.cs`) before
assuming auth is broken — a mismatched verb fails at routing, before authentication middleware ever
runs, and produces a misleading result that looks like an auth gap.

### 5.4 Editing a subchart's template has no effect on `helm template`/`helm upgrade`

**Symptom:** you edit a file under `charts/<subchart>/templates/*.yaml` or
`charts/<subchart>/values.yaml`, but `helm template`/`helm upgrade` renders exactly as if you hadn't.

**Root cause:** `helm dependency build` packages every subchart into a `charts/<name>-<version>.tgz`
archive. If that archive already exists (from an earlier `helm dependency build`), Helm silently
prefers it over the live, unpacked `charts/<name>/` directory — with **zero warning** that your edit
was ignored. This only affects genuine subchart directories; the umbrella chart's own top-level
`templates/*.yaml` is unaffected.

**Fix:** run `helm dependency build` again after *every* edit to a subchart's templates or values,
before the next `helm template`/`helm lint`/`helm upgrade`. If a change "isn't taking effect" and
you've ruled out everything else, this is the first thing to check.

### 5.5 A corrective `helm upgrade` silently keeps the broken prior values

If you're re-running `helm upgrade` to apply a fix and it looks like nothing changed, check whether
you passed `--reset-values` (or repeated every `--set`) — a plain `helm upgrade` reuses the previous
release's values by default, so a fix expressed only as a new `--set` on the command line (not
committed to a values file) silently no-ops on the second attempt.

## 6. docker-compose-specific issues

### 6.1 `authentik-server` retries forever with `password authentication failed for user "authentik"`

**Root cause:** a named Postgres volume (e.g. `iversonserver_postgres_data`) left over from an
earlier `docker compose up` still has the *old* password baked into its initialized data directory —
Postgres only applies `POSTGRES_PASSWORD`-style env vars the **first** time it initializes an empty
data directory. Changing the compose file's password later has no effect on an already-initialized
volume.

**Fix:** `docker compose down`, then `docker volume rm <project>_postgres_data`, then
`docker compose up -d` again to let Postgres reinitialize fresh. (Fresh-DB migrations then take a
few minutes on the Authentik side — a `docker compose up` dependency-wait giving up with "container
... is unhealthy" partway through is often just this normal migration time, not a real failure; check
`docker logs <container>` for actual progress before assuming it's broken.)

### 6.2 Port conflict between a running `kind` cluster and docker-compose

**Symptom:** `docker compose up` fails with `rootlessport listen tcp 0.0.0.0:8080: bind: address
already in use` for a service whose compose port mapping you didn't think was in conflict.

**Root cause:** kind's own control-plane container has its own host port mapping (e.g. `kind-config.
yaml`'s `extraPortMappings`, commonly 8080/8443 for its built-in ingress) — if docker-compose's
`iverson-api` service *also* wants host port 8080, the two environments collide even though they're
otherwise fully independent stacks.

**Fix:** don't run both a live `kind` cluster and the full docker-compose stack on the same host at
the same time unless you've confirmed no host-port overlap. If you need to run the compose parity
check while `kind` already occupies the conflicting port, stop the kind cluster's container first
(`docker stop <kind-cluster-name>-control-plane`) rather than trying to remap docker-compose's fixed
port mappings.

## Diagnostic techniques worth reusing

- **Don't trust "the NetworkPolicy object looks correct."** Create a throwaway debug pod carrying the
  *exact* label(s) the policy's `podSelector`/`from`/`to` expects, test real connectivity from it. If
  it still fails, trace `iptables -t filter -L <chain> -v -n -x` counters chain-by-chain
  (`cali-fw-<iface>` → `cali-po-<policyid>` → ipset membership via `ipset list` on the calico-node
  pod) to find exactly which rule isn't matching and why. A counter you expected to increment staying
  at zero is the tell. Run `iptables -Z` between attempts so repeated tests don't confuse stale
  counts.
- **`ETIMEDOUT` vs `ECONNREFUSED` vs a silent hang are three different diagnostic signatures** — a
  real ETIMEDOUT (not a hang) usually means one side's policy allowed the packet out/in and the other
  side's didn't; a silent hang more often means DNS or scheduling; ECONNREFUSED means you reached the
  host but nothing's listening on that port.
- **When a rollout is stuck, check `kubectl describe pod` for the actual `FailedScheduling` reason**
  before assuming a chart/image bug — "Insufficient cpu" (§4.2) and a real crash (§4.3, or an actual
  application bug) look identical from `kubectl get pods` alone (`Pending`/`CrashLoopBackOff`) but
  need completely different fixes.
- **Rebuild from scratch to rule out stale state** before spending much time on a symptom that
  doesn't match the visible config — several of the NetworkPolicy bugs above were only conclusively
  proven to be *authoring* bugs (not environmental flakiness) by tearing down and recreating the
  cluster and confirming the identical symptom reproduced.
