# TS SDK Scalar Type Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-02-ts-sdk-scalar-type-resolution-design.md` (commit SHA: `42027b479b4af1c12668f185a3eecd3465a87f27`)

**Goal:** Make the TypeScript SDK register every scalar property with its real column type. Today undecorated `number`/`boolean`/`Date` properties and nullable unions silently become `CLR_STRING`, and a decorated `number` becomes `CLR_FLOAT`. Undeterminable types should throw at registration instead of being guessed.

**Architecture:** `describeEntity` resolves each non-relation property's `clrType` through a five-step order:
1. `@IversonArray` / `@IversonGuid`
2. a new `@IversonType(ClrType.X)`
3. a recognized `design:type`
4. the initializer's runtime value
5. otherwise, throw

`Number` maps to `CLR_DOUBLE`. Three new errors cover:
- an undeterminable type
- `@IversonType` on an array
- `@IversonType` combined with `@IversonGuid`

The package entry also exports `IversonType` and the five annotation decorators (with their getters) it never exposed.

**Tech stack:**
- TypeScript 7.0.2 with legacy `experimentalDecorators` + `emitDecoratorMetadata`, and `reflect-metadata`
- vitest 5 on Oxc with `decorator: { legacy: true, emitDecoratorMetadata: true }`
- ts-proto-generated `ClrType` enum

All paths below are relative to `Iverson.Clients/TypeScript/` unless they start with `docs/`. All commands run from that directory. The `:NNN` line references are positions at HEAD `42027b47`, before any task runs. Earlier steps shift them, so the quoted text anchors in each step govern.

---

## File Structure

**Modify:**
- `src/annotations.ts`: add `IversonType(clrType)` and `getTypeFields(target)` (Task 1).
- `src/core.ts`:
  - `jsTypeToClr` returns `ClrType | undefined`, with `Number` → `CLR_DOUBLE`.
  - New `runtimeValueToClr`.
  - The `describeEntity` resolution order plus errors 1–3 (Task 2).
- `src/index.ts`: export `IversonType`/`getTypeFields`, plus `IversonGuid`, `IversonSummary`, `IversonKeywords`, `IversonExtracted`, `IversonEmbeddingModel` and their getters (Task 3).

**Test:**
- `tests/annotations.test.ts`: a new row in the `accumulateSites` table (Task 1) and a `package entry exports` block (Task 3).
- `tests/schema-registrar.test.ts`: a new `_buildRequest — scalar type resolution` block (Task 2).

No files are created.

## Inherited from spec

`thorough-brainstorming` verified these assumptions at spec-write time. They are NOT re-verified here and are trusted as ground truth:

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

## Verified plan-level assumptions

These were introduced by this plan and verified at plan-write time (2026-10-02, HEAD `42027b47`). The decisive check: all three tasks were applied, exactly as written below, to a scratch copy of `Iverson.Clients/TypeScript` (with `../Common` linked). Each task's tests ran before and after its change.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | The five files exist at the paths above | `ls`: `src/annotations.ts` 19473 B, `src/core.ts` 45046 B, `src/index.ts` 1890 B, `tests/annotations.test.ts` 19095 B, `tests/schema-registrar.test.ts` 33176 B |
| 2 | Signature | `annotations.ts` already imports `ClrType` | `src/annotations.ts:22` `import { ClrType } from '../generated/object_mapping.js';` |
| 3 | Signature | A row in the `accumulateSites` table (`label`/`decorate`/`has`/`size`) generates its own tests | `tests/annotations.test.ts` `interface AccumulateSite` and `:495` `describe.each(accumulateSites)`. On the prototype the new row ran 4 tests (verbose reporter), and the file went from 75 to 79 tests |
| 4 | Signature | `tests/annotations.test.ts` imports `describe`/`it`/`expect`, `ClrType` and an annotation list containing `getGuidFields` | `:6` vitest import; `:37` `    getGuidFields,`; `:44` `ClrType` import |
| 5 | Signature | `propsOf` is **local** to each `describe` block (`:525`, `:617`), not shared | Read of both blocks. The new block therefore defines its own `propsOf`, in the `:617` shape (returns the props map), and throw tests wrap `propsOf(...)`, matching `_buildRequest — array fields` (`:703-763`) |
| 6 | Signature | `RegArticle` (module scope in `schema-registrar.test.ts`) has an undecorated `wordCount: number = 0` | Read `:55-70`. The bug-reproduction case reuses it instead of declaring a new model |
| 7 | Signature | `tests/schema-registrar.test.ts` imports `IversonEntity`, `IversonKey`, `IversonDescription`, `IversonArray`, `IversonGuid` (`:21`), `SchemaRegistrar` and `ClrType`; `makeStub` is module-scope | Read of `:7-36`; `makeStub` used by the existing blocks at `:526`, `:618`, `:717` |
| 8 | Signature | `core.ts` imports `getGuidFields` from `./annotations.js` (`:57`); `describeEntity` defines `guidFields` (`:265`), and in the loop `designType`, `arrayElement`, `looksArray` (`:325`), `instance`, `typeName` and `fieldName`. `looksArray` is computed before the missing-`@IversonArray` check (`:326`) | Read of `core.ts:247-367` |
| 9 | Signature | `jsTypeToClr` is at `core.ts:82-92`, with `'Number'` → `CLR_FLOAT` and `default` → `CLR_STRING` | Read |
| 10 | Command | `npm test` = `npm run typecheck && vitest run`; `typecheck` = `tsc -p tsconfig.test.json`, which includes `tests/`, `sample/` and `conformance/`; `./node_modules/.bin/vitest run <file>` runs one file | `package.json:15-16`; `tsconfig.test.json` `include` |
| 11 | Code validity | Strict typecheck accepts every test shape (`x?: number`, `x: Date \| null = null`, `x?: string`, `x: string \| number = ''`, `x: string[] \| null = null`, by-hand `e?: Date`) and the `pkg as Record<string, unknown>` cast | Prototype: `npm run typecheck` exit 0 after each task |
| 12 | Code validity | The `??` chain resolves correctly, including `CLR_STRING`, and error messages match the test regexes | Prototype: all 15 new resolution tests and all 12 export tests pass |
| 13 | Code validity | A class with no syntactic decorators gets no `design:type`; `IversonEntity()(K)` / `IversonKey()(K.prototype, 'id')` by hand satisfy `describeEntity`'s `isIversonEntity` check (`core.ts:248`) | Prototype: the no-metadata case passes (`N`/`D`/`B` from step 4, `E` from step 2) |
| 14 | Ordering | Task 1 has no dependencies. Task 2 imports `getTypeFields`/`IversonType` (Task 1). Task 3 exports Task 1's symbols and does not touch Task 2's code. Tasks 1 and 3 both edit `tests/annotations.test.ts`, so run them in order 1 → 2 → 3 | Diff of each task's hunks on the prototype |
| 15 | TDD red | Each task's new tests fail before its source change | Prototype: Task 1 needs the symbol for its row. Task 2 is 13 failed, 50 passed: the `string \| number = ''` and design-type-beats-initializer cases already pass and are regression guards. Task 3 is 12 failed, 79 passed |
| 16 | Consumer impact | `Number` → `CLR_DOUBLE`, `default` → `undefined` (then error 1), and error 2 placed before the existing missing-`@IversonArray` check break no existing test, sample or conformance model | Prototype after all tasks: `npm test` exit 0, **293/293** (262 baseline + 4 + 15 + 12). `tsc -p tsconfig.conformance.json --noEmit` exit 0, and the build's `tsc --noEmit` exit 0 |
| 17 | Consumer impact | None of the 12 new `index.ts` exports collides with an existing export | `src/index.ts` exports none of them today; its other `export` lines name `core.js`/builder symbols only |
| 18 | Sibling set | All 12 exported names exist in `annotations.ts` under exactly those names | `grep "export function"`: `IversonEmbeddingModel` `:86`, `getEmbeddingModel` `:92`, `IversonSummary` `:183`, `getSummaryFields` `:192`, `IversonKeywords` `:199`, `getKeywordsFields` `:208`, `IversonExtracted` `:230`, `getExtractedFields` `:246`, `IversonGuid` `:319`, `getGuidFields` `:328`; `IversonType`/`getTypeFields` come from Task 1 |
| 19 | Convention | Commit messages are lowercase imperative with no prefix | `git log --oneline -- Iverson.Clients/TypeScript`: `add the typescript matchPattern builder and client method`, `add the match-pattern steps to the five conformance drivers` |
| 20 | Test falsifiability | Task 2's tests kill the two mutants CIR-1 found surviving the original 13 tests | Prototype, one mutant at a time against finished Task 2: `default: return ClrType.CLR_STRING` in `jsTypeToClr` (M1) fails only `resolves a decorated nullable union from its non-null initializer`; running `runtimeValueToClr` before `jsTypeToClr` (M5) fails only `prefers design:type over the initializer when both are known` |

## Tasks

### Task 1: `@IversonType(clrType)` decorator

**Files:**
- Modify: `src/annotations.ts` (insert after `getGuidFields`, `:328-330`)
- Test: `tests/annotations.test.ts` (import list `:7-43`; `accumulateSites` row before the `IversonGuid` row at `:476`)

**Interfaces:**
- Produces: `IversonType(clrType: ClrType): PropertyDecorator` and `getTypeFields(target: Function): Map<string, ClrType>`, consumed by Tasks 2 and 3.

- [ ] **Step 1: Add the table row (test first)**

In `tests/annotations.test.ts`, add two names to the `from '../src/annotations.js'` import list, right after `    getGuidFields,`:

```ts
    IversonType,
    getTypeFields,
```

In `accumulateSites`, insert this row immediately before the `label: 'IversonGuid / getGuidFields (Set)'` row:

```ts
    {
        label: 'IversonType / getTypeFields (Map)',
        decorate: (klass, key) => { IversonType(ClrType.CLR_INT32)(klass.prototype, key); },
        has: (target, key) => getTypeFields(target).has(key),
        size: (target) => getTypeFields(target).size,
    },
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `./node_modules/.bin/vitest run tests/annotations.test.ts`
Expected: FAIL. `IversonType` is not exported from `src/annotations.ts`.

- [ ] **Step 3: Add the decorator**

In `src/annotations.ts`, directly after the `getGuidFields` function (before the `// ── @IversonDescription(text)` banner), insert:

```ts

// ── @IversonType(clrType) ─────────────────────────────────────────────────────

const IVERSON_TYPE_KEY = Symbol('iverson:type');

/**
 * Declares a scalar property's column type explicitly. Needed when the type cannot be
 * inferred: TypeScript erases it (no design:type for undecorated members, `Object` for
 * unions such as `number | null`), and a missing or null initializer carries none.
 * Scalar-only — an array column uses @IversonArray(elementType).
 */
export function IversonType(clrType: ClrType): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: Map<string, ClrType> =
            new Map(Reflect.getMetadata(IVERSON_TYPE_KEY, target.constructor) ?? []);
        existing.set(String(propertyKey), clrType);
        Reflect.defineMetadata(IVERSON_TYPE_KEY, existing, target.constructor);
    };
}

export function getTypeFields(target: Function): Map<string, ClrType> {
    return Reflect.getMetadata(IVERSON_TYPE_KEY, target) ?? new Map();
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `./node_modules/.bin/vitest run tests/annotations.test.ts && npm run typecheck`
Expected: PASS, 79 tests (75 before plus the row's 4); typecheck exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/annotations.ts tests/annotations.test.ts
git commit -m "add the typescript IversonType decorator"
```

### Task 2: Five-step scalar type resolution and errors 1–3

**Files:**
- Modify: `src/core.ts`:
  - `jsTypeToClr` at `:82-92`
  - the annotations import at `:57`
  - `describeEntity`'s `guidFields` at `:265`, `looksArray` at `:325`, and `clrType` at `:364-367`
- Test: `tests/schema-registrar.test.ts` (import list at `:21`; new block before `// ── IversonClient.getSchema` at `:764`)

**Interfaces:**
- Consumes: `IversonType`, `getTypeFields` (Task 1).

- [ ] **Step 1: Write the tests**

In `tests/schema-registrar.test.ts`, add `    IversonType,` to the `from '../src/annotations.js'` import list, right after `    IversonGuid,`. Then insert this block immediately before the `// ── IversonClient.getSchema` banner:

```ts
describe('_buildRequest — scalar type resolution', () => {
    function propsOf(cls: Function) {
        const registrar = new SchemaRegistrar(makeStub(), [cls]);
        const req = registrar._buildRequest(cls);
        return Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));
    }

    it('infers undecorated number, boolean and Date from their initializers', () => {
        @IversonEntity()
        class ScalarInitialized {
            @IversonKey()
            id: string = '';

            n: number = 0;
            b: boolean = true;
            d: Date = new Date();
        }

        const props = propsOf(ScalarInitialized);
        expect(props['N'].clrType).toBe(ClrType.CLR_DOUBLE);
        expect(props['B'].clrType).toBe(ClrType.CLR_BOOL);
        expect(props['D'].clrType).toBe(ClrType.CLR_DATETIME);
    });

    it('maps a decorated number to CLR_DOUBLE', () => {
        @IversonEntity()
        class DecoratedNumber {
            @IversonKey()
            id: string = '';

            @IversonDescription('x')
            n: number = 0;
        }

        expect(propsOf(DecoratedNumber)['N'].clrType).toBe(ClrType.CLR_DOUBLE);
    });

    it('resolves a string | number union from its string initializer', () => {
        @IversonEntity()
        class StringOrNumber {
            @IversonKey()
            id: string = '';

            x: string | number = '';
        }

        expect(propsOf(StringOrNumber)['X'].clrType).toBe(ClrType.CLR_STRING);
    });

    it('resolves a decorated nullable union from its non-null initializer', () => {
        @IversonEntity()
        class InitializedUnion {
            @IversonKey()
            id: string = '';

            @IversonDescription('x')
            x: number | null = 5;
        }

        expect(propsOf(InitializedUnion)['X'].clrType).toBe(ClrType.CLR_DOUBLE);
    });

    it('prefers design:type over the initializer when both are known', () => {
        @IversonEntity()
        class StringTypedNumberInit {
            @IversonKey()
            id: string = '';

            @IversonDescription('x')
            x: string = 0 as unknown as string;
        }

        expect(propsOf(StringTypedNumberInit)['X'].clrType).toBe(ClrType.CLR_STRING);
    });

    it('lets @IversonType override the inferred type', () => {
        @IversonEntity()
        class DeclaredInt {
            @IversonKey()
            id: string = '';

            @IversonType(ClrType.CLR_INT32)
            n: number = 0;
        }

        expect(propsOf(DeclaredInt)['N'].clrType).toBe(ClrType.CLR_INT32);
    });

    it('lets @IversonType declare a nullable Date', () => {
        @IversonEntity()
        class DeclaredNullableDate {
            @IversonKey()
            id: string = '';

            @IversonType(ClrType.CLR_DATETIME)
            d: Date | null = null;
        }

        expect(propsOf(DeclaredNullableDate)['D'].clrType).toBe(ClrType.CLR_DATETIME);
    });

    it('registers an undecorated number initializer as CLR_DOUBLE, not CLR_STRING', () => {
        expect(propsOf(RegArticle)['WordCount'].clrType).toBe(ClrType.CLR_DOUBLE);
    });

    it('resolves @IversonType and initializers when the build emits no decorator metadata', () => {
        // No syntactic decorators, so no build emits design:type for this class; the decorators
        // are applied by hand, as an esbuild-style consumer build would leave them.
        class NoMetadata {
            id: string = '';
            n: number = 0;
            d: Date = new Date();
            b: boolean = true;
            e?: Date;
        }
        IversonEntity()(NoMetadata);
        IversonKey()(NoMetadata.prototype, 'id');
        IversonType(ClrType.CLR_DATETIME)(NoMetadata.prototype, 'e');

        const props = propsOf(NoMetadata);
        expect(props['N'].clrType).toBe(ClrType.CLR_DOUBLE);
        expect(props['D'].clrType).toBe(ClrType.CLR_DATETIME);
        expect(props['B'].clrType).toBe(ClrType.CLR_BOOL);
        expect(props['E'].clrType).toBe(ClrType.CLR_DATETIME);
    });

    it('throws when an optional number has no initializer', () => {
        @IversonEntity()
        class OptionalNumber {
            @IversonKey()
            id: string = '';

            x?: number;
        }

        expect(() => propsOf(OptionalNumber)).toThrow(/OptionalNumber\.x has no type .*cannot be inferred/);
    });

    it('throws when an undecorated nullable Date is initialized to null', () => {
        @IversonEntity()
        class NullDate {
            @IversonKey()
            id: string = '';

            x: Date | null = null;
        }

        expect(() => propsOf(NullDate)).toThrow(/NullDate\.x has no type .*cannot be inferred/);
    });

    it('throws when an optional string has no initializer', () => {
        @IversonEntity()
        class OptionalString {
            @IversonKey()
            id: string = '';

            x?: string;
        }

        expect(() => propsOf(OptionalString)).toThrow(/OptionalString\.x has no type .*cannot be inferred/);
    });

    it('throws when @IversonType sits on a nullable @IversonArray property', () => {
        @IversonEntity()
        class TypedNullableArray {
            @IversonKey()
            id: string = '';

            @IversonArray(ClrType.CLR_STRING)
            @IversonType(ClrType.CLR_STRING)
            x: string[] | null = null;
        }

        expect(() => propsOf(TypedNullableArray)).toThrow(/@IversonType\(\) is scalar-only/);
    });

    it('throws when @IversonType sits on an array property without @IversonArray', () => {
        @IversonEntity()
        class TypedArray {
            @IversonKey()
            id: string = '';

            @IversonType(ClrType.CLR_STRING)
            x: string[] = [];
        }

        expect(() => propsOf(TypedArray)).toThrow(/@IversonType\(\) is scalar-only/);
    });

    it('throws when @IversonType and @IversonGuid both declare the type', () => {
        @IversonEntity()
        class TypedGuid {
            @IversonKey()
            id: string = '';

            @IversonGuid()
            @IversonType(ClrType.CLR_GUID)
            x: string = '';
        }

        expect(() => propsOf(TypedGuid)).toThrow(/both @IversonGuid\(\) and @IversonType\(\)/);
    });
});

```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `./node_modules/.bin/vitest run tests/schema-registrar.test.ts`
Expected: 13 failed, 50 passed. Today `IversonType` is ignored, `Number` → `CLR_FLOAT`, undecorated scalars and `Object`-typed unions → `CLR_STRING`, and nothing throws. Two cases already pass and are regression guards: `string | number = ''`, and the design-type-beats-initializer case.

- [ ] **Step 3: Rewrite `jsTypeToClr` and add the step-4 helper**

In `src/core.ts`, replace the whole `jsTypeToClr` function (`:82-92`) with:

```ts
function jsTypeToClr(typeName: string): ClrType | undefined {
    switch (typeName) {
        case 'String':   return ClrType.CLR_STRING;
        case 'Number':   return ClrType.CLR_DOUBLE;
        case 'Boolean':  return ClrType.CLR_BOOL;
        case 'Date':     return ClrType.CLR_DATETIME;
        case 'Buffer':
        case 'Uint8Array': return ClrType.CLR_BYTES;
        default:         return undefined;
    }
}

/** Step-4 inference from an initializer's runtime value; undefined when it identifies no type. */
function runtimeValueToClr(value: unknown): ClrType | undefined {
    if (typeof value === 'string')  return ClrType.CLR_STRING;
    if (typeof value === 'number')  return ClrType.CLR_DOUBLE;
    if (typeof value === 'boolean') return ClrType.CLR_BOOL;
    if (value instanceof Date)      return ClrType.CLR_DATETIME;
    return undefined;
}
```

- [ ] **Step 4: Wire the resolution order and errors into `describeEntity`**

In the `from './annotations.js'` import list, add `    getTypeFields,` right after `    getGuidFields,` (`:57`).

After `    const guidFields = getGuidFields(cls);` (`:265`), add:

```ts
    const typeFields = getTypeFields(cls);
```

Immediately after `        const looksArray = designType === Array || Array.isArray(instance[fieldName]);` (`:325`), and before the existing `if (looksArray && arrayElement === undefined)` check, insert errors 2 and 3. They come first so that `@IversonType` on an array always reports error 2.

```ts
        const declaredType = typeFields.get(fieldName);
        if (declaredType !== undefined && (looksArray || arrayElement !== undefined)) {
            throw new Error(
                `${typeName}.${fieldName} is an array property but is decorated with @IversonType(); ` +
                '@IversonType() is scalar-only. Remove it and declare the element type with ' +
                '@IversonArray(ClrType.CLR_…).',
            );
        }
        if (declaredType !== undefined && guidFields.has(fieldName)) {
            throw new Error(
                `${typeName}.${fieldName} is decorated with both @IversonGuid() and @IversonType(); ` +
                'each declares the column type. Keep one.',
            );
        }
```

Replace the `clrType` expression (`:364-367`):

```ts
        const clrType = arrayElement
            ?? (guidFields.has(fieldName)
                ? ClrType.CLR_GUID
                : (designType ? jsTypeToClr(designType.name) : ClrType.CLR_STRING));
```

with:

```ts
        const clrType = arrayElement
            ?? (guidFields.has(fieldName) ? ClrType.CLR_GUID : undefined)
            ?? declaredType
            ?? (designType !== undefined ? jsTypeToClr(designType.name) : undefined)
            ?? runtimeValueToClr(instance[fieldName]);
        if (clrType === undefined) {
            throw new Error(
                `${typeName}.${fieldName} has no type the SDK can read, so its column type cannot be inferred: ` +
                'TypeScript erases it (decorator metadata is absent, or a union such as `T | null` reports Object), ' +
                'and the initializer is missing, null, or not a string, number, boolean or Date. ' +
                'Give it an initializer, or add @IversonType(ClrType.CLR_…) naming the type.',
            );
        }
```

Leave the `const isArray = arrayElement !== undefined;` line above it unchanged.

- [ ] **Step 5: Run the full suite and the conformance typecheck**

Run: `npm test && ./node_modules/.bin/tsc -p tsconfig.conformance.json --noEmit`
Expected: typecheck exit 0; `Tests  281 passed (281)`; conformance typecheck exit 0.

- [ ] **Step 6: Commit (the breaking-change note goes in the body; the SDK has no README or CHANGELOG)**

```bash
git add src/core.ts tests/schema-registrar.test.ts
git commit \
  -m "resolve typescript scalar column types from metadata, initializer or @IversonType" \
  -m "describeEntity now resolves a scalar property's column type from @IversonArray/@IversonGuid, then @IversonType, then a recognized design:type, then the initializer's runtime value, and throws when none applies. A TypeScript number now registers as CLR_DOUBLE instead of CLR_FLOAT." \
  -m "BREAKING: re-registering an existing type changes these columns. (1) A property with no recognized design:type (undecorated; a union such as number | null or string | number, decorated or not; or any property on a build that emits no decorator metadata) whose initializer is a number, boolean or Date was CLR_STRING and is now CLR_DOUBLE, CLR_BOOL or CLR_DATETIME. (2) A decorated number on a metadata build was CLR_FLOAT and is now CLR_DOUBLE. (3) A decorated number | null or Date | null with a null initializer or none now throws until declared with @IversonType. (4) Any property whose type cannot be determined now throws before that type's RegisterSchema RPC, including undecorated strings with no initializer, x: string | null = null, and, on no-metadata builds, any property with no non-null initializer and none of @IversonArray, @IversonGuid or @IversonType. Remedy: add an initializer or @IversonType(ClrType.CLR_...)." \
  -m "An existing table fails re-registration before any DDL with FailedPrecondition (column type drift). StarRocks keeps the old column type. Drop the affected Postgres table and tenant StarRocks tables, then re-register."
```

### Task 3: Export `IversonType` and the missing annotation decorators

**Files:**
- Modify: `src/index.ts` (the `export { … } from './annotations.js'` list at `:4-31`)
- Test: `tests/annotations.test.ts` (an import after `:44`; a new block at the end of the file)

**Interfaces:**
- Consumes: `IversonType`, `getTypeFields` (Task 1).

- [ ] **Step 1: Write the test**

In `tests/annotations.test.ts`, directly after `import { ClrType } from '../generated/object_mapping.js';`, add:

```ts
import * as pkg from '../src/index.js';
```

Append to the end of the file:

```ts

describe('package entry exports', () => {
    it.each([
        'IversonType', 'getTypeFields',
        'IversonGuid', 'getGuidFields',
        'IversonSummary', 'getSummaryFields',
        'IversonKeywords', 'getKeywordsFields',
        'IversonExtracted', 'getExtractedFields',
        'IversonEmbeddingModel', 'getEmbeddingModel',
    ])('exports %s', name => {
        expect(typeof (pkg as Record<string, unknown>)[name]).toBe('function');
    });
});
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `./node_modules/.bin/vitest run tests/annotations.test.ts`
Expected: 12 failed, 79 passed.

- [ ] **Step 3: Add the exports**

In `src/index.ts`, right after `    IversonArray,`, add:

```ts
    IversonType,
    IversonGuid,
    IversonSummary,
    IversonKeywords,
    IversonExtracted,
    IversonEmbeddingModel,
```

Right after `    getArrayFields,`, add:

```ts
    getTypeFields,
    getGuidFields,
    getSummaryFields,
    getKeywordsFields,
    getExtractedFields,
    getEmbeddingModel,
```

- [ ] **Step 4: Run the full suite, the conformance typecheck and the build typecheck**

Run: `npm test && ./node_modules/.bin/tsc -p tsconfig.conformance.json --noEmit && ./node_modules/.bin/tsc --noEmit`
Expected: `Tests  293 passed (293)`; both `tsc` runs exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/index.ts tests/annotations.test.ts
git commit -m "export IversonType and the missing annotation decorators from the typescript package entry"
```

## Known issues inherited from spec

- **`CLR_BYTES` does not round-trip from TS.** A wire probe showed `Struct` encodes a `Uint8Array` as `{"0":1,"1":2,"2":3}`. On builds that emit decorator metadata, a decorated `Uint8Array` or `Buffer` keeps registering `CLR_BYTES`, unchanged. On builds that don't, it registered `CLR_STRING` and now throws error 1, like an undecorated one. Runtime inference deliberately excludes it, so an undecorated one now throws instead of silently becoming `CLR_STRING`. — accepted by Ben, 2026-10-02
- **Python `float` → `CLR_FLOAT` (REAL)** has the same precision loss. Left out of this fix by the Q2 decision. — Ben, 2026-10-02
- **esbuild emitting no `design:type`** is carried from the 2026-09-16 investigation and was not re-probed (esbuild is not installed). The design doesn't depend on it: steps 2 and 4 work with or without metadata.
