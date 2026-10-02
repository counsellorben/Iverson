# TS SDK Scalar Type Resolution — Design

**Date:** 2026-10-02
**Status:** Approved design, assumptions verified
**Fixes:** the open bug recorded 2026-09-16: the TS SDK registers undecorated non-string scalars as `CLR_STRING`.

## Problem

`describeEntity` (`Iverson.Clients/TypeScript/src/core.ts:364-367`) picks a scalar property's CLR type from `design:type`, and falls back to `CLR_STRING` when there is none. Both tsc and Oxc emit `design:type` **only for decorated members**. So an undecorated `wordCount: number = 0` registers as a TEXT column. `sample/main.ts:61` then writes `500` into it, and reads return the string `"500"`.

Probing tsc 7.0.2 and Oxc (vitest 5) with the SDK's compiler flags found two more holes. Both compilers produce identical output for all 12 probed shapes:

| Declaration | `design:type` | Registered today |
|---|---|---|
| `@D() n: number = 0` | `Number` | `CLR_FLOAT` (Postgres `REAL`, ~7 significant digits) |
| `u: number = 0` (undecorated) | none | `CLR_STRING` |
| `uDate: Date = new Date()` (undecorated) | none | `CLR_STRING` |
| `@D() nNull: number \| null = null` | `Object` | `CLR_STRING` |
| `@D() dNull: Date \| null = null` | `Object` | `CLR_STRING` |
| `@D() nOpt?: number` | `Number` | `CLR_FLOAT` |
| `uOpt?: number` (undecorated) | none | `CLR_STRING` |

1. **Decorated `number` loses precision.** `Number` maps to `CLR_FLOAT`, which becomes `REAL`. So `16777217` is stored as `16777216`, and millisecond timestamps lose digits.
2. **A nullable union is silently a string.** A decorated `number | null` or `Date | null` gets `design:type` `Object`, which also falls back to `CLR_STRING`. Today no toolchain can declare a nullable number or Date column correctly.

**Why it survived:** `conformance/models.ts` has no TS-registered non-string scalar, and no SDK test asserts the CLR type of a `number`, `boolean` or `Date` property.

## Decisions (user, this session)

- **Q1 — existing data:** only disposable stacks have TS-registered tables, so a breaking registration change is acceptable. No migration tooling.
- **Q2 — `number` mapping:** TS `number` → `CLR_DOUBLE`, both decorated and undecorated. Python's `float` → `CLR_FLOAT` has the same precision problem and is **out of scope**.
- **Q3 — undeterminable type:** throw at registration and name the remedy. Never fall back silently.
- **Approach B:** inference, plus an explicit `@IversonType(ClrType.X)` escape hatch.
  - **Rejected, inference only:** it leaves `x: Date | null = null` with no remedy that keeps its meaning.
  - **Rejected, explicit only:** it breaks every initialized `number` for no gain.
- **Side-fix:** export the decorators and getters missing from the package entry (see Components).

## Design

### Resolution order

For each non-relation property, `describeEntity` resolves `clrType` by the first rule that applies:

1. `@IversonArray(elementType)` → `elementType`. `@IversonGuid()` → `CLR_GUID`. Both are unchanged, including the existing `@IversonGuid` string check.
2. `@IversonType(clrType)` → `clrType`.
3. `design:type`, when it is `String`, `Number`, `Boolean`, `Date`, `Buffer` or `Uint8Array`:
   - `String` → `CLR_STRING`
   - `Number` → **`CLR_DOUBLE`**
   - `Boolean` → `CLR_BOOL`
   - `Date` → `CLR_DATETIME`
   - `Buffer`/`Uint8Array` → `CLR_BYTES`
4. The initializer's runtime value on a fresh instance:
   - `typeof` `'string'` → `CLR_STRING`
   - `'number'` → `CLR_DOUBLE`
   - `'boolean'` → `CLR_BOOL`
   - `instanceof Date` → `CLR_DATETIME`

   `Uint8Array` is deliberately **not** inferred here (see Known issues).
5. Otherwise, throw (error 1 below).

Consequences:
- **An unrecognized `design:type` means "no information".** `Object`, `BigInt` or a class falls through to step 4. So `x: string | number = ''` resolves to `CLR_STRING`, while `x: Date | null = null` and `x: Record<string, string> = {}` throw.
- **`design:type` beats the runtime value** when both are known. A `String`-typed property with a number initializer stays `CLR_STRING`, as today.
- **Integers must be declared.** An integer column needs `@IversonType(ClrType.CLR_INT32)` or `CLR_INT64`; otherwise a `number` is always `CLR_DOUBLE`.
- **Relations are unaffected.** They are skipped before this loop runs (`core.ts:320`) and keep their `CLR_GUID` FK columns.

### Errors

All three are thrown from `describeEntity` as `new Error(...)`, in the existing `${typeName}.${fieldName} …; why; what to do` style (`core.ts:327`, `:335`, `:355`):

1. **Type can't be determined.** Covers no initializer, a `null`/`undefined` initializer, and an unrecognized initializer. The message must say that TypeScript erased the type, and must name both remedies: add an initializer, or add `@IversonType(ClrType.CLR_…)`. This mirrors the existing array error (`core.ts:326-331`).
2. **`@IversonType` on an array property.** Fires when the property also carries `@IversonArray` (co-presence, like error 3's check against `@IversonGuid`) or when it looks like an array (`design:type` `Array`, or an array initializer). Scalar only: point the user to `@IversonArray(ClrType.CLR_…)`.
3. **`@IversonType` and `@IversonGuid` on the same property.** Two type declarations: ask the user to keep one.

Deliberately not added:
- No check of `@IversonType` against `design:type`. `@IversonArray` doesn't check its element type either.
- No special case for `@IversonType(ClrType.CLR_GUID)`.
- No key-field rule. The server already rejects a non-UUID key (`SchemaRegistrationOrchestrator.cs:178`).

### Components

All three files are in `Iverson.Clients/TypeScript/src`:

- **`annotations.ts`:** add `IversonType(clrType: ClrType): PropertyDecorator` and `getTypeFields(target: Function): Map<string, ClrType>`. Build them the same way as `IversonArray`/`getArrayFields` (`:297-308`): a `Map` stored on `target.constructor` under its own `Symbol`, copied before being extended.
- **`core.ts`:**
  - `jsTypeToClr` returns `ClrType | undefined`. `'Number'` → `CLR_DOUBLE`, and the `default` arm returns `undefined`.
  - Add a runtime-value counterpart for step 4.
  - Replace the `clrType` expression at `:364-367` with the five-step order, and add errors 1–3.
- **`index.ts`:** export `IversonType` and `getTypeFields`.
  - **Side-fix (user-approved):** also export the decorators that are defined but missing from the package entry: `IversonGuid`, `IversonSummary`, `IversonKeywords`, `IversonExtracted` and `IversonEmbeddingModel`.
  - Following the file's one-getter-per-decorator convention, also export their getters: `getGuidFields`, `getSummaryFields`, `getKeywordsFields`, `getExtractedFields` and `getEmbeddingModel`.
  - The reason: `package.json` exposes only `"."`, and the server requires a UUID key. So without `IversonGuid`, a package consumer cannot register any entity. The sample and conformance only work because they import `src/annotations.js` directly.

### Testing

Vitest, in the existing suites:

- **`tests/schema-registrar.test.ts`** (uses the `propsOf` helpers at `:525`/`:617):
  - **One assertion per resolution step:**
    - undecorated initialized `number`, `boolean` and `Date` → `CLR_DOUBLE`, `CLR_BOOL` and `CLR_DATETIME`
    - decorated `number` → `CLR_DOUBLE`
    - `@IversonType(ClrType.CLR_INT32)` on a `number` → `CLR_INT32`
    - `@IversonType(ClrType.CLR_DATETIME)` on `Date | null = null` → `CLR_DATETIME`
    - `x: string | number = ''` → `CLR_STRING`
  - **Each of errors 1–3,** including `x?: number` (no initializer), `x: Date | null = null` (undecorated), undecorated `x?: string`, and error 2 on `@IversonArray(ClrType.CLR_STRING)` + `@IversonType(ClrType.CLR_STRING)` on `x: string[] | null = null`.
  - **The bug's reproduction:** an `Article`-shaped model's `WordCount` → `CLR_DOUBLE`.
- **The no-metadata (esbuild) path.** The repo's vitest emits `design:type` for decorated members, so at least one case must apply its decorators by hand. Calling `IversonX(...)(klass.prototype, key)` emits no metadata. That case has to show that steps 2 and 4 resolve without `design:type`.
- **`tests/annotations.test.ts` per-decorator table** (around `:471`): add an `IversonType / getTypeFields (Map)` row.
- **Package exports:** a test that each newly exported symbol is importable from `src/index.ts`.

Conformance is unchanged. A TS-registered non-string field would change the registered schema across all five SDKs, and Python `float` would then register differently from TS `number`. That is the cross-SDK question left out of scope in Q2.

## Breaking change

When a type is re-registered through the TS SDK, its affected column now resolves differently in these cases:
- a property with no recognized `design:type` whose initializer is a number, boolean or `Date` (was `CLR_STRING`; now `CLR_DOUBLE`, `CLR_BOOL` or `CLR_DATETIME`). A property has no recognized `design:type` when it is undecorated; when its declared type is a union such as `number | null` or `string | number`, whose `design:type` is `Object`, decorated or not; and always on the builds that emit no decorator metadata (the same builds as the last bullet; for example Oxc with `emitDecoratorMetadata` off). Examples: undecorated `wordCount: number = 0`; `@D() x: number | null = 5`; `x: string | number = 0`; and, on a no-metadata build, the sample `Article.publishedAt` (`@IversonSearchKey(1)`).
- a decorated `number` property on a build that emits decorator metadata (was `CLR_FLOAT`)
- a decorated `number | null` or `Date | null` with a `null` initializer or none (was `CLR_STRING`; it now throws until declared with `@IversonType`). With a non-null initializer it falls under the first bullet.
- any property whose type cannot be determined now throws error 1 at registration, before that type's RegisterSchema RPC is sent. This includes `string` properties that registered correctly as `CLR_STRING` until now: undecorated strings with no initializer (`x?: string`, `x!: string`, plain `x: string`), `x: string | null = null` (decorated or not), and, on builds that emit no decorator metadata (e.g. esbuild), every property without `@IversonArray`, `@IversonGuid` or `@IversonType` whose initializer is missing, `null`, or not a string, number, boolean or `Date` (for example a `Uint8Array`, `{}` or `0n`). Remedy: add an initializer or `@IversonType(ClrType.CLR_STRING)`.

If such a table already exists, re-registration fails **before any DDL** with `FailedPrecondition`: `Column "…" has type 'text' but the registered schema expects 'double precision'. Migrate the column by hand, then retry registration.` The path is `PostgresSchemaManager.cs:84-113` → `SchemaRegistrationOrchestrator.cs:344-348`.

StarRocks does `CREATE TABLE IF NOT EXISTS` only (`EngagementRepository.cs:471`), so an existing tenant table keeps its old column type. Per Q1, the remedy is to drop the affected tables (Postgres and the tenant StarRocks databases) and re-register. Record this in the commit message; the TS SDK has no README or CHANGELOG to put it in.

## Known issues / accepted as out of scope

- **`CLR_BYTES` does not round-trip from TS.** A wire probe showed `Struct` encodes a `Uint8Array` as `{"0":1,"1":2,"2":3}`. On builds that emit decorator metadata, a decorated `Uint8Array` or `Buffer` keeps registering `CLR_BYTES`, unchanged. On builds that don't, it registered `CLR_STRING` and now throws error 1, like an undecorated one. Runtime inference deliberately excludes it, so an undecorated one now throws instead of silently becoming `CLR_STRING`. — accepted by Ben, 2026-10-02
- **Python `float` → `CLR_FLOAT` (REAL)** has the same precision loss. Left out of this fix by the Q2 decision. — Ben, 2026-10-02
- **esbuild emitting no `design:type`** is carried from the 2026-09-16 investigation and was not re-probed (esbuild is not installed). The design doesn't depend on it: steps 2 and 4 work with or without metadata.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `IversonArray`'s metadata pattern can be copied | `annotations.ts:297-308` — `Map` on `target.constructor`, copied then extended |
| 2 | `index.ts` exports explicitly | `index.ts:4-29` — named list; `IversonGuid` and four other decorators absent (side-fix) |
| 3 | `jsTypeToClr` has a single caller | `grep`: only `core.ts:367` |
| 4 | No other TS code decides a scalar type | `grep design:type\|CLR_FLOAT\|clrType src/`: only `describeEntity` |
| 5 | The server accepts number/bool/ISO-date payloads into DOUBLE/BOOLEAN/TIMESTAMPTZ | Wire probe through `MappingWriteRequest` encode/decode gives `500`, `true` and an ISO string; `SchemaBuilder.cs:389-399` maps; `IntelligenceStoreConsumer.cs:743` reads REAL/DOUBLE alike |
| 6 | `Uint8Array` round-trips | **False** — wire probe; runtime arm excludes it |
| 7 | Exactly three decorators declare a type | `core.ts:363-367`: only `arrayElement`, `guidFields` and `designType` feed `clrType` |
| 8 | No server rule distinguishes FLOAT from DOUBLE for TS-reachable fields | `DecayFieldResolver.cs:44-50` needs `@IversonMetadata` + TIMESTAMPTZ (always decorated); `ObjectSearchGrpcService.cs:1284` lists both as numeric |
| 9 | No existing model throws under the new rules | Prototype of steps 3–5 in a scratch copy: 258/258 pass (4 golden-fixture ENOENTs from the copy's path), zero unresolved throws; `Article` → `WordCount=CLR_DOUBLE` |
| 10 | Enum members exist | `generated/object_mapping.ts:73-76` — `CLR_INT32=2`, `CLR_INT64=3`, `CLR_DOUBLE=4` |
| 11 | Error style | `core.ts:327`, `:335`, `:355` |
| 12 | Test helpers exist | `propsOf` at `schema-registrar.test.ts:525`/`:617`; by-hand decorator table at `annotations.test.ts:~471` |
| 13 | Docs or a changelog exist | **False** — no TS SDK README and no CHANGELOG in the repo; the breaking note goes in the commit message |
| — | `design:type` per shape, tsc vs Oxc | 12-property probe under tsc 7.0.2 and vitest 5/Oxc: identical (table in Problem) |
| — | Drift fails loudly before DDL | `PostgresSchemaManager.cs:84-113`, `SchemaRegistrationOrchestrator.cs:344-348` |
| — | Which currently-working properties newly throw | Prototype of steps 1–5 run through `describeEntity` (Oxc + by-hand no-metadata): undecorated `x?`/`x!`/plain `x: string`, `x: string \| null = null` decorated or not, and a no-metadata `o?: string` all throw error 1; `@IversonType(ClrType.CLR_STRING)` on `x?: string` and `x: string \| null = null` → `CLR_STRING`; no-metadata `@IversonGuid`/`@IversonArray`/`@IversonType` uninitialized properties resolve |
| — | Type changes for properties with no recognized `design:type` | Base vs prototype through `describeEntity`, Oxc with `emitDecoratorMetadata` on and off: `@D() x: number \| null = 5` / `Date \| null = new Date()` / `x: string \| number = 0` STRING→DOUBLE/DATETIME/DOUBLE on both; `@D() x?: number \| null` STRING→error 1; metadata-on decorated `number` FLOAT→DOUBLE and `Uint8Array`/`Buffer` BYTES unchanged; metadata-off decorated `Date`/`number`/`boolean` STRING→DATETIME/DOUBLE/BOOL and `Uint8Array`/`Buffer` STRING→error 1 |
| — | Error 2 needs co-presence detection | Prototype run through `describeEntity`: `@IversonArray` + `@IversonType` on `x: string[] \| null = null` (Oxc) and on no-metadata `x?: string[]` register silently (`isArray=true`) under `looksArray`-only detection and throw with co-presence added; `x: string[] = []` throws either way; scalar `@IversonType(CLR_INT32) n = 0` → `CLR_INT32`, unaffected |
