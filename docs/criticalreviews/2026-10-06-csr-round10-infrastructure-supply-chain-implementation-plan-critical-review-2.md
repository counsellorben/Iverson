# Critical Implementation Review: 2026-10-06-csr-round10-infrastructure-supply-chain-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-06-csr-round10-infrastructure-supply-chain-implementation-plan.md
**Artifact HEAD at review:** 8f8c7931
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA 52275c13); cited file:line references re-checked under §1. (The commits are `73816061` the plan, `50fd55eb` CIR-1, and `8f8c7931` the CIR-1 fixes. The spec's last change is still `52275c13`.)

**Method.**
- Amendment detection used CIR-1's SHA anchor `73816061`.
  - The content-identity check does not match: `git rev-parse 73816061:<plan>` = `c6d4132e`, while `git hash-object <plan>` = `f8cfeb45`. So the amendment set is `git diff 73816061 -- <plan>`.
  - The forward window `git log 73816061..HEAD -- <plan>` contains only `8f8c7931` ("applied 10 fixes from …critical-review-1.md"), which has the in-band shape. The reverse window is empty.
  - Every hunk in the diff maps to one of the 10 CIR-1 fixes, so there are no out-of-band hunks.
- §0 was built from a fresh sweep before CIR-1 was read in detail. Rows marked (a) and (b) are the mandatory fix-neighborhood and intersected-fix rows.
- Code was run in scratch (`scratchpad/cir-d2/`):
  - The prototype `edit.py` was applied to a `git archive HEAD` chart copy, plus the umbrella `starrocks.createUser` and the plan's two Dockerfiles with their real digests.
  - Ten code blocks were extracted verbatim from the plan text and run: Task 1 Step 7, Task 2 Step 7 (checker, guards and both fallbacks), Task 3 Step 7 (dry-run through `docker`/`kind` shims) and Step 10, Task 4 Step 5, image-scan's `sed`, and Task 10's `assert.py`, Step 5 state record and Step 7 teardown.
  - Terraform used CIR-1's UIP prototype with a plugin cache on disk: `init` + `validate` on aws, azure, gcp and bootstrap/azure, `graph`, `fmt`, `tfsec`.
  - PowerShell 7.6.6 was installed in a disk scratch dir to parse and run the `.ps1` files, then removed.
  - The compose check ran on a `git archive` copy with the plan's table applied.
  - The `.dockerignore` canary was rebuilt on busybox. Image-store behaviour was probed with throwaway `docker import` images.
- Every probe image, the PowerShell install, the provider cache and all `.terraform` dirs were deleted. No user container, volume, network or image was touched.

## 0. Coverage enumeration

### 0a. Tasks × surfaces (fresh sweep)

| # | Surface | Disposition |
|---|---|---|
| T1 | Task 1 Steps 1–6 YAML blocks and anchors | ok [compat]. 9 of the 10 plan YAML blocks occur verbatim in the prototype-patched files (script: extract each fenced block, `in` test against the chart files). The tenth, the laptop/local comment paragraph (plan:260-264), is comment text only: dropped D1. Rendered 5 overlays, and kubeconform reports local 100/94, laptop 79/74, aws/azure/gcp 106/100, 0 invalid |
| T1-cmd | Task 1 Step 7 checker | ok [compat]: run verbatim → `allow-all: [] authentik ports: [8443, 9080]` ×5 |
| T2 | Task 2 Steps 1–6 helpers, guard, DNS regex, blocks A/B, values | ok [compat]. All blocks occur verbatim in the patched `_helpers.tpl`, `networkpolicies.yaml`, `values.yaml` and the 5 overlays (same block scan). The renders validate. The Step 3 DNS regex, applied to `HEAD`'s `networkpolicies.yaml`, has both directions checked. over (matches a non-DNS two-line rule): ok [compat]. `matches 14 all-53: True`, and the file's 4 non-DNS `to: []` rules (443 and 443/6443) are all still there after substitution. / under (misses one of the 14): ok [compat]. The file has 14 port-53 rules, and `port: 53` occurs 0 times after substitution |
| T2-cmd | Task 2 Step 7 checker, guards, apiServer fallback, DNS fallback (a: CIR-1 §2.5) | ok [compat]: run verbatim. The checker prints `bad: []` ×5. Both guard strings print. The apiServer fallback prints exactly `iverson-postgres-egress [443, 6443]` and `iverson-kafka-egress [443, 6443]`. The DNS fallback prints `dns fallback port sets: [(('TCP', 53), ('UDP', 53))]` |
| T3-docker | Task 3 Steps 1–2 Dockerfiles | ok [compat]. The plan text differs from the prototype only in its real digests and one `# Base pinned…` comment line (`difflib`). The renders hash the plan text: `e8355217f84b` (TEI) and `3042e552a0d1` (Ollama), each equal to `sha256sum … \| cut -c1-12` |
| T3-chart | Task 3 Steps 3–6 | ok [compat]: Task 3 Step 10 run verbatim → both StatefulSets `init: False vct: False data refs: False`, and `egress: []` ×2 |
| T3-script | Task 3 Step 7 block | ok [compat]: spliced under `set -euo pipefail` and dry-run through shims. Laptop gives 2 builds, each followed by `docker tag` to `docker.io/library/…`, `docker save -o /var/tmp/…` and `kind load image-archive`. A laptop copy with `ollama.enabled: false` gives 1 build |
| T3-ps1 | Task 3 Step 8 `build-and-load-image.ps1` parity | → §2.2 |
| T3-setup | Task 3 Step 9 notes | ok [existence]: `setup.sh:180` and `setup.ps1:102` are the `storageSize` notes (read) |
| T4 | Task 4 Steps 1–5 | ok [compat]: Step 5 run verbatim → `no imageTag left`, `no image pins in overlays`, `no image literals in templates`. The image list is the plan's expected set |
| T5-ignore/canary | Task 5 Step 1 `.dockerignore` (b: CIR-1 §2.4) | ok [compat]. A busybox canary build copied `Iverson.AdminUI/` and `Iverson.Server/Iverson.Api/`. With the plan's lines appended, `find -name '.env*'` printed nothing. With `HEAD`'s file it printed `/api/.env`, `/src/.env.development` and `/src/.env.local`. So under (a `.env*` slips through): ok [compat]. / over (a file a build needs is wrongly excluded by `**/.env`, `**/.env.*`, `docs/`, `.claude/` or `.worktrees/`): ok [negative]. `git ls-files \| grep -E '(^\|/)\.env'` lists only `Iverson.AdminUI/.env.development`, which `vite build` does not read (spec VA43, inherited). `git ls-files Iverson.Server/Iverson.Api Iverson.AdminUI \| grep -c -E '(^\|/)(docs\|\.claude\|\.worktrees)/'` = 0. The build-stage `COPY` sources (`Api/Dockerfile:13-20, 23`, `AdminUI/Dockerfile:15, 17, 18`, `Launcher/Dockerfile:4, 7`) name no `.env*`, `docs/`, `.claude/` or `.worktrees/` path |
| T5-compose | Task 5 Step 5 table and command (a: CIR-1 §2.6) | ok [compat]. The table was applied by line number to a `git archive` copy, and every target line is an `image:` line. Only the two `iverson-api` lines (484, 608) stay unpinned. `config --no-interpolate -q` → `compose-ok`; plain `config -q` → rc 1 (missing `IVERSON_SMOKE_TEST_PASSWORD`); a `[broken` image line → rc 1 |
| T5-build | Task 5 Step 8 keep both `:check` images (a: CIR-1 §2.7) | Producer ok [existence] (plan:870-871 tag `csr10d-api:check` and `csr10d-adminui:check`; plan:877 keeps both). Interaction with Task 10 Step 7: → §2.1 |
| T6 | Task 6 Steps 1, 3, 5 | ok [compat]. `image-scan.yml` was extracted verbatim and parses (`jobs: first-party, third-party`). Its `uses:` lines are all SHA-pinned. The SHA-pin grep has both directions checked. over (a pinned `@<40hex> # vX` line falsely flagged): ok [compat]. Run on the extracted `image-scan.yml`, it prints `every uses: is SHA-pinned`, and on `HEAD` it flags none of the other workflows' pinned lines. / under (an unpinned `uses:` slips through): ok [compat]. On `HEAD` it flags exactly `admin-ui.yml:31, 34, 136` (`@v7`), the three lines Step 1 edits. The third-party `sed` also has both directions checked. over (it emits `iverson-*` or a non-image line): ok [compat]. Its output over the cloud renders is 10 `repo:tag` refs, none `iverson-*`; the comment lines containing "image" were not emitted. / under (it misses a third-party image): ok [compat]. A `grep image` dump of the same renders shows exactly those 10 third-party `image:` values (including the 4-space-indented StarRocks CR lines), plus the 4 excluded `iverson-*` refs |
| T6-ps1 | Task 6 Step 4 setup.ps1 pins | ok [compat]. Applied to a copy: `grep -c -- '--version'` = 7 (0 on `HEAD`), and the PowerShell parser reports `parse-errors=0` |
| T7 | Task 7 Steps 1–5 incl. `depends_on` (a: CIR-1 §2.2) | ok [compat]. azure `validate` printed "Success!". `terraform graph` shows 12 operator→`deployer_cluster_admin` edges with `depends_on` and 0 without. `grep -c '^data ' modules/operators/*.tf` = 0 ×3. `fmt -check` passes with the argument aligned; an unaligned paste fails it: dropped D2 |
| T7-cmd | Task 7 Step 6 loop (a: CIR-1 §2.8) | ok [compat]: `validate -no-color \| head -1` printed `Success! The configuration is valid.` for azure and bootstrap/azure |
| T8 | Task 8 | ok [compat]: aws and gcp `init -lockfile=readonly` + `validate` → "Success!" ×2; `fmt-ok`; `tfsec` "No problems detected!" |
| T9-§7 | Runbook §7 existing-account sequence (a: CIR-1 §2.3, §3.1(a)) | ok [compat]. The bootstrap graph has `deployer_state_data -> storage_account.state` and `storage_container.state -> deployer_state_data`. Import by full ID is documented: [existence] azurerm v3.117.1 `website/docs/r/role_assignment.html.markdown` "## Import" (`terraform import azurerm_role_assignment.example /…/providers/Microsoft.Authorization/roleAssignments/<guid>`, with scope-specific ID forms). Real-account behaviour: U2 |
| T9-rest | Runbook §1–6, §8 | ok [existence]: unchanged since CIR-1; command names match Tasks 3, 7 and 8 |
| T10-S1 | §5a `assert.py` (a: CIR-1 §2.5) plus kube-score | ok [compat]. `assert.py` (verbatim) gives PASS ×5. kube-score criticals keyed by (object, check): base = new, with 12/10/13/13/13 pairs and 0 added and 0 removed per overlay. Rule directions: R1 |
| T10-S2/S3 | §5d, §5e | ok [compat]: T7-cmd, T8 and T6 runs |
| T10-S4a | Canary block (a: CIR-1 §2.4) | ok [compat]. The in-directory canaries are falsifiable (T5-ignore run). The console stage's `WORKDIR` is `/src` (`Iverson.AdminUI/Dockerfile:14`), so `test ! -e /src/.env.development` targets the copied tree. [negative] No `CANARY` string in a `/` grep of `node:22-alpine` (`base-done` only) or in the host `node_modules` |
| T10-S4b/c | Wrong-digest build; local trivy | ok [existence]: unchanged since CIR-1; `aquasec/trivy:0.75.0` re-resolved `af6acf9a…` |
| T10-S5 | Pass 1: state record, retag+load, probes (a: CIR-1 §2.1, §2.7, §2.9) | Probes ok [compat]. The pass-1 render has Services `iverson-api` (8080, 8081) and `iverson-authentik` (9000, 8443, 9080). `tei-egress` selects `iverson.io/component: tei` and `ollama-egress` selects `app: iverson-ollama`. `api-egress`'s only 53 rule is the kube-dns helper. `curlimages/curl:8.18.0` `nc -z -w 2 127.0.0.1 1` → `closed-rc=1`, BusyBox v1.37.0. Retag ok [compat]: `docker tag <localhost image>:check docker.io/library/…` works on this podman 5.7.0. kind falls back to podman with no env var (`kind get clusters` prints "enabling experimental podman provider"). Node resolution of the retag: U1. State record: → §2.1 |
| T10-S6 | Pass 2 (a: CIR-1 §2.1, §2.7) | ok [compat]. The pass-2 render (Step 6's `--set` list) has `iverson-prometheus` 9090 and `iverson-admin-ui` 8080. `prometheus-ingress` admits only `app=iverson-api`. `api-egress` has `iverson-prometheus:9090`. The images are `iverson-admin-ui:csr10d` and `iverson-api:csr10d` with `IfNotPresent` |
| T10-S7 | Teardown and state diff (a: CIR-1 §2.9) | → §2.1 |

### 0b. Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | T1 → T2 overlays' `networkPolicy:` block | ok [compat]: rendered (T2-cmd) |
| C2 | T2 → T3 `include` lines deleted in TEI/Ollama egress | ok [compat]: `egress: []` ×2 |
| C3 | T3 script tags = install tags (both `dependency build` + same values) | ok [compat]: dry-run tags equal the `helm template` tags (`…-e8355217f84b`, `…-3042e552a0d1`) |
| C4 | T5 S8 `:check` images → T10 S4c `docker save` and S5/S6 retag (persisted local images) | ok [existence]: produced at plan:870-871, consumed at plan:1430, 1455, 1528 |
| C5 | T5 S8 `:check` images → T10 S5 `before-images.txt` → T10 S7 `comm -23` (persistence boundary) (b: CIR-1 §2.7 × §2.9) | → §2.1 |
| C6 | T10 S5 `before-{containers,volumes,networks}.txt` → S7 diffs | Formats ok [compat]. I ran a script over the plan text that extracted each Step 5 `… \| sort > before-<k>.txt` command and searched Step 7 for a line starting with the identical command and reading the same `before-<k>.txt` (for images, the `after-images.txt` writer). Output: `containers MATCH`, `volumes MATCH`, `networks MATCH`, `images MATCH`. Contents: population P1 |
| C7 | T3 S8 `.ps1` mode → runbook §2 and `setup.ps1:102` note | → §2.2 |
| C8 | T7 → T9 runbook §6/§7 ordering claims | ok [compat]: T7 and T9-§7 graph runs |
| C9 | T2 S7 → T10 S1 "guard and fallback checks re-pass" | ok [compat]: T2-cmd |

### 0c. Rule-like content (both directions)

| # | Rule | Disposition |
|---|---|---|
| R1 | `assert.py` `API` and ingress-CIDR equality checks (a: CIR-1 §2.5) | over (a correct render failed): ok [compat], PASS ×5. / under (a wrong render passed): ok [compat]. aws with `--set networkPolicy.apiServerCidrs={10.0.0.0/20}` → FAIL `iverson-{postgres,kafka}-egress: API cidrs ['10.0.0.0/20']`. aws with `--set networkPolicy.clusterCidrs={10.0.128.0/20}` → FAIL on all 4 (policy, port) cells |
| R2 | Task 2 DNS-fallback port-set checker (a: CIR-1 §2.5) | over: ok [compat]. The patched chart with the flag prints only `(('TCP', 53), ('UDP', 53))`; with the flag off the set is `[]`. / under: ok [compat]. On the `HEAD` chart with the flag it prints three tuple sets, adding `(('TCP', 443),)` and `(('TCP', 443), ('TCP', 6443))`, so a stray `to: []` is visible |
| R3 | Outsider `nc` refusal and `apiprobe` DNS checks (a: CIR-1 §2.1) | over (a correct deploy reported broken): ok [compat]. The outsider gets `egress: [{}]`, the targets' Services expose the probed ports (T10-S5/S6), and the `nc` exit codes were shown. / under (a regression passes): UNVERIFIED [compat] → U5. The read is [existence]: on `main`, `from: []` at `networkpolicies.yaml:514, 605, 615` and `to: []` on 53 would admit the outsider and the outside resolver, which would print `ADMITTED (fail)` / `OUTSIDE RESOLVER REACHABLE (fail)`. Observing that needs a kind cluster, which this round could not run |
| R4 | Step 7 `comm -23` "nothing removed or re-pointed" (a: CIR-1 §2.9) | under (a user image removed and not reported): ok [compat]. In-round run: a throwaway `csr10d-simapi:check` was present in a snapshot written with the plan's literal Step 5 images command. After it was removed, the plan's literal `comm -23` line printed `localhost/csr10d-simapi:check 7c76993b232a`, and `no-image-removed-or-repointed` did not print. CIR-1's toy `comm` run (removed and re-pointed entries listed) is [existence], recorded and not re-run. / over (a clean run reported dirty): → §2.1 |
| R5 | `.ps1` mode switch (`[switch]$ModelImages`) | over: n/a, since a switch only adds a branch. / under (the mode never runs): → §2.2 |

### 0d. Population matrices (closure for confirmed findings)

- **§2.1: images Step 7 removes × present in Step 5's `before-images.txt` (7 cells):**
  - `docker.io/library/iverson-api:csr10d`: ok [existence]. Created at plan:1455, after the snapshot (plan:1446).
  - `localhost/iverson-api:csr10d`, `localhost/iverson-admin-ui:csr10d`: ok [negative]. `grep -n -E 'localhost/iverson\|-t iverson-(api\|admin-ui)\|build-and-load-image\.sh csr10d iverson *$\|--image-name iverson-admin-ui'` over the plan has one hit, plan:1554, the `rmi` itself. No step builds or tags those names: Task 10 retags `:check` images to `docker.io/library/…` (plan:1455, 1528), and the app-image script call that could produce `localhost/…` is gone.
  - `docker.io/library/iverson-admin-ui:csr10d`: ok [existence]. Created in Step 6 (plan:1528).
  - `csr10d-api:check`, `csr10d-adminui:check`: → §2.1. Created at Task 5 Step 8, kept, used at plan:1430 before the snapshot, then removed at plan:1554.
  - `iverson-{tei,ollama}-model:*` (plan:1555): ok [existence]. Built at plan:1456, after the snapshot.
- **P1: the other state files × Task 10's own resources:**
  - containers: ok [existence]. `kind delete` removes the node container. `csr10d-ui` is `--rm`'d in Task 5.
  - networks: ok [compat]. `kind` already exists in `docker network ls` (`abe2b701…`, `iversonserver_default`, `kind`, `podman`), so `kind create` adds none.
  - volumes: UNVERIFIED [compat] → U3. kind v0.24.0 `pkg/cluster/internal/providers/podman/provider.go:143-172` runs `podman rm -f -v` and then `deleteVolumes`. That is a source read; the run needs a cluster.
- **§2.2: PowerShell scripts the plan edits or tells users to run (2 cells):**
  - `build-and-load-image.ps1`: → §2.2.
  - `setup.ps1`: ok [compat]. `parse-errors=0` on `HEAD`, and again after Task 6 Step 4's pins.
  - Task 3 Step 9's `Write-Host` line is prose, so it is [existence] only. No other tracked `.ps1` exists: [negative] `git ls-files '*.ps1'` lists exactly these two.

### 0e. Intersected fix texts and amendment hunks

| # | Item | Disposition |
|---|---|---|
| X1 | (b) CIR-1 §2.7 (keep the `:check` images for Task 10) × §2.9 (`comm -23` must be empty) × Step 7's original `rmi` list | → §2.1 |
| X2 | (b) CIR-1 T3-ps1 "ok [existence]: static only (no `pwsh`)": the evidence has now changed, because a parser exists in-round | → §2.2. This is not a re-raise: CIR-1 raised no finding there |
| X3 | (b) Plan rows 29–34, recorded at UIP | ok [compat]: each re-run this round (§1 rows 29–34) |
| X4 | (c) Out-of-band amendment hunks | [negative] ok. The forward window holds only in-band `8f8c7931` and the reverse window is empty. Every diff hunk maps to a CIR-1 fix: §2.1 → plan:1474-1498, 1525-1541; §2.2 → plan:1130; §2.3/§3.1 → plan:1309-1315; §2.4 → plan:1407-1416; §2.5 → plan:474-481, 1357-1368; §2.6 → plan:852; §2.7 → plan:877, 1455, 1528; §2.8 → plan:1208; §2.9 → plan:1439-1447, 1559-1564; rows 29–34 |

### 0f. Dropped candidates

| # | Candidate | Disposition |
|---|---|---|
| D1 | The prototype never rewrote the laptop/local comment paragraph (plan:260-264) | dropped. Comment-only, so the render is identical. The plan text is the instruction, and its anchor exists (`values-local.yaml:14`, `values-laptop.yaml:13`) |
| D2 | `depends_on = [module.cluster]` pasted unaligned fails Task 7 Step 6's `fmt -check` | dropped. The bullet's inline code fixes no whitespace, and the same holds for the round-1-reviewed `use_azuread_auth = true` bullet. `fmt -check` names the file, and `terraform fmt` aligns it (run: unaligned fails, then fmt yields `depends_on   = [module.cluster]` and passes) |
| D3 | The plan never runs `helm lint`, although spec §5a lists it | dropped. `helm lint` with ci-overrides reports `0 chart(s) failed` ×5 on the patched chart (run), and `deploy-validate.yml:28-40` lints every PR. No outcome breaks |
| D4 | Step 4(b) uses relative paths after Step 4(a) `cd`s into a directory it then deletes | dropped. The plan's blocks consistently assume the worktree root at block start (each Task begins `cd Iverson.Server/…` from the root). This is a working-directory convention, not a broken command |
| D5 | azurerm 3.x reads queue service properties of a key-disabled account under Entra | dropped. It is unprobeable without a cloud. It belongs to CIR-1 D1/U4's refresh-under-Entra family, which CIR-1 §3.1 put to the user and the user decided as (a); there is no new evidence |

### 0g. UNVERIFIED residues (each flows to §3)

| # | Residue | Disposition |
|---|---|---|
| U1 | The kind node resolves the retagged `docker.io/library/iverson-{api,admin-ui}:csr10d` (CIR-1 U5, still unrun) | UNVERIFIED [compat]: no kind cluster in-round. Not load-bearing: it is the load path of `build-and-load-image.sh:63-66` → §3 note |
| U2 | Runbook §7 on a real Azure account: the out-of-band role, import, then apply completes (CIR-1 U2–U4) | UNVERIFIED [compat]: no cloud. Not load-bearing for this round: the spec's global constraint accepts static-only Terraform, and the user decided the mechanism at CIR-1 §3.1 → §3 note |
| U3 | `kind delete` leaves `docker volume ls` unchanged under podman | UNVERIFIED [compat]: source read only (P1). Not load-bearing: a leftover would print a named diff line, never hide a user change → §3 note |
| U4 | §2.2's fix on Windows PowerShell 5.1 | UNVERIFIED [compat]: run on PowerShell 7.6.6 (Linux) only. Not load-bearing: the requirement that `param` come first, preceded only by comments, is the same language rule in 5.1 → §3 note |
| U5 | R3 under: on `main`'s rules, the outsider and `apiprobe` checks print `ADMITTED (fail)` / `OUTSIDE RESOLVER REACHABLE (fail)` | UNVERIFIED [compat]: needs a kind cluster. Not load-bearing for this plan's text: the rules that would admit the traffic are read at `networkpolicies.yaml:514, 605, 615` and the 53 `to: []` sites. Task 10 Step 5 runs these checks against the new rules, where a regression shows as `ADMITTED (fail)` → §3 note |
| U6 | §1 row 27: gRPC through kind ingress-nginx needs TLS with ALPN h2 on 8443 | UNVERIFIED [compat]: recorded from the 2026-09-24 spec, needs a cluster. Not load-bearing: Task 10 Step 5's gRPC control uses exactly that path (`--http2`, 8443), and its expected `HTTP/2 200` plus `grpc-status` would expose a wrong premise as a visible failure → §3 note |

## 1. Verified-plan-assumptions cross-check

1. New paths — still holds. [absence] `ls` → "No such file or directory" ×4.
2. Overlays and ci-overrides; CI renders each — still holds. [existence] 8 files; `deploy-validate.yml:28-40` (5 overlays) and `.gitlab-ci.yml:39-45` (4) read.
3. `validateNoPlaceholders` at `_validate.tpl:23`, included at `networkpolicies.yaml:1` — still holds. [existence] read.
4. `$.Files.Get` inside `range` — still holds. [compat] TEI tag suffix = `sha256sum` of the file (T3-docker).
5. Helm functions — still holds. [compat] v3.16.4 renders. CI's 3.15: [existence], recorded and not re-run.
6. kubeconform counts and §5a — still holds. [compat] 100/94, 79/74, 106/100 ×3, 0 invalid; PASS ×5.
7. No new kube-score critical — still holds. [compat] (object, check) sets equal per overlay, 0 added and 0 removed.
8. Guards — still holds. [compat] Both strings printed.
9. Fallback scoping — still holds. [compat] T2-cmd.
10. A Dockerfile edit plus `dependency build` changes the suffix — still holds. [compat] `…-3042e552a0d1` → `…-eead05ee801e` after appending a line, equal to the new `sha256sum`. CIR-1 already noted the stale-tgz clause as non-deterministic and not load-bearing.
11. `egress: []` validates — still holds. [compat] kubeconform 0 invalid.
12. Script parse; Ollama disabled gives 1 build — still holds. [compat] Shim dry-run 2, then 1.
13. `kind load image-archive`; `/var/tmp` ext4, `/tmp` tmpfs — still holds. [compat] `--help` "Loads docker image from archive…"; `df -hT`.
14. Terraform passes fmt, validate and tfsec — still holds. [compat] `fmt-ok`; Success ×4 (aws, azure, gcp, bootstrap/azure); "No problems detected!".
15. Two `identity {` blocks at `:91`, `:175` — still holds. [existence] grep.
16. `aws_vpc.this` at `:14` — still holds. [existence] grep.
17. Index digests — still holds. [compat] `digest.sh` re-resolved TEI `8de25e75…`, ollama `3d8a05e3…`, node `0a7108bf…`, nginx `15c994d1…`, curl `d94d07ba…`.
18. trivy-action SHA and inputs; trivy digest — still holds. [compat] `gh api …/commits/v0.36.0` → `ed142fd0…`. `action.yaml` at that SHA has all 7 inputs. `aquasec/trivy:0.75.0` → `af6acf9a…`.
19. setup-node and setup-helm SHAs — still holds. [compat] `820762786…`, `9bc31f4e…`; checkout v7.0.1 `3d3c42e5…`.
20. No actionlint; PyYAML 6.0.3 — still holds. [compat] `which` empty; `6.0.3`.
21. Host Node v22.16.0 — still holds. [compat] `node -v`.
22. The setup notes and the at-rest runbook subset — still holds. [existence] `setup.sh:180`, `setup.ps1:102` read; `at-rest-encryption-verification.md` present.
23. No tfvars — still holds. [negative] `git ls-files | grep -c '\.tfvars'` = 0.
24. Every Helm caller's overlays set `apiServerCidrs` — still holds. [negative] Grepping tracked non-doc files for `helm (template|lint|upgrade|install)` finds `deploy-validate.yml`, `.gitlab-ci.yml` and `setup.{sh,ps1}`; setup installs only operators. The other three hits are text inside chart templates and values (`secret-service-clients.yaml`, `_validate.tpl`, `values-local.yaml`), not callers. No `.tf` file references `helm/iverson`.
25. `vpc_id` default null keeps the three call sites valid — still holds. [compat] aws, azure and gcp validate.
26. The LoadTest lag report connects to Kafka — still holds. [existence] `WritePathRunner.cs:219` `PrintKafkaLagAsync`.
27. gRPC needs TLS ALPN h2 on 8443 — still holds as recorded. UNVERIFIED [compat] → U6: the recorded run is [existence] (the 2026-09-24 spec), and re-running it needs a kind cluster.
28. Compose stack not running; 8080/8443 free — still holds. [compat] `docker ps` empty; `ss -ltn` no match.
29. `depends_on` ordering — still holds. [compat] graph edges 12 with, 0 without; `^data` 0.
30. Bootstrap ordering — still holds. [compat] Both graph edges printed.
31. `validate | head -1` — still holds. [compat] Printed `Success!…`.
32. `config --no-interpolate` — still holds. [compat] `compose-ok`; plain `config` rc 1; malformed rc 1.
33. curl image `nc`/`nslookup`; the outsider is default-deny only — still holds. [compat] `closed-rc=1`, BusyBox v1.37.0. The render's selectors (T10-S5): only `iverson-default-deny` (`podSelector: {}`) selects `app=csr10d-outsider`.
34. `.dockerignore` excludes in-directory `.env*` — still holds. [compat] Canary build both ways (T5-ignore).

**Span check: uncovered dependencies.**
- Task 3 Step 8 needs `build-and-load-image.ps1` to parse. No row covers it, and it does not parse: → §2.2.
- Task 10 Step 7's image check needs no image in `before-images.txt` to be removed by Step 7 itself. No row covers it, and two are: → §2.1.
- `kind load` without `KIND_EXPERIMENTAL_PROVIDER` picks podman: verified [compat], since `kind get clusters` with no env prints "enabling experimental podman provider".
- The `iverson` namespace exists before `helm upgrade -n iverson` (no `--create-namespace`): verified [existence] by `setup.sh:55`.
- `kind delete` removes podman node volumes: U3 → §3 note.

## 2. Literal-wrongness findings

1. **Task 10 Step 7: `no-image-removed-or-repointed` cannot print on a correct run, because Step 7 deletes two images that Step 5's snapshot recorded.**
   - CIR-1 §2.7's fix keeps `csr10d-api:check` and `csr10d-adminui:check` from Task 5 Step 8 for Task 10 (plan:877). Task 10 Step 4(c) saves them (plan:1430), so they exist when Step 5 writes `before-images.txt` (plan:1446).
   - Step 7's first `rmi` (plan:1554) removes both, and then writes `after-images.txt` (plan:1559). `comm -23 before after` (plan:1560) prints the two `localhost/csr10d-…:check <id>` lines, so the expected `no-image-removed-or-repointed` (plan:1564) never appears.
   - The same `rmi` names `localhost/…:csr10d` tags that never exist. podman still removes the names that do exist; it exits 1, which `>/dev/null 2>&1` hides.
   - Evidence: [compat] probe with throwaway images, run on this podman 5.7.0.
     - `docker rmi <existing-tag> localhost/<missing> <existing:check> <existing:check2>` → rc 1, and every existing name was removed (`none-left`).
     - The plan's Step 5 and Step 7 lines (with probe names) run on a `:check` image present before the snapshot: no `no-image-removed-or-repointed`, and `comm -23` printed `localhost/csr10d-simapi:check 7c76993b232a`.
   - **Proposed fix.** In Step 7 (plan:1554), drop `csr10d-api:check csr10d-adminui:check` from the first `docker rmi`. After the `comm -13` line (plan:1561), add `docker rmi csr10d-api:check csr10d-adminui:check >/dev/null`. In the Expected paragraph (plan:1564), add: "The two `:check` images from Task 5 are removed last, after the comparison, because they predate the Step 5 snapshot."
   - Evidence: [compat] Same probe with the reordered lines: untag the retag, snapshot, `comm -23` → `no-image-removed-or-repointed`; the final `rmi` then removes the `:check` image (count 0). [existence] No other Step 7 removal target exists at snapshot time (§0d matrix).

2. **Task 3 Step 8: `build-and-load-image.ps1` does not parse, so the model-image mode the plan adds to its `param(...)` can never run.**
   - The script's first statement is `$ErrorActionPreference = "Stop"`, and `param(…)` comes after it (`build-and-load-image.ps1:1, 13-16`). A script's `param` block must come first, preceded only by comments.
   - PowerShell therefore parses `param(...)` as an expression and rejects `[string]$Tag = "0.1.0", …`.
   - Step 8 adds `[switch]$ModelImages` and `[string]$Values` to that same block. Its only check is a static bash-to-PowerShell line comparison, which cannot catch a parse failure.
   - Spec §2c's ".ps1 also build[s] and load[s] the model images", and the `setup.ps1:102` note Step 9 writes, therefore point at a script that fails before its first line runs. This is a pre-existing defect since `c87402c9`, and Step 8's outcome depends on it.
   - Evidence: [compat] With the `HEAD` file and PowerShell 7.6.6, `pwsh -File build-and-load-image.ps1 -Tag csr10d -ClusterName iverson` → `ParserError: … :14 The assignment expression is not valid`, rc 1, and the `docker` shim log stayed empty. `Parser::ParseFile` gives `build-and-load-image.ps1 parse-errors=1` and `setup.ps1 parse-errors=0`.
   - **Proposed fix.** In Step 8, add a first sub-bullet: "Move `$ErrorActionPreference = "Stop"` from line 1 to directly after the `param(...)` block. A script's `param` block must be its first statement, after comments only. Today the file fails to parse (`The assignment expression is not valid`)." Add to the Step 8 verification: "If `pwsh` is available, `pwsh -NoProfile -Command '$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile(\"<abs path>\",[ref]$null,[ref]$e); $e.Count'` prints `0`."
   - Evidence: [compat] Same file with the line moved below `param(...)`, plus `[switch]$ModelImages` and `[string]$Values`. `-Tag csr10d -ClusterName iverson` ran through the shims: `docker build`, `docker tag … docker.io/library/iverson-api:csr10d`, then `kind load docker-image … --name iverson`. `-ModelImages -ClusterName kx` bound the switch and printed the default `values-laptop.yaml` path. UNVERIFIED [compat]: Windows PowerShell 5.1 (U4); the rule is the same.

## 3. Forced decisions

No forced decisions found.

These UNVERIFIED residues are not load-bearing and need no decision:
- U1: the kind node resolving the retagged app images. This is the existing script's load path.
- U2: runbook §7 on a real Azure account. The spec accepts static-only Terraform, and the user decided the mechanism at CIR-1 §3.1.
- U3: `kind delete` removing podman node volumes. A miss is visible as a named diff line.
- U4: §2.2's fix on PowerShell 5.1. It is the same language rule.
- U5: R3's under direction, observed on `main`'s rules. It needs a cluster; the admitting rules were read, and Task 10 Step 5 runs the checks live.
- U6: §1 row 27, gRPC needing TLS ALPN h2 on 8443. It is recorded, and Task 10 Step 5's gRPC control would fail visibly if it were wrong.

## 4. Previously addressed

- CIR-1 §2.1 (outsider probe had no egress; `curl` against h2c): resolved. `outsider_egress` and `nc -z` are at plan:1475-1488 and 1534-1537; DNS checks run from `apiprobe` at plan:1490-1491. [compat] Selectors, Service ports and `nc` were probed (T10-S5).
- CIR-1 §2.2 (operators did not wait for the deployer role): resolved at plan:1130. [compat] 12 graph edges.
- CIR-1 §2.3 (runbook §7 ordering claim): resolved at plan:1309-1315. [compat] Graph edges; import by ID is documented.
- CIR-1 §2.4 (unfalsifiable canary): resolved at plan:1407, 1412 and 1416. [compat] Canary build both ways.
- CIR-1 §2.5 (missing §5a assertions): resolved at plan:474-481 and 1357-1368. [compat] Both directions (R1, R2).
- CIR-1 §2.6 (compose needs `.env`): resolved at plan:852. [compat] `compose-ok`.
- CIR-1 §2.7 (uncapped rebuilds in Task 10): resolved at plan:877, 1455 and 1528. Its interaction with Step 7 is new this round (§2.1).
- CIR-1 §2.8 (`tail -1`): resolved at plan:1208. [compat] `head -1` prints Success.
- CIR-1 §2.9 (state-record formats; `kindest/node` addition): resolved at plan:1442-1447 and 1556-1564 for formats and additions. The removal interaction is new mechanism evidence (§2.1), not a re-raise.
- CIR-1 §3.1 (existing state account mechanism): decided as option (a), at plan:1309-1315.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes
