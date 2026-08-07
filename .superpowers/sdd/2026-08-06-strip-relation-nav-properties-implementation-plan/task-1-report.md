# Task 1 report — strip relation nav properties from write payloads

## Status
DONE

## Commit
`14b6765` — "strip relation nav properties from write payloads"

## Changes
- `Iverson.Server/Iverson.Api/Grpc/StructFieldAccess.cs`: added `RemoveField(Struct, string)`, mirroring `SetField`'s case-variant handling.
- `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs`: in `ValidateAndNormalizeRelations`'s loop, compute `navIsDistinctKey = !string.Equals(relation.PropertyName, relation.ForeignKey, OrdinalIgnoreCase)` before the switch, then strip the nav property after the switch only when `navIsDistinctKey` is true. Per-kind methods unchanged.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`: added `CaptureNavProperties` / `RestoreNavProperties` file-local helpers. In both `Post` and `Update`, capture immediately before `ValidateAndNormalizeRelations` and restore immediately after `SerializePayload`, so `payloadJson` (and therefore the outbox row + Kafka event) never carries the nav property, but `response.Data` (== `request.Payload`) still does.
- Tests added: 5 in `RelationValidatorTests.cs` (nav strip for ManyToOne, camelCase variant, ManyToMany, OneToMany, and the PropertyName==ForeignKey name-guard regression), 1 in `ObjectMappingGrpcServiceTests.cs` (`MappingPost_NavPropertyPresentInResponsePayload`, capturing the published `EntityEvent.PayloadJson` via the existing `_events.When(...)` pattern).

## Test suite
**633 passed / 0 failed / 633 total** (627 baseline + 6 new), full run in the foreground, ~4m17s. No StarRocks or other integration flakes observed — clean pass.

## Red-then-green evidence

### Test 6 — response-echo regression (`MappingPost_NavPropertyPresentInResponsePayload`)
- **Red:** `git stash`ed all three production files (StructFieldAccess.cs, RelationValidator.cs, ObjectMappingGrpcService.cs) back to pre-change state, keeping the new tests. Ran the test — it failed:
  ```
  Did not expect evt!.PayloadJson "...,"Author":{"Id":"..."},...` to contain ""Author"".
  ```
  (In this pre-change state the assertion on `evt.PayloadJson` not containing `"Author"` fails because nothing strips it — confirming the test actually exercises the stripping behavior, not a tautology.)
- **Green:** `git stash pop` restored the production changes; re-ran — passed. `response.Data` retains `Author` (echoed payload), `evt.PayloadJson` (what reached Kafka/outbox) does not.

### Test 5 — name-guard regression (`PropertyNameEqualsForeignKey_KeyNotStripped`)
- **Red:** With the full production change in place, temporarily replaced `if (navIsDistinctKey)` with `if (true)` in `RelationValidator.cs` (i.e. strip unconditionally, bypassing the guard). Ran the test — it failed:
  ```
  Expected payload.Fields {empty} to contain key "TagIds".
  ```
  This confirms that without the guard, a `RelationDescriptor("TagIds", ManyToMany, "Tag", "TagIds")` collision deletes the FK entirely, exactly the A7 regression scenario.
- **Green:** Restored `if (navIsDistinctKey)`; re-ran — passed, `TagIds` survives with its GUID values intact.

## Concerns
None. `dotnet build` is clean (0 errors, the one pre-existing `VectorOutput.Data` obsolete warning in `Iverson.Vector` is unrelated to this task). Task 2 can rely on `navIsDistinctKey` as specified — it is computed once per relation in the loop and is the load-bearing guard for both the strip (this task) and the upcoming conflict detection.

---

## Fix: Important finding — response-echo key-case rewrite (2026-08-06)

**Ben's ruling:** the Global Constraint ("stripping must not change what the caller gets back") is authoritative and supersedes the plan's Step 3 code block. The echoed payload must come back with the caller's exact original key spelling.

### Root cause
`CaptureNavProperties` recorded the captured value under the schema's canonical `r.PropertyName` (PascalCase) regardless of which spelling the caller actually sent. `RestoreNavProperties` then wrote it back via `StructFieldAccess.SetField`, which canonicalizes: it removes any camelCase variant and writes the PascalCase key. A caller who sent `payload["author"] = {...}` got back `response.Data["Author"]` — the key case was silently rewritten.

### Fix
`Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`:
- `CaptureNavProperties` now enumerates `StructFieldAccess.Candidates(r.PropertyName)` (canonical + camelCase) and records whichever key is actually present in the payload, tuple renamed `(Key, Value)`.
- `RestoreNavProperties` now writes directly via `payload.Fields[key] = value` instead of `StructFieldAccess.SetField`, so no canonicalization happens on restore.

No change was needed to `RelationValidator.cs` or `StructFieldAccess.cs` — the stripping logic already used `RemoveField`, which correctly removes both variants; only the *restore* path canonicalized.

### New regression test
`MappingPost_CamelCaseNavPropertyKeyPreservedInResponsePayload` (`Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`): Posts an `Article` with the nav property sent as camelCase `author`, asserts `response.Data` contains `author` and does NOT contain `Author`.

### Red-then-green evidence
- **Red:** `git stash`ed only the fixed `ObjectMappingGrpcService.cs` (keeping the new test), leaving `CaptureNavProperties`/`RestoreNavProperties` in their pre-fix (canonicalizing) form. Ran:
  ```
  dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~MappingPost_CamelCaseNavPropertyKeyPreservedInResponsePayload"
  ```
  Result: **1 failed**.
  ```
  Expected response.Data.Fields {... ["Author"] = { "Id": "11111111-..." }} to contain key "author".
  ```
  This is exactly the finding's described symptom — the canonical `Author` key comes back instead of the caller's `author`.
- **Green:** `git stash pop` restored the fix. Re-ran the same filtered command — passed.

### Full suite re-run
```
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```
Ran in the foreground (touched a shared restore-path helper used by both `Post` and `Update`, so the full Api suite was re-run rather than just the covering files). Output:
```
Passed!  - Failed: 0, Passed: 634, Skipped: 0, Total: 634, Duration: 2 m 50 s - Iverson.Api.Tests.dll (net10.0)
```
634/634 — 633 prior baseline + the 1 new regression test. No environmental flakes observed.

### Commit
`1c401ed` — "preserve caller's nav property key case when restoring echoed payload"
