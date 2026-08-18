# Task 1 report: harness fallback, requirement, const

## What changed

### docs/standards/iverson-client-standard.md
- `:259` — `IVC-LIFE-007` row's Status changed to `Retired`; Statement cell left byte-identical
  ("The entity returned by a depth-resolved read is hydrated at that depth").
- `:260` (new row) — added `IVC-LIFE-008 | Active | Behaviour | The entity returned by a
  depth-resolved read carries the related object's data, including that object's own key and not
  only the foreign key`.
- New prose paragraph below the `IVC-LIFE-005` retirement note, following its shape: explains why
  `IVC-LIFE-007` is retired (its statement encoded .NET's navigation-member shape) and why
  `IVC-LIFE-008` is `Behaviour` not `Capability` (reachability is already `IVC-LIFE-006`'s Kind).
- `#### Coverage` — "Depth-resolved read hydration" Evidence cell changed from `IVC-LIFE-007` to
  `IVC-LIFE-008`.
- `#### Known non-conformance` — untouched, per the brief (Task 6 owns it); it still names
  `IVC-LIFE-007` in its prose, which Task 6 will update.

### Iverson.Server/Iverson.ClientConformance/Requirements.cs
- `LifeDepthResolvedReadHydrated` constant value changed from `"IVC-LIFE-007"` to `"IVC-LIFE-008"`;
  symbol name unchanged (no consumer changes needed).
- Doc comment above it rewritten to describe the successor's statement (observable carried data,
  not member reachability) and reasoning for staying `Behaviour`, and to describe the new
  carrier-fallback lookup path.
- Two other doc-comment references to the now-retired `IVC-LIFE-007` (in
  `LifeDepthResolvedReadReachable`'s comment and the `IVC-LIFE-005` retirement comment) updated to
  point at `IVC-LIFE-008` / note `IVC-LIFE-007`'s own retirement, so the file's cross-references
  stay accurate.

### Iverson.Server/Iverson.ClientConformance/Verifier.cs
- `VerifyDepthCapability` (was `:443`) doc comment rewritten for the successor ID and to describe
  the carrier fallback and why it is keyed on count, not absence.
- `VerifyDepthCapability`'s relation loop now calls a new private helper
  `CountHydratedObjectsForRelation(depth1Entity, propertyName)` instead of inlining
  `CountHydratedObjects(FindProperty(depth1Entity, r.PropertyName))`.
- Added `private const string HydrationCarrierPropertyName = "Hydrated";`
- Added `private static int CountHydratedObjectsForRelation(JsonElement? depth1Entity, string
  propertyName)`: tries `FindProperty(depth1Entity, propertyName)` at top level; if that count is
  0, retries via `FindProperty(FindProperty(depth1Entity, "Hydrated"), propertyName)`. Both lookups
  go through the existing `Normalize`-based `FindProperty`, so the carrier name and relation name
  inside it are matched the same normalized way as every other property lookup in this file.
- `VerifyDepthResolvedReadReachable`'s doc comment reference to `IVC-LIFE-007` updated to
  `IVC-LIFE-008`.

### Iverson.Server/Iverson.ClientConformance.Tests/VerifierTests.cs
Added three tests after the three existing `VerifyDepthCapability` cases:
- `VerifyDepthCapability_passes_when_a_relation_hydrates_only_inside_the_carrier` — top level has
  no `author` property at all; hydrated child lives only under `Hydrated.author`. Passes.
- `VerifyDepthCapability_passes_when_the_top_level_property_is_empty_but_the_carrier_holds_the_child`
  — the shadowing case: top level has `"author": null` (present, empty) AND `Hydrated.author` holds
  the real child. Passes — this is the case that would regress under an absence-keyed fallback.
- `VerifyDepthCapability_fails_when_the_relation_is_absent_from_both_top_level_and_carrier` — top
  level has no `author`, and `Hydrated` is present but empty. Fails.

## Test output (full suite, final state)

```
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj

Passed!  - Failed:     0, Passed:   217, Skipped:     0, Total:   217, Duration: 2 s - Iverson.ClientConformance.Tests.dll (net10.0)
```

## Mutation testing (Step 6)

### Mutation 1: delete the carrier retry

Changed `CountHydratedObjectsForRelation` to:
```csharp
private static int CountHydratedObjectsForRelation(JsonElement? depth1Entity, string propertyName)
{
    var topLevelCount = CountHydratedObjects(FindProperty(depth1Entity, propertyName));
    return topLevelCount;
}
```
(carrier lookup deleted). Ran the suite:

```
[xUnit.net 00:00:02.35]     Iverson.ClientConformance.Tests.VerifierTests.VerifyDepthCapability_passes_when_a_relation_hydrates_only_inside_the_carrier [FAIL]
[xUnit.net 00:00:02.37]     Iverson.ClientConformance.Tests.VerifierTests.VerifyDepthCapability_passes_when_the_top_level_property_is_empty_but_the_carrier_holds_the_child [FAIL]
  Failed Iverson.ClientConformance.Tests.VerifierTests.VerifyDepthCapability_passes_when_a_relation_hydrates_only_inside_the_carrier [319 ms]
  Error Message:
   Expected assertion.Passed to be True, but found False.
  Failed Iverson.ClientConformance.Tests.VerifierTests.VerifyDepthCapability_passes_when_the_top_level_property_is_empty_but_the_carrier_holds_the_child [7 ms]
  Error Message:
   Expected assertion.Passed to be True, but found False.

Failed!  - Failed:     2, Passed:   215, Skipped:     0, Total:   217, Duration: 2 s
```
Both carrier-dependent tests reddened, naming the relevant relation via `assertion.Passed`
expectation failure (the assertion's own `Detail` names the relation on failure — verified via the
`hydratedRelations`/`entity=` detail branch in `VerifyDepthCapability`). Restored the file from a
saved copy (`Verifier.cs.orig`, captured before mutation).

### Mutation 2: key the fallback on property absence instead of count

Changed the helper to:
```csharp
private static int CountHydratedObjectsForRelation(JsonElement? depth1Entity, string propertyName)
{
    var topLevelProperty = FindProperty(depth1Entity, propertyName);
    if (topLevelProperty is not null)
        return CountHydratedObjects(topLevelProperty);

    var carrier = FindProperty(depth1Entity, HydrationCarrierPropertyName);
    return CountHydratedObjects(FindProperty(carrier, propertyName));
}
```
Ran the suite:

```
[xUnit.net 00:00:11.50]     Iverson.ClientConformance.Tests.VerifierTests.VerifyDepthCapability_passes_when_the_top_level_property_is_empty_but_the_carrier_holds_the_child [FAIL]
  Failed Iverson.ClientConformance.Tests.VerifierTests.VerifyDepthCapability_passes_when_the_top_level_property_is_empty_but_the_carrier_holds_the_child [303 ms]
  Error Message:
   Expected assertion.Passed to be True, but found False.

Failed!  - Failed:     1, Passed:   216, Skipped:     0, Total:   217, Duration: 11 s
```
Exactly the shadowing test reddened (top-level `"author": null` is present-but-non-null-check
`is not null`, so an absence-keyed fallback stops there and never reaches the carrier) — the other
carrier test (no top-level `author` property at all) still passed under absence-keying, as
expected, since absence-keying does reach the carrier when the property is truly missing.

Restored the file to the correct implementation from the saved copy. Final suite run after restore
(shown above) is 217/217 green.

## Concerns

- None outstanding for this task's scope. The `Known non-conformance` prose still names
  `IVC-LIFE-007` — left alone intentionally per the brief; Task 6 owns rewriting that sentence and
  the surrounding section.
- Doc-comment cross-references to `IVC-LIFE-007`/`IVC-LIFE-008` elsewhere in `Requirements.cs` and
  `Verifier.cs` were updated beyond the brief's literal Step 3/Step 4 scope (two extra doc-comment
  sentences) to keep the file internally consistent now that `IVC-LIFE-007` is retired — this is a
  documentation-only change, not a behavior or interface change, and should not affect later tasks.
