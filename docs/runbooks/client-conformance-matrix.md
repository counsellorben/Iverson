# Running the client-conformance matrix

The orchestrator (`Iverson.Server/Iverson.ClientConformance`) builds five driver processes — .NET,
Java, Python, TypeScript, Go — and runs every scenario against each, producing a languages-down /
scenarios-across report. A full matrix additionally fails the run when any requirement in
`docs/standards/iverson-client-standard.md` was left untouched.

## Credentials — read this first

Every past live verification of this harness lost time here, and one declined to run at all. The
reason is worth stating so the next one doesn't repeat it: **grepping `IVERSON_CLIENT_SECRET` finds
only readers.** Nothing that reads the variable names the file the value comes from, and the file
that holds it never mentions the variable. Both halves look empty from the other side.

The values live in the local-development Authentik blueprint:

    Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml

That blueprint is `compose-only` by its own banner — Helm generates these secrets for kind and for
real deployments, so those targets still need them supplied out of band.

For a docker-compose stack:

```bash
export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id
export IVERSON_CLIENT_SECRET=dev-only-not-for-production-loadtest-secret-0123456789
export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/
export IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
```

`IVERSON_CLIENT_SCOPE` is not optional in practice and has no default. The two scopes are exactly
the provider's `property_mappings` in that blueprint: without `schema_admin` the service token is
accepted and then refused on `RegisterSchema` (403), which presents as a driver defect rather than
as a missing export.

Every `IVERSON_ACTING_USER_*` variable already has a working compose default in `TokenBroker.cs`
and needs no export.

## Running it

```bash
cd Iverson.Server/Iverson.ClientConformance
dotnet run                                   # full matrix
dotnet run -- --languages dotnet,python      # partial
dotnet run -- --json /tmp/matrix.json        # also write JSON
dotnet run -- --help
```

Narrowing either axis makes the run partial, which turns OFF the untouched-requirement gate — a
partial run leaves requirements untouched by construction. **A green partial run is not evidence of
coverage.** Only a full matrix's exit code carries that claim.

Preflight checks gRPC, Authentik and Postgres before any driver is built, and names what is down.

## Cell statuses

| Status | Meaning |
|---|---|
| `ok` | This language's client was observed satisfying the scenario. |
| `FAIL` | Observed getting it wrong. Fails the run. |
| `skip` | **Not observed.** Never means "passed". |
| `n/a` | The scenario ran, but not per-language — it is a single orchestrator-side check with no client library involved, so it runs once and the other columns have nothing of their own to report. The cell's reason names the column holding the real result. |
| `xfail` | Expected failure. No scenario currently emits one. |

`skip` and `n/a` are deliberately different words. `n/a` work WAS observed — once, in the canonical
column — so calling it `skip` would misreport a covered scenario as an unobserved one. Neither
counts against the run's exit code; only `FAIL` and an untouched requirement do.

Two `n/a` scenarios exist today: `nav-property-rejected` and `tenant-rejected`, each a
server-side registration check no client library can express. Both run in the `dotnet` column.

One genuine `skip` remains, and it is accepted rather than outstanding: **`java` /
`naming-rejected`**. That scenario provokes a deliberately misnamed foreign key so the server can
reject it; the Java registrar (`SchemaRegistrar.inferForeignKey`) always derives the FK name as
`{RelatedTypeName}Id` with no override, so the misnaming cannot be expressed in that client's
declaration style. The requirement itself (`IVC-REG-003`) is fully discharged — by the
`dotnet` column's server-side check and by go/python/typescript's client-side checks — so this is a
per-language capability gap, not a coverage gap. Closing it would mean adding a wrong-FK-name
escape hatch to a shipped client library purely to satisfy a conformance test, which is a worse
trade than documenting it here.

## Toolchains

A language whose toolchain is absent is reported as `skip`. Drivers need: .NET SDK, `python3`,
`npx`/`node`, `go`, and `mvn`.

## After a run

The matrix itself uses no Testcontainers, but every unit suite does. Ryuk — Testcontainers' own
reaper, and the only thing that cleans up after a test process is KILLED rather than exiting — was
disabled on this dev box for a long time, which let an interrupted or crashed suite leak its
containers forever. That wedged the machine once (load average 1329, 15 leaked containers, 9 of
them StarRocks clusters), caused 61 spurious failures in an unrelated suite once, and killed an
SDD session mid-plan once (39 leaked containers; every "failure" was a fixture's `StartAsync`, not
an assertion).

**Ryuk was re-enabled on 2026-09-02** — `ryuk.disabled=false` in `~/.testcontainers.properties` and
`TESTCONTAINERS_RYUK_DISABLED=false` in `~/.bashrc` — so a killed run now reaps itself within about
20 seconds and leaks are no longer the default outcome.

One trap survives that change. **The environment variable overrides the properties file**, and a
shell or agent session started before the flip still carries the old `=true` in its own environment
no matter what is on disk now. A long-lived session is therefore the one place leaks can still
happen. Check before running any container-backed suite from an old session:

```bash
echo "${TESTCONTAINERS_RYUK_DISABLED:-<unset>}"    # want false or unset, NOT true
```

If it says `true`, prefix the run (`TESTCONTAINERS_RYUK_DISABLED=false dotnet test ...`) rather than
trusting the file.

`scripts/reap-testcontainers.sh` remains the cleanup for a run that leaked anyway, and for any
machine where Ryuk genuinely cannot run; `--dry-run` lists what it would remove first. It touches
only containers labelled `org.testcontainers=true` plus Ryuk itself, so the compose dev stack
(`iverson-postgres`, `iverson-starrocks`, ...) is never at risk. Note that the script's own header
still describes Ryuk as disabled and has not been updated.

Container-backed test classes are also serialized into a single xunit collection per assembly
(`ContainerCollection.cs` in `Iverson.Api.Tests`, `Iverson.Sql.Tests` and `Iverson.Vector.Tests`),
which caps concurrent containers on this 4-core box — an `IClassFixture` is constructed once per
test CLASS, so without that the assembly starts one container per class in parallel.
