# Ruling 3 — Drop trailing `actingUserToken` param from Java EntityCoordinator

Worktree: `/home/ben/repositories/Iverson-serverids`, branch `acting-user-identity-parity`.
Status: **COMMITTED**. One test was flagged for owner ruling mid-task (see "Blocking issue" below); the owner ruled, the ruling was applied, and the build went green — see "Addendum" at the end of this report.

## Scope covered

File: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java`

### 1. Methods with no paired overload — parameter simply dropped

- `getMapped(String id, int depth, String actingUserToken)` → `getMapped(String id, int depth)` — `EntityCoordinator.java:183`. Body now calls `mappingStubFor(null)` at `:189` (still resolves via bound/ambient through `withIdentity`).
- `postMapped(T entity, String actingUserToken)` → `postMapped(T entity)` — `EntityCoordinator.java:199`. Body calls `mappingStubFor(null)` at `:204`.
- `updateMapped(T entity, String actingUserToken)` → `updateMapped(T entity)` — `EntityCoordinator.java:213`. Body calls `mappingStubFor(null)` at `:218`.
- No `@param actingUserToken` javadoc tags existed on any of these three methods pre-change (checked before editing) — nothing to remove there.

### 2. Search-family pairs collapsed (5 pairs → 5 methods)

Each pair (no-token delegator + token-taking body) collapsed into the single surviving no-token method, routed through `stubFor(null)` (which still resolves bound/ambient identity via `withIdentity`):

- `groupBy(GroupByBuilder)` — `EntityCoordinator.java:248`, body now `stubFor(null).groupBy(request)` at `:250`. Deleted the old `groupBy(GroupByBuilder, String actingUserToken)` overload.
- `aggregate(AggregateBuilder)` — `EntityCoordinator.java:262`, body `stubFor(null).aggregate(request)` at `:264`. Deleted `aggregate(AggregateBuilder, String)`.
- `pipeline(PipelineBuilder)` — `EntityCoordinator.java:272`, body `stubFor(null).pipeline(request)` at `:274`. Deleted `pipeline(PipelineBuilder, String)`.
- `searchSimilar(SimilarBuilder)` — `EntityCoordinator.java:283`, body `stubFor(null).searchSimilar(request)` at `:285`. Deleted `searchSimilar(SimilarBuilder, String)`.
- `searchChunks(ChunksBuilder)` — `EntityCoordinator.java:297`, body `stubFor(null).searchChunks(request)` at `:299`. Deleted `searchChunks(ChunksBuilder, String)`.

None of these five had `@param actingUserToken` javadoc tags either — the token-taking overloads only had a one-line `/** Same as {@link #x(...)}, propagating an acting-user token if given. */` comment, which was deleted along with the method (no dangling javadoc to fix).

### 3. Out-of-scope surface confirmed untouched

- `IversonClient.getSchema(String traceId, String actingUserToken)` — not touched.
- `EntityCoordinator.withActingUser(String actingUserToken)` — not touched (`EntityCoordinator.java:89`).
- `IversonClient` constructors taking `actingUserToken` — not touched.
- Other 4 client libraries, server, write/mapped-CRUD contracts — not touched.

## Mechanical verification

Regex sweep (multi-line aware, via `perl -0777`) over the final file for any `public` method still ending in `String actingUserToken)`, and a duplicate-signature check:

```
perl -0777 -ne 'my @m; while (/public\s+[\w<>\[\],\s.]+?\s(\w+)\s*\(([^)]*)\)/gs) {
  push @m, "$1(".join(",", map {s/^\s+|\s+$//g; $_} split(/,/, $2)).")";
}
my %seen;
for (@m) { print "DUPLICATE: $_\n" if $seen{$_}++; }
for (@m) { print "OFFENDING: $_\n" if /String\s+actingUserToken\)$/; }' EntityCoordinator.java
```

Output:
```
OFFENDING: withActingUser(String actingUserToken)
```
No `DUPLICATE:` lines. The one "OFFENDING" hit is `withActingUser` itself — explicitly out of scope (that parameter is the whole point of the method). No other public method retains a trailing token parameter; no two methods share a signature.

## Call sites updated

`Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java`:
- `:82` `authorCoordinator.postMapped(author, null)` → `authorCoordinator.postMapped(author)`
- `:100` `articleCoordinator.postMapped(article, null)` → `articleCoordinator.postMapped(article)`
- `:104-105` `articleCoordinator.getMapped(persistedArticle.getId().toString(), 1, null)` → `articleCoordinator.getMapped(persistedArticle.getId().toString(), 1)`

Sample module compile check (`mvn -f Iverson.Clients/Java/pom.xml -pl sample -am compile -DskipTests`): **BUILD SUCCESS**.

## Tests migrated

`Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java`:

| Test | Before | After | Assertion |
|---|---|---|---|
| `groupBy_withActingUserToken_usesWithOption` (`:96`) | `sut.groupBy(builder, "user-token-123")` | `sut.withActingUser("user-token-123").groupBy(builder)` | unchanged: `verify(mockStub).withOption(ACTING_USER_TOKEN, "user-token-123")` |
| `groupBy_withNullActingUserToken_doesNotCallWithOption` → renamed `groupBy_withNoActingUserBound_doesNotCallWithOption` (`:105`) | `sut.groupBy(builder, null)` | `sut.groupBy(builder)` | unchanged: `verify(mockStub, never()).withOption(any(), any())`. Renamed only because "null token" no longer exists as a call-site concept — the assertion (no identity attaches) is identical. |
| `aggregate_withActingUserToken_usesWithOption` (`:131`) | `sut.aggregate(builder, "user-token-456")` | `sut.withActingUser("user-token-456").aggregate(builder)` | unchanged |
| `pipeline_withActingUserToken_usesWithOption` (`:159`) | `sut.pipeline(builder, "user-token-999")` | `sut.withActingUser("user-token-999").pipeline(builder)` | unchanged |
| `searchSimilar_withActingUserToken_usesWithOption` (`:191`) | `sut.searchSimilar(builder, "user-token-321")` | `sut.withActingUser("user-token-321").searchSimilar(builder)` | unchanged |
| `searchChunks_withActingUserToken_usesWithOption` (`:221`) | `sut.searchChunks(builder, "user-token-789")` | `sut.withActingUser("user-token-789").searchChunks(builder)` | unchanged |
| `getMappedPassesDepthThrough` (`:233`) | `mappingSut.getMapped(id.toString(), 3, "tok")` | `mappingSut.withActingUser("tok").getMapped(id.toString(), 3)` | unchanged: depth/key assertions via `ArgumentCaptor` |
| `getMapped_returnsNull_whenNotFound` (`:257`) | `mappingSut.getMapped("missing-id", 1, "tok")` | `mappingSut.withActingUser("tok").getMapped("missing-id", 1)` | unchanged: `assertNull(...)` |
| `postMappedReturnsEntityHydratedFromData` (`:267`) | `mappingSut.postMapped(entity, "tok")` | `mappingSut.withActingUser("tok").postMapped(entity)` | unchanged: hydration asserts + `verify(mockMappingStub).withOption(eq(ACTING_USER_TOKEN), eq("tok"))` |
| `postMapped_throws_whenServerReportsFailure` (`:290`) | `assertThrows(..., () -> mappingSut.postMapped(entity, "tok"))` | `assertThrows(..., () -> mappingSut.withActingUser("tok").postMapped(entity))` | unchanged |
| `updateMappedSendsTheKeyItWasGiven` (`:302`) | `mappingSut.updateMapped(entity, "tok")` | `mappingSut.withActingUser("tok").updateMapped(entity)` | unchanged: payload assertions via `ArgumentCaptor` |
| `updateMapped_throws_whenServerReportsFailure` (`:327`) | `assertThrows(..., () -> mappingSut.updateMapped(entity, "tok"))` | `assertThrows(..., () -> mappingSut.withActingUser("tok").updateMapped(entity))` | unchanged |

All 12 migrations above are pure call-syntax changes — no assertion was weakened, dropped, or altered. No test became an exact duplicate of another after collapsing the 5 overload pairs (each surviving test still exercises a distinct scenario: happy path, identity-attached, or now happy-path-with-no-identity for the renamed groupBy test).

`lenient()` markers at `EntityCoordinatorTest.java:66-71` left untouched, as instructed.

## Blocking issue — needs owner ruling

`EntityCoordinatorIdentityResolutionTest.java:182-201`, test `explicitToken_takesPrecedenceOverBoundAndAmbient`, is part of a 4-test suite (`Rule 1`/`Rule 2` block) that verifies the acting-user identity resolution order. This specific test verified **Rule 1** (an explicit per-call token overrides both the coordinator-bound and ambient identity) using `getMapped(id, depth, "explicit")` as its vehicle.

`getMapped` no longer accepts a per-call token at all — per this ruling, none of the 8 methods do anymore. So Rule 1 (explicit-call override) is now **unreachable through any public `EntityCoordinator` method** — it only remains reachable via `IversonClient.getSchema(traceId, actingUserToken)`, which two other tests in the same file already cover (`getSchema_fallsBackToAmbientIdentity_whenNoExplicitTokenGiven`, `getSchema_explicitToken_takesPrecedenceOverAmbientIdentity`, `:196+`).

Migrating this test's *call* (`sut.getMapped("some-id", 1, "explicit")` → `sut.getMapped("some-id", 1)`) is mechanical, but its *assertion* — `verify(mockMappingStub).withOption(ACTING_USER_TOKEN, "explicit")` — can no longer be true for any input, because there is no "explicit" argument to give it anymore. Migrating the call without changing the assertion is exactly the "narrow authorization" boundary I was told not to cross on my own: **the test would need to assert something different (e.g., that the bound token "bound" now reaches the stub, which duplicates existing Rule-2 coverage) rather than just change how it's called.**

I left the call updated only enough to compile (`sut.getMapped("some-id", 1)`) and did **not** touch the assertion. Per repo owner's instruction, I'm stopping and reporting rather than resolving it myself. The test now fails honestly, demonstrating the exact gap:

```
EntityCoordinatorIdentityResolutionTest.explicitToken_takesPrecedenceOverBoundAndAmbient:199
Wanted but not invoked:
mockMappingStub.withOption(acting-user-token, "explicit");
However, there was exactly 1 interaction with this mock:
mockMappingStub.withOption(acting-user-token, "bound");
mockMappingStub.get(type_name: "IdentityTestArticle" key: "some-id" depth: 1);
```

Options for the owner to choose from (not decided by me):
1. Delete this test (Rule 1 is still tested via `getSchema`; Rule 1 via `EntityCoordinator` is no longer a real code path).
2. Repurpose it to assert Rule 2 instead — but that would exactly duplicate `boundIdentity_takesPrecedenceOverAmbient` (`:143-153`), which already covers "bound overrides ambient" via `persist`.
3. Something else the owner prefers.

I did not choose an option myself, since I was told not to weaken/drop assertions or delete tests marked "redundant" on my own authority. A one-line marker comment was added at `EntityCoordinatorIdentityResolutionTest.java:189-195` pointing back to this report.

## Mutation testing (2 of 8 methods)

**`postMapped`** — broke identity routing by replacing `mappingStubFor(null).post(request)` with the bare `client.mappingStub.post(request)` at `EntityCoordinator.java:204`. Ran `mvn -f Iverson.Clients/Java/pom.xml test -pl client -am -Dtest=EntityCoordinatorTest`. Result: **`postMappedReturnsEntityHydratedFromData` failed** — `Wanted but not invoked: mockMappingStub.withOption(acting-user-token, "tok")`. Restored by hand to `mappingStubFor(null).post(request)`.

**`searchSimilar`** — broke identity routing by replacing `stubFor(null).searchSimilar(request)` with the bare `searchStub.searchSimilar(request)` at `EntityCoordinator.java:285`. Ran the same test command. Result: **`searchSimilar_withActingUserToken_usesWithOption` failed** — `Wanted but not invoked: mockStub.withOption(acting-user-token, "user-token-321")`. Restored by hand to `stubFor(null).searchSimilar(request)`.

(Note: I initially also tried mutating `getMapped` by replacing `mappingStubFor(null).get(request)` with `client.mappingStub.get(request)`. No *green* test caught it — the only test that would have caught it is the already-broken `explicitToken_takesPrecedenceOverBoundAndAmbient` above, which fails either way. Restored by hand. Not counted as one of the two required mutation results, since it doesn't demonstrate coverage by a passing test — reported here for transparency.)

## Full test summary

```
mvn -f Iverson.Clients/Java/pom.xml test
...
Tests run: 181, Failures: 1, Errors: 0, Skipped: 0
[ERROR] EntityCoordinatorIdentityResolutionTest.explicitToken_takesPrecedenceOverBoundAndAmbient:199
```

181 total (same count as before this change — no overload-pair collapse produced an exact-duplicate test to report). 180 pass; the 1 failure is the flagged, expected-per-report failure above, not a regression from the mechanical migration.

Sample module: `mvn -f Iverson.Clients/Java/pom.xml -pl sample -am compile -DskipTests` → BUILD SUCCESS.

## Addendum — owner ruling on the blocking test, applied

The repo owner ruled on the blocking issue above: **delete `explicitToken_takesPrecedenceOverBoundAndAmbient` outright**, not repurpose it, and not leave it disabled. Stated reasoning, reproduced here in full because a bare deletion in the diff would otherwise look exactly like the defect class this branch has been guarded against (a green suite hiding a removed guard):

> The ruling did not merely remove a redundant spelling of the per-call override. On `EntityCoordinator` it MERGED levels 1 and 2 of the resolution rule. A per-call override is now spelled `coordinator.withActingUser(token).getMapped(id, depth)`, which IS the bound level. So "an explicit per-call token outranks the bound one" no longer names a distinct behavior on the coordinator — there is no longer any way to express the two levels separately there, by design. The test is not inconvenient; its subject ceased to exist. Repurposing it to assert Rule 2 would exactly duplicate `boundIdentity_takesPrecedenceOverAmbient` (`:143-153` before this addendum, now shifted — see below), and a duplicate test is worse than no test — it implies coverage of something that is not there.

Actions taken, in order:

1. **Deleted** `explicitToken_takesPrecedenceOverBoundAndAmbient` from `EntityCoordinatorIdentityResolutionTest.java` outright (was `:182-201` including the owner-ruling-pending marker comment added in the prior pass). No `@Disabled`, no comment-out — the method and its body are gone.

2. **Added an explanatory comment block** immediately above `boundIdentity_takesPrecedenceOverAmbient`, replacing the old `// ── Rule 2: ... ─────` one-liner (`EntityCoordinatorIdentityResolutionTest.java:141-159`). It records: there is no separate Rule-1 suite for `EntityCoordinator` because `withActingUser(token)` merged levels 1 and 2 by design (the per-call override IS the bound-coordinator mechanism — call `withActingUser` immediately before one operation to scope it to that call without mutating the original coordinator); `boundIdentity_takesPrecedenceOverAmbient` is what covers "a caller overrides identity for a single call"; and Rule 1 as a genuinely distinct level still exists only on `IversonClient.getSchema`.

3. **Rule 1 as a distinct level, still covered where it still exists** (`IversonClient.getSchema` kept its trailing `actingUserToken` parameter by this ruling's explicit scope-out): traced to two existing tests, both untouched by this change —
   - `getSchema_fallsBackToAmbientIdentity_whenNoExplicitTokenGiven` — `EntityCoordinatorIdentityResolutionTest.java:200`
   - `getSchema_explicitToken_takesPrecedenceOverAmbientIdentity` — `EntityCoordinatorIdentityResolutionTest.java:211`

4. **`EntityCoordinator.withActingUser` javadoc** (`EntityCoordinator.java:84-90`) previously described only the bind-a-coordinator use case and referred to "a more specific (per-call explicit) token" as if that were still a separate, distinct mechanism — which is now stale given the merge. Rewrote it to state directly that `withActingUser` is also the per-call override mechanism, with the `coordinator.withActingUser(token).getMapped(id, depth)` idiom spelled out and a note that the original coordinator is left unmodified (cross-referencing `withActingUser_doesNotMutateOriginalCoordinator`, which already covers that non-mutation guarantee).

No other test was touched in this addendum, per instruction.

### Build after the ruling

```
mvn -f Iverson.Clients/Java/pom.xml test
...
Tests run: 180, Failures: 0, Errors: 0, Skipped: 0
BUILD SUCCESS
```

180 = 181 (original count before this whole ruling-3 change) − 1 (the deleted test). No other test count changed. Sample module built as part of the same reactor run (`Iverson Sample Application ... SUCCESS`), confirming it still compiles against the new signatures.

### Commit

Committed. See commit SHA reported back to the owner in this turn's reply.
