# Task 5 report — Go registry, carrier, and exclusions

## Summary

Added a `Hydrated map[string]any` read-path carrier field convention to the Go client: excluded
it from schema-metadata extraction and write-path serialization, populated it on depth-resolved
reads with typed `*T` pointers (or typed slices) resolved through a new package-level
name→`reflect.Type` registry, kept the existing `OneToMany` declared-member skip (load-bearing per
the brief), and declared the carrier on the conformance model structs that have relations.

## Changes

### `Iverson.Clients/Go/iverson/tags.go`

- `:120-131` — added `const HydratedFieldName = "Hydrated"`, documented as the well-known
  (not tag-driven) carrier field name that Task 1's conformance-verifier fallback locates by exact
  name.
- `InspectType`'s field loop (`:236-241` after the edit) — skips a field named `Hydrated` before
  any tag parsing, so it never reaches `goTypeToClr`. This is Step 1, the prerequisite for Step 5:
  without it the carrier still doesn't error registration (see the mutation-test note below), but
  it registers as a bogus scalar `Hydrated` schema property.

### `Iverson.Clients/Go/iverson/registrar.go`

- `:11-29` — added the package-level registry: `registeredTypesMu sync.RWMutex` +
  `registeredTypes map[string]reflect.Type`, and `lookupRegisteredType(name string) (reflect.Type,
  bool)`. Package-level because `structToEntity[T]` (coordinator.go) takes only a
  `*structpb.Struct` and has no `SchemaRegistrar` instance to reach through, and its 7 call sites
  in `coordinator.go` would all need a new parameter otherwise.
- `buildRequest` (`:89-91` after the edit, right after `t := reflect.TypeOf(e)` is resolved) —
  registers `registeredTypes[t.Name()] = t`. This runs on every `buildRequest` call, including from
  `propsByName`/tests that never make an RPC, since the registration side effect is pure reflection
  with no I/O dependency.

### `Iverson.Clients/Go/iverson/coordinator.go`

- `entityToStruct` (`:487-493` after the edit) — skips a field named `Hydrated` at the top of its
  field loop, before tag inspection. Step 4: the carrier is never a write-side field.
- `structToEntity[T]` (`:619-632`) — now delegates to a new `fillEntityValue(s *structpb.Struct, t
  reflect.Type) (reflect.Value, error)` and unboxes the result. Factored out so hydration can
  recurse into a related type chosen at runtime (from the registry) without a generic type
  parameter to carry it.
- `fillEntityValue` (`:634-679`) — same scalar/FK field-filling logic the old `structToEntity` had
  (including the **kept** `KindOneToMany` skip on the declared member, per the brief: `GoAuthor.
  GoArticles` is `[]string` and `protoValueToGoValue` has no struct case, so routing structs there
  would silently produce one empty string per related row), plus a new `Hydrated`-name skip, then
  calls `populateHydrated`.
- `populateHydrated(s *structpb.Struct, t reflect.Type, v reflect.Value) error` (`:681-748`) — the
  Step 3 core. If `t` declares no `Hydrated map[string]any` field, no-ops. Otherwise calls
  `InspectType` to get `meta.Relations` (best-effort: an `InspectType` error skips hydration rather
  than failing the whole read), and for each relation:
  - derives the wire key via the existing `relationPropertyName(fm)` — the same nav-property name
    the schema registers and the Task-1 verifier looks under inside `Hydrated`.
  - looks up `fm.RelatedType` in `registeredTypes`.
  - `many_to_one`/`one_to_one`: reads a single nested struct, recurses via `fillEntityValue`, boxes
    a `*RelatedType` into the map — or, if unregistered, stores `pbVal.AsInterface()` untyped.
  - `many_to_many`/`one_to_many` (**both**, per the brief): reads a list of nested structs, builds
    a `[]*RelatedType` via `reflect.MakeSlice`/`reflect.Append` — or, if unregistered, the untyped
    list.
  - Sets `v.FieldByName("Hydrated")` only if at least one relation actually hydrated.

### `Iverson.Clients/Go/conformance/models.go`

- `GoAuthor` (`:14-24`) — added `Hydrated map[string]any`, documented as the landing spot for
  hydrated `GoArticle` children since `GoArticles []string` can't hold them.
- `GoArticle` (`:32-46`) — added `Hydrated map[string]any`, documented as landing `GoAuthor`,
  `GoTags`, and `GoTag` under their wire names.
- `GoTag` was left unchanged: it declares no relations of its own, so it never needs the carrier as
  a *host* (it's only ever a related type resolved by name from the registry).

### `Iverson.Clients/Go/iverson/coordinator_test.go`

Added a fixture block mirroring the conformance model's relation shapes (`HydTag`, `HydAuthor`,
`HydArticle` — many_to_one, many_to_many, one_to_one via a second singular FK to the many_to_many's
own related type, and the reverse one_to_many — plus `HydUnregistered` for the fallback case), a
`registerHydFixtures` helper that registers `HydAuthor`/`HydArticle`/`HydTag` via `buildRequest`
directly (no RPC needed — registration is a pure reflection side effect), and 6 new tests:

- `TestBuildRequest_HydratedCarrier_RegistersSuccessfully` — Step 1/6: registration succeeds with
  a carrier-bearing struct, **and** asserts `Hydrated` is absent from the registered properties
  (the assertion that actually reddens on revert — a bare "no error" check doesn't, since
  `goTypeToClr`'s default case falls back to `CLR_STRING` rather than erroring; see below).
- `TestStructToEntity_HydratesManyToOneManyToManyAndOneToOne` — all three FK-member kinds land
  typed pointers (`*HydAuthor`, `[]*HydTag`, `*HydTag`) in `Hydrated` under their nav-property wire
  keys.
- `TestStructToEntity_OneToMany_HydratesCarrierWhileDeclaredMemberStaysEmpty` — `one_to_many`
  children land in `Hydrated["HydArticles"]` as `[]*HydArticle` while `HydAuthor.HydArticles`
  (`[]string`) stays empty.
- `TestEntityToStruct_HydratedCarrier_ExcludedFromWrite` — `entityToStruct` emits no `Hydrated` key
  while still writing `HydAuthorId`.
- `TestStructToEntity_UnregisteredRelatedType_FallsBackToUntypedChild` — clears the registry entry
  for `HydAuthor`, hydrates, and asserts `Hydrated["HydAuthor"]` is an untyped
  `map[string]interface{}` (via `pbVal.AsInterface()`), not a typed pointer, then restores the
  registry entry via `t.Cleanup`.

Also kept the pre-existing `TestStructToEntity_OneToMany_HydratedChildStructsLeaveFieldEmpty` test
untouched — it already exercised the read-side `OneToMany` skip before this task and still passes.

## Full test output (post-implementation, both changes live)

```
$ cd Iverson.Clients/Go && go test ./... -count=1
ok  	github.com/iverson/clients/go/conformance	0.025s
?   	github.com/iverson/clients/go/generated	[no test files]
ok  	github.com/iverson/clients/go/iverson	0.033s
ok  	github.com/iverson/clients/go/iverson_test	0.018s
?   	github.com/iverson/clients/go/sample	[no test files]
?   	github.com/iverson/clients/go/sample/models	[no test files]
```

Verbose run: 188 `--- PASS` lines, 0 `--- FAIL` lines, across all 3 test-bearing packages
(`iverson`, `iverson_test`, `conformance`). `gofmt -l .` and `go vet ./...` both ran clean.

## Step 7 — mutation proof (3 reverts, each run, each restored)

Backed up `iverson/tags.go` and `iverson/coordinator.go` before mutating, diffed after restoring
each time to confirm byte-identical restoration.

### Revert 1 — Step 1's `InspectType` exclusion (`tags.go`)

Removed the `if sf.Name == HydratedFieldName { continue }` guard from `InspectType`'s field loop.

```
$ go test ./iverson/... -run TestBuildRequest_HydratedCarrier_RegistersSuccessfully -v
=== RUN   TestBuildRequest_HydratedCarrier_RegistersSuccessfully
    coordinator_test.go:240: Hydrated must not be registered as a schema property, got: [name:"Id"  is_key:true name:"TenantId"  is_nullable:true name:"Title"  is_nullable:true name:"Hydrated"  is_nullable:true name:"HydAuthorId"  clr_type:CLR_GUID  is_nullable:true name:"HydTagIds"  clr_type:CLR_GUID  is_nullable:true  is_array:true name:"HydTagId"  clr_type:CLR_GUID  is_nullable:true]
--- FAIL: TestBuildRequest_HydratedCarrier_RegistersSuccessfully (0.00s)
FAIL
FAIL	github.com/iverson/clients/go/iverson	0.008s
FAIL
```

Reddened as expected. Note: registration does **not** hard-error without the exclusion (the brief's
A13 framing is about eventual server-side corruption, not a client-side panic) — `goScalarToClr`'s
`default` case returns `(CLR_STRING, false)` with no error for an unrecognized `reflect.Map` kind.
The test therefore had to assert the property's *absence*, not merely that `buildRequest` returned
no error, to actually bind the mechanism; the property-presence assertion is what reddened.
Restored `tags.go` from backup; `diff` confirmed byte-identical. Full suite re-run confirmed
188/188 passing again.

### Revert 2 — Step 4's `entityToStruct` exclusion (`coordinator.go`)

Removed the `if sf.Name == HydratedFieldName { continue }` guard from `entityToStruct`'s field
loop.

```
$ go test ./iverson/... -run TestEntityToStruct_HydratedCarrier_ExcludedFromWrite -v
=== RUN   TestEntityToStruct_HydratedCarrier_ExcludedFromWrite
    coordinator_test.go:369: Hydrated must not appear in the write-path payload, fields: map[HydAuthorId:string_value:"auth-1" HydTagId:string_value:"" HydTagIds:null_value:NULL_VALUE Hydrated:null_value:NULL_VALUE Id:string_value:"art-1" TenantId:string_value:"t1" Title:string_value:"hello"]
--- FAIL: TestEntityToStruct_HydratedCarrier_ExcludedFromWrite (0.00s)
FAIL
FAIL	github.com/iverson/clients/go/iverson	0.006s
FAIL
```

Reddened as expected (the map serializes to `null_value` under `goValueToProtoValue`'s default
case — present, wrong, and misleading on a real write). Restored the guard; re-ran full suite
(`-count=1`) to bypass Go's test cache: 188/188 passing, `diff` against the pre-mutation backup
confirmed byte-identical.

### Revert 3 — Step 3's `one_to_many` branch in `populateHydrated`

Changed `case KindManyToMany, KindOneToMany:` to `case KindManyToMany:`, dropping `one_to_many`
from the list-hydration branch.

```
$ go test ./iverson/... -run TestStructToEntity_OneToMany_HydratesCarrierWhileDeclaredMemberStaysEmpty -v
=== RUN   TestStructToEntity_OneToMany_HydratesCarrierWhileDeclaredMemberStaysEmpty
    coordinator_test.go:349: Hydrated[HydArticles] = <nil>, want []*HydArticle
--- FAIL: TestStructToEntity_OneToMany_HydratesCarrierWhileDeclaredMemberStaysEmpty (0.00s)
FAIL
FAIL	github.com/iverson/clients/go/iverson	0.005s
FAIL
```

Reddened as expected. Restored the case list; `diff` against backup confirmed byte-identical; full
suite (`-count=1`) confirmed 188/188 passing again.

## Concerns

- `populateHydrated`'s `InspectType` re-inspection on every read is a small, deliberate
  duplication of work `buildRequest` already did once at registration time (re-parsing struct tags
  per read rather than caching `EntityMeta` alongside the `reflect.Type` in the registry). It's
  correct and the read path isn't latency-sensitive in this client's existing design, but a future
  optimization pass could store `EntityMeta` in `registeredTypes` instead of re-deriving it.
- The unregistered-type fallback path (`pbVal.AsInterface()`) is exercised by one test that
  temporarily deletes a registry entry and restores it via `t.Cleanup`; this mutates
  package-level shared state during the test run. Test order matters only insofar as no other test
  depends on `HydAuthor` being registered *during* that one test's body, which is true today, but
  a future test added inside that window could be affected. Flagging for reviewer awareness rather
  than fixing, since restructuring the registry to be per-test-injectable is out of this task's
  scope (the brief specifies it must be package-level).
- No model file changes were needed for `GoTag`, `SharedAuthor`, `SharedArticle`, or `GoBadArticle`
  — none of them declare relations of their own, so none of them need to *host* the carrier (they
  can still be *resolved* as related types via the registry without one). If a future conformance
  scenario adds a relation to any of these, it will need the carrier added at that time.
