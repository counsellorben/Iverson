# Task 2 report — error when the foreign key and nav property disagree

**Commit:** `715e7a8` — "error when a relation's foreign key and nav property disagree"
(on branch `worktree-relation-properties-write-path`, parent `1c401ed`)

## What changed

`Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs`:

- Added `KeyColumnNameFor(relatedTypeName)` — extracted from the old `ValidateNestedObject`.
- Added `ReadNestedKey(nested, relatedTypeName)` — non-enforcing, parses a nested object's key
  into a `Guid?`, records no error. Used by both the normalize path (via `ValidateNestedObject`)
  and the new conflict-detection path.
- `ValidateNestedObject` rebuilt on top of `ReadNestedKey`; contract unchanged (still enforces the
  key-only rule, still returns the *raw* key string for FK normalization).
- `ValidateSingleRelation` (ManyToOne/OneToOne) gained a `bool navIsDistinctKey` parameter. When
  the FK is present and valid, it now reads the nav value (gated on the guard) and, if it carries
  a *readable* key that disagrees with the FK, adds an error naming both the nav property and the
  FK: `'{PropertyName}' references '{navKey}' but '{ForeignKey}' is '{fk}'. Remove one, or make
  them agree.` A nav object with no usable key is treated as "no second opinion" — the FK stands,
  no error.
- `ValidateCollectionRelation` (ManyToMany) gained the same parameter. When the FK list is present
  and every element parses, a *non-empty* nav list is cross-checked as a set (order-independent);
  disagreement (including a subset/superset) adds `'{PropertyName}' and '{ForeignKey}' disagree.
  Remove one, or make them agree.` An empty nav list means "not supplied" and is not cross-checked
  (this is also the existing empty-list-clears-FK contract from Task 1, preserved).
- Both call sites in the `switch` in `ValidateAndNormalizeRelations` now forward `navIsDistinctKey`.

`Iverson.Server/Iverson.Api.Tests/Grpc/RelationValidatorTests.cs`: added the 8 tests from the
brief's Step 6, plus one necessary fix to a pre-existing test (see Deviation below).

## Deviation from the brief

The brief's test list was implemented verbatim, but running the full suite after implementing
turned up a **pre-existing** test whose premise the brief could not have known was now false:
`ValidateAndNormalizeRelations_ManyToOne_ForeignKeyAlreadyPresent_NavPropertyIgnored`. It set an
FK and a nav object carrying a **different, unrelated** GUID, and asserted the FK stood unchanged
— i.e. it was pinning the exact silent-precedence behavior this task exists to remove. With the
Task 2 change, that scenario now correctly throws (`RpcException`), which is the intended
behavior, not a regression.

I updated that test's nested key to equal the FK (so it now exercises "FK already present, nav
property matches, cross-check passes and FK is not overridden by the nav object's key") and
renamed it to `ForeignKeyAlreadyPresent_MatchingNavPropertyNotOverridden`, with a comment
explaining the change and pointing at `ManyToOne_NavObjectDisagreesWithFk_Throws` for the
disagreement coverage the old test no longer provides. This is the smallest fix that keeps the
test suite internally consistent with the task's mandated behavior change; I did not touch any
other pre-existing test or any file outside `RelationValidator.cs` / `RelationValidatorTests.cs`.

No other deviations. `RelationDescriptor` was never spelled out explicitly (inferred from
`schema.Relations` / existing local variables, matching the note in the assignment). The
`navIsDistinctKey` expression was not extracted into a shared helper — it now appears in three
places (`RelationValidator`'s top-level loop, and both per-kind methods receive it as a parameter
from that one computation), which matches "third use is expected and deferred," since the
computation itself still lives in exactly one place (the top-level loop) and is only *passed
through*, not recomputed.

## Red-then-green evidence

Isolated `RelationValidator.cs` from the test file with `git stash push -- .../RelationValidator.cs`
(keeping the new tests in place), then ran:

```
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~RelationValidatorTests"
```

**Red** (production code stashed, only old + new test file present):

```
[xUnit.net]     Iverson.Api.Tests.Grpc.RelationValidatorTests.ManyToOne_NavObjectDisagreesWithFk_Throws [FAIL]
  Error Message: Expected a <Grpc.Core.RpcException> to be thrown, but no exception was thrown.
[xUnit.net]     Iverson.Api.Tests.Grpc.RelationValidatorTests.ManyToMany_NavListSubsetOfFkList_Throws [FAIL]
  Error Message: Expected a <Grpc.Core.RpcException> to be thrown, but no exception was thrown.

Failed!  - Failed: 2, Passed: 31, Skipped: 0, Total: 33
```

(The other 6 new tests already passed against the old code, since they describe no-throw /
pass-through cases that the old code also satisfied — only the two `_Throws` tests actually guard
new behavior.)

`git stash pop` restored `RelationValidator.cs`.

**Green** (production code restored):

```
Passed!  - Failed: 0, Passed: 33, Skipped: 0, Total: 33, Duration: 400 ms
```

## Rejection-test message assertions

Confirming explicitly, per the review flag about a sibling test on this branch: both new
Task-2 rejection tests assert on `Status.Detail` content naming the offending properties, not a
bare `Throw<RpcException>()`:

- `ManyToOne_NavObjectDisagreesWithFk_Throws` (line 475-476):
  `.Where(e => e.Status.Detail.Contains("'Author'") && e.Status.Detail.Contains("'AuthorId'"))`
- `ManyToMany_NavListSubsetOfFkList_Throws` (line 559-560):
  `.Where(e => e.Status.Detail.Contains("'Tags'") && e.Status.Detail.Contains("'TagIds'"))`

## Full suite

```
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

```
Passed!  - Failed: 0, Passed: 642, Skipped: 0, Total: 642, Duration: 2 m
```

642, not the brief's predicted 641 — Task 1 landed at 634 (not 633 as the brief assumed), plus
this task's net +8 (9 new tests, 0 removed — the one pre-existing test was edited in place, not
added or removed) = 642.

## Anything a reviewer should know

- The "FK stands, no error" behavior when the nav object carries no usable key
  (`ManyToOne_NavObjectWithNoUsableKey_ForeignKeyStands`) is deliberate per the brief's
  `ReadNestedKey` doc comment — an unreadable nested key is "no second opinion," not a conflict.
  This means a nav object with a garbage/missing key alongside a valid FK is silently accepted
  (the nav object itself is still stripped from the payload afterward, per Task 1's stripping
  step, so nothing bad is persisted — it just isn't flagged as an error either).
- `ValidateCollectionRelation`'s `fkAllValid` local (not `errors.Count`) gates the cross-check, per
  the brief's Step 4 note, so an earlier relation's errors in the same `errors` list can't
  spuriously suppress or trigger this relation's cross-check.
- The one edited pre-existing test changes what scenario it exercises (disagreement → agreement);
  I did not delete or weaken its assertion, only made its premise consistent with the new
  contract.
