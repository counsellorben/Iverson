# Critical Implementation Review: 2026-10-06-csr-round10-infrastructure-supply-chain-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-06-csr-round10-infrastructure-supply-chain-implementation-plan.md
**Artifact HEAD at review:** 73816061
**Verified plan-level assumptions section:** present

⚠️ 1 commits since plan-write time (SHA 52275c13); cited file:line references re-checked under §1. (The one commit is `73816061`, the plan itself.)

Method note. The Helm prototype `edit.py` was re-applied to a fresh `git archive HEAD` copy, plus the plan-only text it omits: the umbrella `starrocks.createUser`, and the comments the plan adds in Terraform. Every plan verification command was then run verbatim against that copy: Task 1 Step 7, Task 2 Step 7, Task 3 Step 10, Task 4 Step 5 and Task 10's `assert.py`. The plan's `--model-images` block was spliced into a copy of `build-and-load-image.sh` and dry-run through `docker`/`kind` shims. The Terraform prototype diff, with the plan's comment placement, was run through `fmt`, `init -lockfile=readonly`, `validate` (all four roots), `tfsec` and `terraform graph`. Scratch lives under `scratchpad/cir-d1/`, and every `.terraform` dir was deleted.

## 0. Coverage enumeration

### 0a. Tasks × surfaces

| # | Surface | Disposition |
|---|---|---|
| T1-prose | Task 1 Steps 1–6 anchors (api-ingress comment, admin-ui first three comment lines plus `spec:`, prometheus peer through `{{- end }}`, authentik 9000 through 9080, values comment, overlay paragraphs) | ok [existence]: each anchor read in `networkpolicies.yaml:21-37, 495-522, 578-589, 605-616`; `values-laptop.yaml:13-17` and `values-local.yaml:14-18` both begin `# 172.18.0.0/16 is kind's Docker bridge network`; aws/azure/gcp comment blocks are contiguous `#` lines (`values-aws.yaml:4-6`, `values-azure.yaml:4-7`, `values-gcp.yaml:5-15`) |
| T1-code | Task 1 YAML blocks | ok [compat]: rendered all 5 overlays and kubeconform reports 0 invalid (local 100/94, laptop 79/74, cloud 106/100) |
| T1-cmd | Task 1 Step 7 checker | ok [compat]: run verbatim on all 5 overlays → `allow-all: [] authentik ports: [8443, 9080]` ×5 |
| T2-code | Task 2 helpers, guard, 14-site regex, block A/B | ok [compat]: the regex matched 14; renders validate; the API helper's `- to:` then `  - ipBlock` nests correctly (parsed and validated) |
| T2-cmd | Task 2 Step 7 checker, guards, apiServer fallback | ok [compat]: run verbatim → both guard messages print; the fallback prints exactly `iverson-postgres-egress [443, 6443]` and `iverson-kafka-egress [443, 6443]` |
| T2-spec5a | Task 2 / Task 10: §5a fallback and CIDR assertions | → §2.5 |
| T3-docker | Task 3 Steps 1–2 Dockerfiles | ok [compat]: `curl`, `seq`, `find`, `tail` are present in the TEI image and `sha256sum`, `cut` in the Ollama 0.12.11 image (run); both base images run as root; Ollama's local `RepoDigests` include `sha256:3d8a05e3…` |
| T3-ollama-uid | Task 3: the baked Ollama model is readable by uid 1000 on a read-only root | ok [compat]: ran `ollama:0.12.11` as root and pulled `all-minilm:22m` into `OLLAMA_MODELS=/models`. Files are `644 root` and dirs `755 root` (umask 0022), so uid 1000 can read them |
| T3-chart | Task 3 Steps 3–6 StatefulSet, values and egress edits | ok [compat]: Task 3 Step 10 run verbatim → `iverson-tei-bge-base iverson-tei-model:bge-base-a5beb1e3e68b-c2d6955415bf init: False vct: False data refs: False`, the matching Ollama line, `egress: []` ×2, `syntax-ok`. The suffixes equal `sha256sum … \| cut -c1-12` (`c2d6955415bf`, `e9deef361c4d`) |
| T3-script | Task 3 Step 7 `--model-images` block | ok [compat]: spliced into the real script and dry-run with shims → 2 builds plus tag/save/`kind load image-archive` under `/var/tmp`; with `ollama.enabled: false` → 1 build; `bash -n` ok |
| T3-ps1 | Task 3 Step 8 PowerShell | ok [existence]: static only (no `pwsh`). Each bash step has a counterpart. Staging through `GetTempPath()` is a Windows-host path. Not load-bearing for any check |
| T3-setup | Task 3 Step 9 notes | ok [existence]: `setup.sh:180` and `setup.ps1:102` are the `storageSize` notes |
| T4-code | Task 4 Steps 1–4 image scalars | ok [compat]: rendered all 5 overlays; template image lines at `qdrant/statefulset.yaml:45`, `jaeger:37`, `prometheus:41`, `redis:37`, `authentik deployment-server.yaml:54,84,150`, `deployment-worker.yaml:27`, `job-revoke-cross-db.yaml:32`, `job-create-user.yaml:31` all match the plan |
| T4-cmd | Task 4 Step 5 greps and image list | ok [compat]: run verbatim → `no imageTag left`, `no image pins in overlays`, `no image literals in templates`; the image list equals the plan's expected set |
| T5-ignore | Task 5 Step 1 `.dockerignore` | ok [existence]: root and `Iverson.Server` files read; the patterns are inherited (spec VA40) |
| T5-docker | Task 5 Steps 2–4 Dockerfiles | ok [existence]: `Iverson.Api/Dockerfile:23` and `Launcher/Dockerfile:7` are `COPY . .`; AdminUI lines `:13, :24` match the replacements. node, nginx and TEI digests were re-resolved (§1 row 17) |
| T5-compose | Task 5 Step 5 table and `config -q` | table: ok [totality]. All 20 `image:` lines (`docker-compose.yml:35…608`) are covered: 18 rows plus the two first-party lines. Every digest equals `digests.txt` and the registry. Command: → §2.6 |
| T5-pyc | Task 5 Step 6 | ok [existence]: `git ls-files` lists the `.pyc`; `.gitignore:82` (`__pycache__/`) matches it |
| T5-npm | Task 5 Step 7 | ok [existence]: scripts `build: vite build` and `test: vitest run`; no `.npmrc`/engine-strict, so `EBADENGINE` only warns on host Node 22.16.0 |
| T5-build | Task 5 Step 8 build and console run | ok [totality]. `grep -o '\$\{?[A-Z_]{3,}'` over `docker-entrypoint.sh` yields 8 names; 3 are not external inputs (`ADMIN_API_ORIGIN` is assigned at `:84`, `HSTS_LINE` at `:101/:103`, `NAME` appears only in comments). The remaining 5 are `OIDC_CLIENT_ID`, `OIDC_AUTHORITY` and `API_BASE_URL` (`validate`d at `:80-82`), `EXTERNAL_SCHEME` (`:100`) and `OIDC_ORIGIN` (`:112`). The plan's `docker run -e` list (plan:861-862) sets exactly these 5 |
| T6-ci | Task 6 Steps 1, 3, 5 | ok [compat]: the plan's `image-scan.yml` was extracted verbatim and `admin-ui.yml` re-pinned in a scratch copy. The YAML parse prints `yaml ok`; the SHA grep prints `every uses: is SHA-pinned`. The third-party `sed` extraction, run on cloud renders, yields 10 images with no `iverson-*` |
| T6-dependabot | Task 6 Step 2 entries | ok [totality]: tracked Dockerfiles are exactly AdminUI, Api, Events, Launcher, Sql and Vector (`git ls-files`), plus 2 new ones. The 6 terraform lockfile roots and 13 Chart.yaml dirs are covered by the entries |
| T6-ps1 | Task 6 Step 4 `--version` count | ok [compat]. Applied Step 4 to a scratch copy of `setup.ps1`: one `--version <v> \`` line after each of the 7 `--repo` lines, with the versions from `setup.sh`. Then `grep -c -- '--version'` = 7 (it was 0 on `HEAD`) |
| T7-code | Task 7 Steps 1–5 | ok [compat]: prototype diff plus the plan's comments gives `fmt-ok`; `validate` "Success!" for azure and bootstrap/azure; `tfsec` "No problems detected!" |
| T7-wiring | Task 7: spec §3a "The operators module depends on it" | → §2.2 |
| T7-cmd | Task 7 Step 6 validate loop | → §2.8 |
| T8 | Task 8 Steps 1–4 | ok [compat]: `fmt-ok`; `validate` Success for aws and gcp; `launch_template.version = …latest_version` (`cluster-aws/main.tf:570`), so the runbook's "nodes roll" claim holds |
| T9-runbook | Task 9 sections 1–8 | §3 PVC label: ok [existence]. StatefulSet PVCs inherit the selector `matchLabels` (`app: <release>-ollama`, `app: <release>-tei-<slug>`). §4 `getent`: ok [compat]. `docker run mcr.microsoft.com/dotnet/aspnet:10.0` → `/usr/bin/getent`, `ubuntu 24.04`. §7 ordering sentence: → §2.3. §8: ok [existence]. `cluster-aws/main.tf:568-571` sets `launch_template.version = …latest_version`, so a template change rolls the node group (T8) |
| T10-S1 | §5a assert.py and kube-score | ok [compat]: `PASS` ×5 on new renders; criticals base→new 21/21, 18/18, 22/22 ×3; `comm` added 0 and removed 0. Assertion coverage: → §2.5 |
| T10-S2/S3 | §5d, §5e | S2 → §2.8 (reuses the T7 loop); S3 ok [compat] (T6-ci) |
| T10-S4a | Build-context canary | → §2.4 |
| T10-S4b/c | Wrong-digest build; local trivy | ok [existence]: `aquasec/trivy:0.75.0` digest re-resolved `af6acf9a…`; the `FATAL` text matches the Dockerfile's `echo` |
| T10-S5 | Pass 1 deploy, probes, gRPC control, port-forward | Probe design: → §2.1. App-image rebuild: → §2.7. State record: → §2.9. gRPC control: ok [existence]. Rendered Ingress `iverson.local` carries `/iverson.ObjectMappingService` → `iverson-api:8080` with `backend-protocol: GRPC` and TLS `iverson-api-tls`; the Authentik Ingress maps `authentik.iverson.local` → `iverson-authentik:9080`; the secret `iverson-authentik-loadtest-client` has keys `client-id`/`client-secret` |
| T10-S6 | Pass 2 | Probe design: → §2.1. App-image rebuild: → §2.7. Positives: ok [compat] (pass-2 render). `api-egress` gains `iverson-prometheus:9090`; `prometheus-ingress` admits only `app=iverson-api`; the admin-ui Ingress `/admin(/|$)(.*)` has `ssl-redirect: "false"` |
| T10-S7 | Teardown diff | → §2.9 |

### 0b. Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | T1 → T2: the overlays' `networkPolicy:` block; T2 inserts `apiServerCidrs` under `clusterCidrs` | ok [compat]: rendered (T2-cmd) |
| C2 | T2 → T3: T3 deletes T2's `include` lines in `tei-egress`/`ollama-egress` | ok [compat]: final render `egress: []` |
| C3 | T3 → T3 script → T10 S5: the script's rendered tags equal the install's tags (both `helm dependency build` first, same `-f values-laptop.yaml`) | ok [compat]: dry-run tags equal the `helm template` tags (`…-c2d6955415bf`, `…-e9deef361c4d`) |
| C4 | T3 → T6: the model-image dirs exist before the Dependabot entries name them | ok [existence]: task order 3 < 6 |
| C5 | T5 S8 → T10 S4c: `csr10d-api:check`/`csr10d-adminui:check` (persisted local images) | ok [existence]: produced at plan:859-860 and consumed by `docker save` at plan:1400 |
| C6 | T5 S8 → T10 S5/S6: "Keep `csr10d-api:check` for Task 10, where it is retagged" (plan:866) | → §2.7. Task 10 never retags; it rebuilds uncapped |
| C7 | T10 S5 `before-*.txt` → T10 S7 diffs (persistence boundary) | → §2.9 |
| C8 | T7 → T9: runbook §6 and §7 describe Terraform's ordering | §6 ok [existence]; §7 → §2.3 |
| C9 | T2 S7 → T10 S1: "guard and fallback checks re-pass" | ok [compat]: re-run (T2-cmd) |

### 0c. Rule-like content (both directions)

| # | Rule | Disposition |
|---|---|---|
| R1 | Task 2 DNS regex (14 sites) | over: ok [totality]. Exactly 14 substitutions, and every match is a `to: [] # DNS` + 53 pair. / under: ok [totality]. `grep 'port: 53'` on the original file finds 14, and after substitution no `to: []` 53 rule remains |
| R2 | Task 10 `assert.py` | over (false-fails a correct render): ok [compat], PASS ×5. / under (passes a wrong render): → §2.5. It only rejects unexpected ingress CIDRs, never requires the expected ones. It checks only that API rules are ipBlock-shaped. It never exercises `dnsAnyDestination` |
| R3 | Task 6 image-scan `sed`/`grep -v (^|/)iverson-` | over: ok [compat]. No `iverson-*` survives. / under: ok [compat]. All 10 third-party refs in the cloud renders are extracted, including the 4-space-indented StarRocks CR lines |
| R4 | Task 3 awk/sed render parse | over: ok [compat]. One TEI tuple per `embeddingModels` entry, one Ollama image; `iverson.io/model:` does not match `model-digest:`. / under: ok [compat]. Ollama disabled gives 1 build |
| R5 | Task 6 SHA-pin grep | over: ok [compat]. Passes the pinned `@<40hex> # vX` form. / under: ok [compat]. It flags the 3 current `@v7` uses before the fix (run on HEAD) |
| R6 | Task 10 S4a canary set × builds | over (false-fail, i.e. a canary reported on a correct build): ok [compat]. In a canary build using the plan's `.dockerignore`, only non-canary files reached the image (see §2.4's Evidence run). / under (false-pass, i.e. a broken exclusion goes unreported): → §2.4. No canary sits under a console `COPY` source or inside a copied directory (matrix below) |
| R7 | Task 10 S5/S6 probe-pod checks | over (false-fail, i.e. a correct deploy reported broken): → §2.1. `dns via kube-dns ok` can never print from the egress-less outsider. / under (false-pass, i.e. a regressed ingress rule reported refused): → §2.1. The outsider's own default-deny egress makes every refusal pass on `main` too. `curl` to the h2c-only port prints `000` even when admitted (matrix below) |

### 0d. Population matrices (closure for confirmed findings)

- **§2.1 probe checks (8 cells):**
  - Outsider → api 8080, api 8081, authentik 9000, authentik 9080 (pass 1): → §2.1.
  - Outsider DNS positive: → §2.1.
  - Outsider outside-resolver negative: → §2.1.
  - Outsider → prometheus 9090, admin-ui 8080 (pass 2): → §2.1.
  - apiprobe → TEI 8080 and Ollama 11434: ok [compat]. `api-egress` admits `iverson.io/component=tei` on 8080 and `app=iverson-ollama` on 11434, plus the kube-dns helper (render dump).
  - apiprobe → prometheus 9090: ok [compat] (pass-2 render).
  - teiprobe and ollamaprobe egress-blocked: ok [compat]. `tei-egress` and `ollama-egress` render `egress: []`. The check is falsifiable: `main` has `to: []` on 53 and 443 (`networkpolicies.yaml:458-462, 487-493`).
  - Host → ingress-nginx (gRPC, `/admin`): ok [compat]. These go from the host through the kind `hostPort` to ingress-nginx, which is admitted by namespace on api 8080, admin-ui 8080 and Authentik 9080 in the pass-1 and pass-2 renders. No probe pod's egress is involved.
- **§2.4 canaries × builds (4 × 2 cells):**
  - Api: the root `.env`, `Iverson.Server/.env.canary`, `.worktrees/x/.env` and `.claude/w/f` canaries are each falsifiable, ok [existence]. `main`'s `COPY . .` would carry all four.
  - Console: all four → §2.4. No console `COPY` source (`AdminUI/Dockerfile:15,17,18`) contains any of them, on `main` or after.
  - `.dockerignore` entries × builds: `**/.env` and `**/.env.*` inside the copied dirs → §2.4 (no canary there).
  - `docs/`, `.claude/` and `.worktrees/` entries: n/a. After Task 5, no root-context `COPY . .` remains for them to guard.
  - `Iverson.Server`-context builds: n/a. Spec §5c names only the Api and console.
- **§2.2 spec-stated Terraform ordering edges (3 cells):**
  - Cluster → both control-plane Network Contributor assignments: ok [compat]. `depends_on` is present.
  - Container → `deployer_state_data`: ok [compat] (graph edge).
  - Operators → `deployer_cluster_admin`: → §2.2.
- **§2.3 runbook ordering claims (4 cells):**
  - §3 upgrade order: ok [existence]. plan:1275-1277 is an operator command sequence (delete StatefulSets, delete PVCs, `helm upgrade`) and claims no Terraform ordering.
  - §6: ok [existence]. plan:1293's "if the operators module fails with 403 right after the role assignment, wait five minutes and re-apply" holds with or without §2.2's `depends_on`: role propagation can take up to five minutes after creation (spec:115), and §2.2's graph run shows the dependency is missing today.
  - §7: → §2.3.
  - §8: ok [existence]. `cluster-aws/main.tf:568-571` uses `latest_version`, so the hop-limit change rolls the nodes.
- **§2.8 `validate | tail -1` sites (3 cells):** Task 7 Step 6 (plan:1196), Task 8 Step 5 (reuses it) and Task 10 Step 2 (reuses it): all → §2.8. `tfsec | tail -2`: ok [compat]. `od` shows `No problems detected!`.
- **§2.9 state-diff files (4 cells):**
  - containers: → §2.9. Raw `docker ps -a` vs `--format '{{.Names}}' | sort`.
  - volumes: → §2.9, sort only. `-q` matches but is unsorted against a sorted compare.
  - networks: → §2.9. Raw vs `--format '{{.Name}}' | sort`.
  - images: → §2.9. Format mismatch, plus the `kindest/node` addition.
- **§2.5 spec §5a assertions (12 cells):**
  - no `from: []`/`to: []` at defaults: ok [compat]. `assert.py`'s `from == []` and `to == []` checks (plan:1327, 1330) PASS ×5 on the new renders.
  - `dnsAnyDestination`: → §2.5.
  - `apiServerAnyDestination`: ok [compat]. Task 2 Step 7's filter run verbatim prints only `iverson-postgres-egress [443, 6443]` and `iverson-kafka-egress [443, 6443]`.
  - DNS rules use the helper: ok [compat]. `assert.py` requires `to == DNS` on every 53 rule (plan:1332); PASS ×5.
  - API rules use `apiServerCidrs`: → §2.5 (under).
  - TEI/Ollama have no egress: ok [compat]. `assert.py` (plan:1334-1335) PASS ×5; Task 3 Step 10 prints `egress: []` ×2.
  - `clusterCidrs` per §1a: → §2.5 (under).
  - exact tags and derived model names: ok [compat]. `assert.py`'s regex and floating-tag checks (plan:1345-1349) PASS ×5. The Task 4 Step 5 image list has only exact tags.
  - guards: ok [compat]. Task 2 Step 7 printed both guard messages.
  - no third-party image in overlays or template literals: ok [compat]. Task 4 Step 5 printed `no image pins in overlays` and `no image literals in templates`.
  - no `/data`: ok [compat]. Task 3 Step 10 prints `data refs: False` ×2, and `assert.py`'s `/data` check PASSes ×5.
  - tag tracks the Dockerfile: ok [compat]. Task 3 Step 10's suffixes `c2d6955415bf`/`e9deef361c4d` equal `sha256sum | cut -c1-12`; an edit plus `dependency build` moved `e9deef361c4d` to `77f60a87578b` (§1 row 10).
- **§2.7 app-image builds in Task 10 (2 cells):** Step 5 api (plan:1416) and Step 6 admin-ui (plan:1477): both → §2.7.

### 0e. Dropped candidates

| # | Candidate | Disposition |
|---|---|---|
| D1 | The bootstrap's create-time data-plane read of a key-disabled account might need a data role | dropped. It needs an Azure authorization fact I cannot probe without a cloud. The spec accepts static-only Terraform verification. The existing-account variant of the same question is carried as an UNVERIFIED differentiator inside §3.1, not as its own finding |
| D2 | `setup.ps1`'s ingress-nginx lacks `setup.sh`'s snippet/risk-level flags | dropped. This exists already on `main` and pinning does not change it. Spec §4c asks only for version pins |
| D3 | Task 10 Step 1's `git worktree add` base checkout is never removed | dropped. Housekeeping only; no outcome breaks |
| D4 | Dependabot will raise two PRs per chart image (umbrella and subchart pins) | dropped. This is spec §4d's chosen layout, a design matter |
| D5 | A Windows/`pwsh` `GetTempPath()` stage dir differs from bash's `/var/tmp` | dropped. Windows temp is on disk; the RAM-backed `/tmp` concern is WSL-only |
| D6 | The `ollama pull` 3-second start race in the Dockerfile | dropped. Speculative; the prototype build passed (spec VA22) |

### 0f. UNVERIFIED residues (each flows to §3)

| # | Residue | Disposition |
|---|---|---|
| U1 | §2.3's mechanism for granting the data role before keys go off on an existing account | UNVERIFIED: no cloud account → §3.1 |
| U2 | §3.1(a): Azure rejects a duplicate role assignment, and azurerm 3.117.1 imports one by full ID | UNVERIFIED: no cloud account → §3.1 (an option's differentiator) |
| U3 | §3.1(b): both applies complete on a real account | UNVERIFIED: only `validate` was run → §3.1 |
| U4 | §3.1(c) and D1: whether the refresh or read of a key-disabled account and its container succeeds under Entra auth before the data role exists | UNVERIFIED: no cloud account → §3.1 |
| U5 | §2.7 fix: the kind node resolves the retagged `docker.io/library/iverson-{api,admin-ui}:csr10d` | UNVERIFIED: no kind cluster in-round. Not load-bearing (same load path as `build-and-load-image.sh:63-66`) → §3 note |

## 1. Verified-plan-assumptions cross-check

1. **New paths** — still holds. [absence] `ls` → "No such file or directory" for all four paths.
2. **Overlays and ci-overrides; CI renders each** — still holds. [existence] `deploy-validate.yml:28-40` and `.gitlab-ci.yml:39-45` read; all 8 files exist.
3. **`validateNoPlaceholders` at `_validate.tpl:23`, included at `networkpolicies.yaml:1`** — still holds. [existence] read.
4. **Subchart `$.Files.Get` inside `range` renders the Dockerfile hash** — still holds. [compat] Rendered `bge-base-a5beb1e3e68b-c2d6955415bf`, equal to `sha256sum … | cut -c1-12`.
5. **Helm has `sha256sum`/`trunc`/`replace`/`fail`/`required`** — still holds. [compat] Local v3.16.4 renders. CI's v3.15.0 (`deploy-validate.yml:23`) is [existence], recorded and not re-run.
6. **Prototype renders 5 overlays with 0 invalid; §5a asserts pass** — still holds. [compat] Re-run: local 100/94, laptop 79/74, aws/azure/gcp 106/100; plan `assert.py` PASS ×5.
7. **No new kube-score critical** — still holds. [compat] Re-run against a fresh `HEAD` render: 21/21, 18/18, 22/22 ×3; `comm` added 0 and removed 0.
8. **Guard messages** — still holds. [compat] Both strings printed by the plan's own commands.
9. **Fallback scoping** — still holds. [compat] `apiServerAnyDestination` → postgres and kafka 443/6443 only. `dnsAnyDestination` → the set of `to: []` port tuples is exactly `{(TCP,53),(UDP,53)}`.
10. **The Dockerfile edit plus `dependency build` changes the suffix; a stale `.tgz` does not** — still holds for the load-bearing half. [compat] The edit followed by `dependency build` moved `e9deef361c4d` to `77f60a87578b`. The parenthetical "a stale `.tgz` does not" is not deterministic: 12 renders with a stale tgz present gave 7 of the edited hash and 5 of the old one. Helm loads both `charts/ollama/` and `charts/ollama-0.1.0.tgz`. Not load-bearing: every plan render site runs `dependency build` first. The sites are T1 S7, T2 S7 (via T1 S7), T3 S7 script, T3 S10, T4 S5, T10 S1, T10 S5, T10 S6 (no Dockerfile change since S5), CI `deploy-validate` and `image-scan`.
11. **`egress: []` validates** — still holds. [compat] kubeconform 0 invalid.
12. **The script parse yields TEI/Ollama inputs; Ollama disabled gives 1 build** — still holds. [compat] Shim dry-run: 2 builds, then 1.
13. **`kind load image-archive` exists; `/var/tmp` ext4, `/tmp` tmpfs** — still holds. [compat] kind v0.24.0 help text; `df -hT`.
14. **Terraform edits pass fmt, validate and tfsec** — still holds. [compat] Re-run with the plan's comments: `fmt-ok`; Success ×4 (aws, azure, gcp, bootstrap/azure); `No problems detected!`.
15. **Two `identity` blocks at `:91` and `:175`** — still holds. [existence] read.
16. **`aws_vpc.this` at `cluster-aws/main.tf:14`** — still holds. [existence] read.
17. **Base image index digests** — still holds. [compat] `digest.sh` re-resolved TEI `8de25e75…`, node `0a7108bf…`, nginx `15c994d1…`, postgres `65b16a8b…` and trivy `af6acf9a…`. Ollama `3d8a05e3…` is in the local `RepoDigests`.
18. **trivy-action v0.36.0 = `ed142fd0…`; inputs** — still holds. [compat] GitHub API: annotated tag `a9c7b0f0…` resolves to commit `ed142fd0…`. `action.yaml` at that SHA has `image-ref`, `exit-code`, `severity`, `ignore-unfixed`, `version`, `format` and `scan-type`.
19. **setup-node v7.0.0 and setup-helm v5.0.1 SHAs** — still holds. [compat] GitHub API `820762786…` and `3d3c42e5…`; `deploy-validate.yml:21` read.
20. **No actionlint; PyYAML 6.0.3** — still holds. [compat] `which` is empty; `yaml.__version__` is 6.0.3.
21. **Host Node v22.16.0** — still holds. [compat] `node -v`.
22. **The setup notes and the at-rest runbook subset** — still holds. [existence] `setup.sh:180`, `setup.ps1:102`; `at-rest-encryption-verification.md:70-85, 143-152` is a subset check.
23. **No tfvars** — still holds. [negative] `git ls-files | grep -c .tfvars` = 0.
24. **Every Helm caller's overlays set `apiServerCidrs`** — still holds. [negative] The callers grep finds deploy-validate (5 overlays), GitLab (4), image-scan (3 cloud), the `--model-images` script (`--values`, laptop default) and Task 10. Terraform deploys no `helm/iverson` chart (grep empty).
25. **`vpc_id` default null keeps the three operators call sites valid** — still holds. [compat] aws, azure and gcp validate.
26. **The LoadTest lag report connects to Kafka** — still holds. [existence] `WritePathRunner.cs:219-238`.
27. **gRPC through kind ingress needs TLS ALPN h2 on 8443** — still holds. [existence] Recorded from the 2026-09-24 spec; needs a cluster, not re-run.
28. **The compose stack is not running; 8080/8443 free** — still holds. [compat] `docker ps` shows every `iverson*` container Exited; `ss -ltn` has no match.

**Span check: uncovered dependencies.** Each is verified in-round and routed:
- A probe pod "outside each allowed set" has no egress at all under `default-deny`: → §2.1.
- `docker compose config` needs the untracked `Iverson.Server/.env`, absent from a fresh worktree: → §2.6.
- `terraform validate`'s output ends in a blank line: → §2.8.
- `kind create` pulls a node image that is not present locally: → §2.9.
- The AKS operators' ordering against the deployer role assignment: → §2.2.
- One apply cannot order "role before keys off" for an existing state account: → §2.3 and §3.1.
- The Inherited-from-spec gap behind §2.1: spec §5b assumes, without stating it, that a probe pod can originate traffic.

## 2. Literal-wrongness findings

1. **Task 10 Steps 5–6: the "outsider" probe has no egress. Its DNS positive can never print, and its refusal checks pass whatever the ingress policies say.**
   - A pod labelled `app=csr10d-outsider` is selected only by `iverson-default-deny` (`podSelector: {}`, `policyTypes: [Ingress, Egress]`). No other policy selects it.
   - So `nslookup kubernetes.default…` from `outsider` (plan:1438) fails, and the expected `dns via kube-dns ok` (plan:1455) never appears.
   - `outside resolver blocked` and every `outsider -> … refused/timeout` line (plan:1436-1437, 1484-1485) are produced by the outsider's own missing egress, not by the target's ingress rule.
   - On `main` the same checks also "refuse" Authentik 9000/9080 and admin-ui 8080, although those rules are `from: []` there (`networkpolicies.yaml:514, 605, 615`). The checks therefore cannot detect a Task 1 regression, which is what spec §5b requires them to show (spec:206, 214).
   - Secondary: `curl http://iverson-api:8080/` (HTTP/1.1 against the h2c-only port) prints `000` and falls through to `refused/timeout` even when the connection is admitted.
   - Evidence: [totality] Rendered every laptop NetworkPolicy's `podSelector` (24 policies); only `iverson-default-deny` matches `app=csr10d-outsider`. [existence] `api-egress`'s only port-53 rule is the kube-dns helper (render dump).
   - **Proposed fix.**
     - Before the outsider checks in each pass, apply a throwaway `NetworkPolicy` `csr10d-outsider-egress`: `podSelector: {matchLabels: {app: csr10d-outsider}}`, `policyTypes: ["Egress"]`, `egress: [{}]`. Delete it with the probe pods.
     - Replace each outsider `curl` with a protocol-independent connect probe: `kubectl -n iverson exec outsider -- nc -z -w 5 <svc> <port> && echo "outsider -> <svc>:<port> ADMITTED (fail)" || echo "outsider -> <svc>:<port> refused/timeout"`.
     - Move both DNS checks to after `probe apiprobe …` and run them from `apiprobe` (`app=iverson-api`, whose only DNS rule is the kube-dns helper): `kubectl -n iverson exec apiprobe -- nslookup kubernetes.default.svc.cluster.local` must succeed, and `… nslookup example.com 8.8.8.8` must fail.
     - Keep the expected lines; the outsider expectations become "refused/timeout" for every target.
   - Evidence:
     - [compat] `curlimages/curl:8.18.0` ships BusyBox `nc` with `-z -w` and `nslookup`. Run: `nc -z -w 3` → `closed-rc=1` on a closed port, `open-rc=0` on a listening one.
     - [existence] `egress: [{}]` is the NetworkPolicy allow-all-egress form. No cluster run was done; enforcement of per-policy egress on this Calico is inherited (spec VA4, VA16).
     - [existence] Falsifiability after the fix: on `main` the outsider would be admitted to Authentik 9000/9080 and admin-ui 8080 (`from: []` at `networkpolicies.yaml:514, 605, 615`), and `apiprobe` would reach 8.8.8.8 (`to: []` on 53 at `:190` and peers).

2. **Task 7: spec §3a's "The operators module depends on it" is not implemented, so the first Azure apply always races the deployer's role assignment.**
   - Spec:115 says the operators module depends on `azurerm_role_assignment.deployer_cluster_admin`.
   - The plan adds the assignment (plan:1022-1026) but no dependency. `module "operators"` reads only `module.cluster.cluster_name` and `data_volumes_des_id`, and the providers read only `host`/`cluster_ca_certificate`. All of these depend on the cluster, not the assignment.
   - With local accounts disabled, the operators' Kubernetes and Helm resources can be applied before the deployer holds any Kubernetes role. The runbook's "wait five minutes and re-apply" (plan:1293) then becomes the normal path rather than a propagation fallback.
   - Evidence: [negative] Ran `terraform graph` on the prototype azure root (backend block stripped). `module.operators.helm_release.cloudnative_pg` and `module.operators.kubernetes_namespace.iverson` reach `azurerm_kubernetes_cluster.this` (True) but not `deployer_cluster_admin` (False).
   - **Proposed fix.** In Task 7 Step 4, add `depends_on = [module.cluster]` to `module "operators"` in `azure/main.tf`, with the comment "The deploying principal's Kubernetes RBAC role must exist before any operator resource." Add it to the Step 4 bullets and the Step 7 file list (already listed).
   - Evidence: [compat] Scratch run: `fmt-ok`; `validate` "Success!"; `terraform graph` then shows both operator resources reaching `deployer_cluster_admin` (True). [negative] `grep '^data '` over `modules/operators/*.tf` is empty, so the module-level `depends_on` defers no data source.

3. **Task 9 runbook §7 states an ordering the configuration does not produce, and drops the spec's instruction.**
   - Plan:1298 says "The data role assignment is created before shared keys are disabled."
   - `deployer_state_data` takes `scope = azurerm_storage_account.state.id`, so for an existing account one apply updates the account first (keys off, network deny) and creates the role afterwards.
   - Spec:137 instead requires the runbook to have the operator "grant the data role and add the operator's IP before disabling shared keys". The plan's runbook replaces that instruction with the false sentence.
   - Evidence: [compat] `terraform graph` on the prototype bootstrap shows `"azurerm_role_assignment.deployer_state_data" -> "azurerm_storage_account.state"` and `"azurerm_storage_container.state" -> "azurerm_role_assignment.deployer_state_data"`.
   - **Proposed fix.** Delete the sentence. State the true order ("in one apply, Terraform disables shared keys before it creates the data role"). Then give the spec's existing-account ordering as an explicit operator step, using the mechanism the user picks in §3.1.
   - Evidence: [compat] The graph edges above. The mechanism is UNVERIFIED and routed to §3.1.

4. **Task 10 Step 4(a): the build-context canary cannot fail for the console, and exercises none of Task 5's `**/.env*` entries.**
   - The canaries are placed at `.env`, `Iverson.Server/.env.canary`, `.worktrees/x/.env` and `.claude/w/f` (plan:1378).
   - The console build copies only `Iverson.AdminUI/package*.json`, `Iverson.AdminUI/` and `Iverson.Clients/Common/Proto` (`Iverson.AdminUI/Dockerfile:15, 17, 18`). No canary can reach it on `main` either, so `ui-canaries-done` with no paths proves nothing.
   - The Api check is falsifiable only through Task 5's explicit `COPY` list. No canary sits inside a copied directory, where the new `.dockerignore` lines are the only guard.
   - Spec §5c (spec:217) asks the canary to show `.env` files "never reach the Api and console build stages". CDR-1 R12 relies on "§5c's canary check" to cover `.env` content inside the nine copied directories.
   - Evidence: [negative] `grep -n '^\s*COPY' Iverson.AdminUI/Dockerfile` lists the build-stage sources `:15` `Iverson.AdminUI/package.json Iverson.AdminUI/package-lock.json`, `:17` `Iverson.AdminUI/` and `:18` `Iverson.Clients/Common/Proto`. The rest (`:34, :39, :40`) are a `--from=build` copy plus `nginx.conf` and `docker-entrypoint.sh`. None of the 4 canary paths (`.env`, `Iverson.Server/.env.canary`, `.worktrees/x/.env`, `.claude/w/f`) lies under any of them.
   - **Proposed fix.**
     - In plan:1378, also write `echo CANARY > Iverson.AdminUI/.env.local && echo CANARY > Iverson.Server/Iverson.Api/.env`.
     - After the console build, add `docker run --rm --entrypoint sh csr10d-ctx-ui -c 'test ! -e /src/.env.development && echo tracked-env-excluded'`. The tracked `Iverson.AdminUI/.env.development` must be dropped by `**/.env.*`.
     - Expected: no paths before either `*-canaries-done`, plus `tracked-env-excluded`.
   - Evidence:
     - [compat] Ran a canary build: a trivial `alpine` Dockerfile (`COPY Iverson.AdminUI/ /src/` and `COPY Iverson.Server/Iverson.Api/ /api/`, then `find`) over a scratch context holding the real `Iverson.AdminUI/.env.development`, the two proposed canaries and one ordinary file per directory.
       - With `HEAD`'s root `.dockerignore`, the image contained `/api/.env`, `/src/.env.development` and `/src/.env.local`, so the new canaries are caught on `main`.
       - With the plan's additions (`**/.env`, `**/.env.*`, `.claude/`, `.worktrees/`, `docs/`) appended, only `/api/Program.cs` and `/src/src/main.ts` remained. So `**/.env.*` drops `.env.development` and the in-directory canaries are excluded.
       - The probe images were removed afterwards.
     - [existence] `git ls-files` shows `.env.development` is the only tracked `.env*` in the console.
     - [compat] Host `node_modules` grep for `CANARY` is empty, so the `/` grep has no in-tree false hit.

5. **§5a assertions the plan's checkers omit or check in one direction only.**
   - Spec §5a (spec:199) lists three assertions the plan's checkers do not deliver:
     - "Setting `networkPolicy.dnsAnyDestination` … renders `to: []` for exactly its own rules": no plan step renders with `dnsAnyDestination=true`. Task 2 Step 7 and Task 10 Step 1 cover only `apiServerAnyDestination`.
     - "API-server rules use `apiServerCidrs`": `assert.py` and the Task 2 checker test only `all("ipBlock" in p …)`.
     - "`clusterCidrs` per 1a": `assert.py` rejects unexpected CIDRs but never requires the expected ones.
   - A render with the wrong CIDR list, or with an admin-ui or Authentik 9080 rule missing its `clusterCidrs` range, passes.
   - Evidence: [compat] `assert.py` PASS ×5 on the new renders, with no assertion comparing any CIDR list for equality (read plan:1320-1333).
   - **Proposed fix.**
     - Add to Task 2 Step 7 the same python filter as the apiServer fallback, over `--set networkPolicy.dnsAnyDestination=true`. Expected: every printed rule is `[53, 53]`, and no other port appears.
     - Add to `assert.py` an `API` table (each overlay's `apiServerCidrs`, as in Task 2 Step 6). Require every 443+6443 rule's ipBlock CIDRs to equal it.
     - For each of `iverson-api-ingress` 8080 and 8081, `iverson-admin-ui-ingress` 8080 and `iverson-authentik-ingress` 9080, require the sorted ipBlock CIDRs to equal `EXPECT`.
   - Evidence: [compat] Implemented these two-direction checks in scratch (`cir-d1/assert-add.py`). They PASS on all 5 new renders and FAIL on all 5 `HEAD` renders (`iverson-postgres-egress: API cidrs []`). `dnsAnyDestination=true` renders the `to: []` port set `{(TCP,53),(UDP,53)}` only.

6. **Task 5 Step 5's compose check fails in the plan's worktree.**
   - `docker compose -f Iverson.Server/docker-compose.yml config -q` interpolates `${AUTHENTIK_SECRET_KEY:?…}` and eight other required variables (`docker-compose.yml:119, 339…370`). These come from the untracked `Iverson.Server/.env`.
   - That file is ignored (`.gitignore:60`), so the fresh worktree `.worktrees/csr10-infra-supply-chain` does not have it.
   - The command exits 1 and `compose-ok` never prints.
   - Evidence: [compat] In a `git archive` copy without `.env`: "required variable AUTHENTIK_SECRET_KEY is missing a value", rc=1.
   - **Proposed fix.** Use `docker compose -f Iverson.Server/docker-compose.yml config --no-interpolate -q && echo compose-ok`. It validates structure and image syntax without secrets and starts nothing.
   - Evidence: [compat] Same copy: `--no-interpolate -q` → `compose-ok`. A copy with one `image:` line broken to `[broken` is rejected (rc=1, "did not find expected ','"), so the check still fails on a malformed edit.

7. **Task 10 Steps 5–6 rebuild the app images without a memory cap, against the spec's constraint and Task 5's own handoff.**
   - Spec global constraint (spec:20): "Image builds run with `--memory` caps."
   - `build-and-load-image.sh:49` runs `docker build` with no `--memory`.
   - Step 5 (plan:1416) uses it for the Api image before the cluster deploy. Step 6 (plan:1477) uses it for the console while the full laptop deployment is running.
   - Task 5 Step 8 says to "Keep `csr10d-api:check` for Task 10, where it is retagged" (plan:866), but Task 10 never retags.
   - Evidence: [existence] `build-and-load-image.sh:49` read; plan:866, 1416 and 1477 read.
   - **Proposed fix.**
     - Step 5: replace the app build with `docker tag csr10d-api:check docker.io/library/iverson-api:csr10d && TMPDIR=/var/tmp kind load docker-image docker.io/library/iverson-api:csr10d --name iverson`.
     - Step 6: replace it with `docker tag csr10d-adminui:check docker.io/library/iverson-admin-ui:csr10d && TMPDIR=/var/tmp kind load docker-image docker.io/library/iverson-admin-ui:csr10d --name iverson`.
     - Tasks 6–9 change no Api or console source, so Task 5's images are current. Step 7's `rmi` list already names `docker.io/library/iverson-{api,admin-ui}:csr10d`.
   - Evidence:
     - [negative] Grepped the plan's Task 6–9 text (plan:870-1305) for `Iverson\.(Server/Iverson\.[A-Za-z]+|AdminUI)`. There are 8 hits, none of them an edit to a source.
       - Six are Dependabot `directories:` entries (plan:885-890).
       - Two are the image-scan matrix's `dockerfile:` values (plan:924-925), which build those Dockerfiles but do not modify them.
       - The tasks' `Modify:`/`Create:` lists name only `.github/*`, `deploy/kind/setup.ps1`, `deploy/terraform/**` and `docs/runbooks/…`.
     - [compat] Dry-ran the replacement logic (`set -euo pipefail`, `docker tag …`, `TMPDIR=/var/tmp kind load docker-image …`) through the `docker`/`kind` shims: `syntax-ok`, and it emits exactly the tag-then-load pair of `build-and-load-image.sh:63-66`.
     - UNVERIFIED [compat]: that the kind node then resolves the retagged image for the pod. No kind cluster was allowed in-round. Not load-bearing: it is the same `docker.io/library/…` plus `kind load docker-image` path the existing script already uses, and if it misbehaves the plan's own rebuild remains as a fallback. Routed to §3 as U5.

8. **Task 7 Step 6's validate loop never prints its expected "Success!" lines.**
   - `terraform validate -no-color` ends with a blank line, so `| tail -1` prints an empty line.
   - The same loop is reused by Task 8 Step 5 and Task 10 Step 2 (3 sites).
   - Evidence: [compat] `od -c` of `validate -no-color` output is `…is valid.\n\n`, and `| tail -1` yields `\n`.
   - **Proposed fix.** Change `| tail -1` to `| head -1` at plan:1196. Task 8 Step 5 and Task 10 Step 2 inherit it.
   - Evidence: [compat] Same `od` dump: line 1 is `Success! The configuration is valid.`

9. **Task 10 Steps 5 and 7: the before/after state contract is unspecified at the producer, and "all four `*-unchanged`" cannot hold for images.**
   - Step 5 says to record "`docker ps -a`, `docker volume ls -q`, `docker network ls`, `docker images` into `$SCR/csr10d/before-*.txt`" (plan:1409), with no file names, no `--format` and no sort.
   - Step 7 diffs `before-containers.txt`, `before-volumes.txt`, `before-networks.txt` and `before-images.txt` against `--format`-ed, sorted listings (plan:1504-1507). Recorded as written, three of the four differ in format, and all four in ordering.
   - Independently, `kind create cluster` pulls a `kindest/node` image that is not present locally. `images-unchanged` therefore cannot print, and the carve-out ("dangling build stages") does not cover that pull.
   - Evidence: [absence] The full named-image listing (`docker images`, 213 images) has no `kindest/node` entry. [existence] plan:1409 vs 1504-1507 read.
   - **Proposed fix.**
     - In Step 5, record with the exact Step 7 commands:
       - `docker ps -a --format '{{.Names}}' | sort > …/before-containers.txt`
       - `docker volume ls -q | sort > …/before-volumes.txt`
       - `docker network ls --format '{{.Name}}' | sort > …/before-networks.txt`
       - `docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > …/before-images.txt`
     - In Step 7, for images use `comm -23 before-images.txt <(docker images … | sort)` and expect empty output (nothing the user had is gone or re-pointed). Separately list `comm -13` additions and confirm each is a throwaway from this task, such as the `kindest/node` image and digest-pinned bases.
   - Evidence: [existence] The commands are Step 7's own. [compat] `comm` run on sorted toy listings (before `a id1`, `b id2`, `c id3`; after `a id1`, `b id9`, `d id4`): `comm -23` printed `b:1 id2` and `c:1 id3`, which are the re-pointed and removed entries. `comm -13` printed `b:1 id9` and `d:1 id4`, which are the additions.

## 3. Forced decisions

1. **How the runbook achieves spec §3c's "grant the data role before disabling shared keys" for an existing state account.**
   - Why forced: §2.3 shows one apply cannot produce that order. The role assignment's `scope` references the storage account, so Terraform updates the account first ([compat] graph edge). The spec mandates the order (spec:137), so the runbook must pick a mechanism. Each available one trades config surface, manual steps or unverified provider behaviour.
   - Why no option dominates: (a) keeps the reviewed configuration identical to the spec but adds manual Azure CLI and import steps. (b) automates the order but adds a variable the spec does not list and needs two applies. (c) costs nothing but contradicts the spec's text and depends on unverified behaviour. Combining (a) with (b) only adds the costs of both.
   - **(a) Grant out of band, then import.** Before the first apply, the operator grants Storage Blob Data Contributor on the account with the Azure CLI and waits for propagation. They then run `terraform import azurerm_role_assignment.deployer_state_data <assignment-id>` and apply. The config is unchanged.
     - Evidence: UNVERIFIED [negative] (a duplicate role assignment is rejected, so the apply cannot proceed without the import) and UNVERIFIED [compat] (azurerm 3.117.1 imports `azurerm_role_assignment` by its full ID). Neither could be probed without a cloud. The config being unchanged is [existence]: option (a) edits no `.tf` file.
   - **(b) A toggle variable in `bootstrap/azure`.** Add a variable `state_shared_key_enabled` (bool, default `false`) driving both `shared_access_key_enabled` and the provider's `storage_use_azuread = !var.state_shared_key_enabled`. Against an existing account, the first apply runs with `true`: keys stay on, key auth is used, and the role is created. The second apply uses the default and disables keys after the role exists. This adds a variable the spec does not list.
     - Evidence: [compat] Scratch run: `fmt-ok` and `validate` "Success!" with both references. UNVERIFIED [compat]: whether either apply then completes on a real account.
   - **(c) Keep the single apply and document the true order.** The runbook says keys go off before the role is created and to re-run on a 403. This contradicts spec:137's ordering.
     - Evidence: UNVERIFIED [compat]. Whether azurerm 3.117.1's refresh or read of the existing account and container under Entra auth succeeds before the data role exists could not be probed without a cloud. If it fails, a re-run does not help, because the role is never created.

Not-load-bearing UNVERIFIED residue, needing no decision: U5 (§0f). That the kind node resolves §2.7's retagged images could not be run without a cluster. It reuses the existing script's load path, and the plan's rebuild path remains as a fallback.

## 5. Recommendation

🛑 Surface forced decisions to user
