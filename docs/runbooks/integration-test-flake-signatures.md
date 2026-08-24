# Integration-test flake signatures

Six failure signatures accumulated during the remove-IversonTenant work, each recorded at the time
as an undiagnosed "flake family". This is what they actually were. Four had one shared mechanism,
one was a wall-clock race, and one remains unreproduced.

**The general lesson, stated once:** none of the four largest was random. "Flake" was a label applied
to a resource-exhaustion problem that was fully deterministic given the load, and to a timing test
whose budget was smaller than the pauses that load created. Before labelling a failure a flake,
count what else is running.

## Fixed

### FE Full GC / planner timeout
> `StarRocks planner use long time NNNN ms in memo phase ... 1. FE Full GC`

`StarRocksContainerFixture` was an `IClassFixture`, which xunit constructs **once per test class**.
Three classes used it, so three StarRocks all-in-one containers ran concurrently — each a full FE+BE
claiming 4 CPU cores and a ~7.9 GB memory limit, on a box with 4 cores and 9 GB. A full-solution run
peaked at five, once `Iverson.Api.Tests`' two fixtures joined in.

Fixed by sharing one container through `StarRocksCollection`. Peak went 5 → 2, and the assembly got
*faster* (1 m 44 s → 42 s): two container boots cost more than serializing the classes.

**Adding a StarRocks test class?** Put `[Collection(StarRocksCollection.Name)]` on it. Do not use
`IClassFixture` — it silently starts another container.

### Backends without enough disk space
> `Current available backends: [], backends without enough disk space: [1000x]`

Recorded twice, in two different assemblies. A freshly started BE registers with the FE and flips
`Alive` to `true` **before its first disk heartbeat lands**. In that window `SHOW BACKENDS` reports
`AvailCapacity: 1.000 B`, and the FE rejects every query that touches table data.

Every readiness check in the repo tested `Alive` and nothing else — including
`EngagementHealthChecker`, which backs **both** the k8s readiness probe **and** `EngagementRepository`'s
production readiness gate. **This was a production defect, not a test artifact:** on a cold start the
gate opened, the first real query failed anyway, and `/health` reported Healthy while k8s routed
traffic to a pod whose StarRocks queries could not succeed.

Both checks now also require reported capacity. A genuinely full disk reports NOT ready, which is
correct — StarRocks refuses data queries in that state too.

### Circuit breaker returns the wrong exception
> expected `BrokenCircuitException`, got `MySqlException: Unable to connect to any of the specified MySQL hosts`

Not a connectivity problem despite the message. `FastTestOptions`' `BreakDuration` is 600 ms, and the
test makes three calls expecting the third to fail fast. If more than 600 ms elapses between call 2
tripping the breaker and call 3, the breaker has already gone half-open, call 3 executes the
operation, and the real exception surfaces. On a box hosting three to five StarRocks containers, a
600 ms pause between two statements is unremarkable.

Reproduced exactly by inserting a 700 ms pause. The open-circuit test now uses its own long
`BreakDuration`; it never waits for the break to elapse, so this costs nothing. The half-open test
keeps the short one, because elapsing it is what that test is about.

## Mitigated, not individually proven

### Command timeout during seeding
> `MySqlException: Command Timeout expired` inside `SeedAsync`

Same contention as the Full GC signature, and mitigated by the same change. Not separately
reproduced — recorded as mitigated rather than fixed.

### Kafka controller timeout
> `KafkaException: Failed while waiting for controller: Local: Timed out`

Seen once, in `Iverson.Api.Tests`. A Kafka testcontainer competing for the same exhausted box.
Peak StarRocks containers dropping 5 → 2 removes most of that pressure. Not separately reproduced.

## Open — not reproduced

### Syntax error on `@`
> `Getting syntax error at line 1, column 21. Unexpected input '@'`

Seen once, in `StarRocksIntegrationTests.SearchAsync_EqualsClause`. Ruling 41 flagged it as possibly
a real parameter-binding defect rather than a flake, which was the right instinct — it is a
**parser**-stage error, and every other signature here is a runtime or analysis-stage one.

Not reproduced. What was ruled out, empirically, against a live StarRocks 4.1.1:

- **`LIMIT`/`OFFSET` are not parameterised** — both are interpolated as integers, so the classic
  "`@` where a literal is required" cause does not exist in this codebase. (StarRocks also answers
  that case with `LIMIT clause @p0 must be number`, an analysis error, not a syntax error.)
- **The only literal `@` this code sends** is the user spec in `GRANT ... TO USER 'iverson_app'@'%'`.
  All four syntactic variants parse cleanly on 4.1.1.
- **`SELECT @@version_comment`** parses fine; the audit-log failures of that statement were the
  readiness race above, and it comes from the image's own entrypoint, not from this code.

One finding worth keeping regardless, because it is a hazard in its own right: **StarRocks accepts an
unsubstituted `@p0` in a `WHERE` clause as an unset user variable.** `SELECT Id FROM t WHERE Name=@p0`
raises no error and returns zero rows. So if parameter substitution ever did fail, the symptom would
be **silently wrong results, not an exception**.

Both conditions present when this was seen are now gone: the fixtures ran `:latest` (4.1.4 at the
time) and are pinned to 4.1.1, and the oversubscription is removed. A recurrence would therefore be
new information — capture the FE audit log (`/data/deploy/starrocks/fe/log/fe.audit.log`) while the
container is still up, since the fixture disposes it within seconds of the run ending.

## Reproducing any of this

Container-level facts here were established by polling a live container:

```bash
docker run -d --rm --name sr -p 19030:9030 starrocks/allin1-ubuntu:4.1.1
docker exec sr mysql -h127.0.0.1 -P9030 -uroot -e "SHOW BACKENDS\G"   # watch Alive vs AvailCapacity
docker exec sr cat /data/deploy/starrocks/fe/log/fe.audit.log         # every statement the FE received
docker rm -f sr
```
