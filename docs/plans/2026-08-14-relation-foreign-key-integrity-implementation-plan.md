# Relation Foreign-Key Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-14-relation-foreign-key-integrity-design.md` (commit SHA: `43e4b9ba431c27e433e7d587695b644d2f617c3a`)

**Goal:** Stop a `many_to_many` foreign key from being destroyed by depth-1 hydration in the Python, TypeScript and Go clients, and stop the Java client from dropping every array-typed field on read.

**Architecture:** Two independent client-side defects. Part A extends an existing navigation-property-name strip rule from `"Id"` to `"Ids"` in three clients, so a relation's `property_name` no longer collides with its own `foreign_key`. Part B adds a `LIST_VALUE` case to the Java client's struct→POJO conversion, plus the navigation-property skip its write path already has. No server, orchestrator, driver or harness-assertion code changes.

**Tech stack:** Python (pytest), TypeScript (vitest + tsc), Go 1.25 (`go test`), Java (Maven, module `client`). gRPC + protobuf `Struct` payloads throughout.

---

## Global Constraints

- **Both parts must be mutation-tested.** After the tests pass, revert the production edit and confirm the new tests go red, then restore. A green suite is exactly what hid the Java defect — `StructConverterTest` has always passed while every array field came back null.
- **Do not change any foreign-key derivation.** `_infer_fk` / `inferFk` / `inferFK` are correct in all three clients and are out of bounds for Task 1.
- **Do not edit a conflicting pre-existing test.** If a change appears to require modifying an existing test's assertions, STOP and report rather than editing it.

## File Structure

**Modify**
- `Iverson.Clients/Python/iverson_client/core.py` — `_relation_property_name`, add the `many_to_many` branch
- `Iverson.Clients/TypeScript/src/core.ts` — `relationPropertyName`, same branch
- `Iverson.Clients/Go/iverson/registrar.go` — `relationPropertyName`, same branch
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java` — `fromStruct`, both `fromValue` overloads

**Test**
- `Iverson.Clients/Python/tests/test_schema_registrar.py`
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`
- `Iverson.Clients/Go/iverson/coordinator_test.go` — Go's registrar tests live here; there is no `registrar_test.go`
- `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java`

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and re-confirmed across two critical-design-review rounds. Trusted as ground truth, NOT re-verified here:

- The three `relationPropertyName` functions are the only `property_name` derivation sites (`core.py:100,280`; `core.ts:381`; `registrar.go:129,328`).
- The FK column is emitted as its own `PropertyDescriptor` and survives the rename (`registrar.go:138`; `core.py:265-273`).
- Write and read payload keys derive from the foreign key, never `property_name` (`core.py:409,696`; `core.ts:468,488`; `coordinator.go:547,640`).
- `many_to_many` is the only colliding relation kind; `one_to_many` FKs live on the related row.
- `RelationValidator.cs:20-24` gates the nav-property write rejection on `PropertyName != ForeignKey`; opening that gate is a strict gain, as no client emits the nav key.
- `fromStruct` is the only struct→POJO path (`EntityCoordinator.java:140,156,193,211,225,239,291`); `fromStructAsMap` serves `:255,279`.
- Java entity array fields are `List<T>`, never Java arrays.
- `toStruct` emits `LIST_VALUE` for `Collection` (`StructConverter.java:110-115`), making the gap one-directional.
- **B7 (failed assumption, carried forward):** `fromStruct` does NOT read only what `toStruct` writes — it has no navigation-property filter, so it receives hydrated nav properties at depth >= 1. This is why Task 2 adds the skip.
- .NET shares none of the changed derivation code (`SchemaRegistrar.cs:85,268`).

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | The three non-Go test files exist as named | `ls` returned all three: `test_schema_registrar.py`, `schema-registrar.test.ts`, `StructConverterTest.java` |
| 2 | File path | **Corrected during verification.** The draft named `Iverson.Clients/Go/iverson/registrar_test.go`; it does not exist. Go's package has only `auth_test.go` and `coordinator_test.go`, and the registrar tests are in the latter | `ls Iverson.Clients/Go/iverson/*_test.go`; `grep -l` for `inferFK`/`SchemaRegistrar` matched `coordinator_test.go` only |
| 3 | Signature | Python's relation dict discriminator is the string `"many_to_many"` | `core.py:274-282` builds `{"field", "kind", "related_type"}`; `annotations.py:203-205` sets `relation_kind="many_to_many"` |
| 4 | Signature | `RelationKindString` includes `'many_to_many'` | `annotations.ts:43` — `'many_to_one' \| 'many_to_many' \| 'one_to_many' \| 'one_to_one'` |
| 5 | Signature | Go's `KindManyToMany` constant exists and is used the same way as `KindManyToOne` | `registrar.go:142,301,313` |
| 6 | Signature | `isNavigationProperty(Field)` is `private static` in `StructConverter`, so `fromStruct` can call it | `StructConverter.java:129` |
| 7 | Signature | Both `fromValue` overloads are `private static` with exactly two in-file callers, so changing the typed overload's parameters breaks no external consumer | `StructConverter.java:153,162`; callers at `:73,90` only |
| 8 | Code validity | `ParameterizedType`, `Type`, `ListValue` and `Collection` are already imported — the new code adds no import | `StructConverter.java:3,11-13,16` |
| 9 | Command | Python: `pytest` from `Iverson.Clients/Python` | `pyproject.toml` — `[tool.pytest.ini_options] testpaths = ["tests"]` |
| 10 | Command | TypeScript: `npm test` from `Iverson.Clients/TypeScript`, which runs `tsc -p tsconfig.test.json && vitest run` | `package.json:scripts.test` = `"npm run typecheck && vitest run"` |
| 11 | Command | Go: `go test ./...`, `go vet ./...`, `gofmt -l .` from `Iverson.Clients/Go` | `go.mod` — `module github.com/iverson/clients/go`, `go 1.25.0`. `gofmt -l` is included because `go vet` does not check formatting and a prior task on this branch introduced gofmt drift that vet passed |
| 12 | Command | Java: `mvn -pl client test` from `Iverson.Clients/Java` | `pom.xml` `<modules>` lists `client`, `sample`, `conformance`; `client/pom.xml` artifactId `iverson-client` |
| 13 | Ordering | Task 1 and Task 2 are independent — disjoint files, disjoint languages, no shared symbol | Task 1 touches Python/TS/Go registrar derivation; Task 2 touches only `StructConverter.java` |
| 14 | Code validity | The Python derivation yields `PyTags` from `py_tag_ids` | Ran the derivation: `_to_pascal_case('py_tag_ids')` → `PyTagIds`; `[:-3] + 's'` → `PyTags` |
| 15 | Code validity | The TypeScript derivation yields `TsTags` from `tsTagIds` | `toPascalCase` is `charAt(0).toUpperCase() + slice(1)` (`core.ts:91-94`) → `TsTagIds`; `slice(0,-3) + 's'` → `TsTags` |
| 16 | Code validity | Java `List<UUID>` elements arrive as `STRING_VALUE`, so the existing typed `fromValue` converts them | `toStruct` writes UUIDs via `setStringValue` (`:105`); `StructConverterTest.java:118` reads list elements with `getStringValue` |
| 17 | Consumer impact | No existing test asserts a `many_to_many` `property_name`; all three assert the FK **property** instead, so none breaks | `test_schema_registrar.py:320` asserts `"RegTagIds" in props`; `schema-registrar.test.ts:469-471` asserts `mtm['RegAuthorIds']`; `coordinator_test.go:212-214` asserts `ArticleIds` |
| 18 | Consumer impact | Go's `coordinator_test.go` fixture `Tag.Articles` is unaffected by the new branch — its member name has no `Ids` suffix | `coordinator_test.go:69` — `Articles []string \`iverson:"many_to_many:Article"\`` |
| 19 | Consumer impact | Nothing can depend on Java nav properties being populated by `fromStruct`, because they resolve to `null` today | `fromValue(Value, Class)` `default -> null` (`:179`) covers `LIST_VALUE` and `STRUCT_VALUE` |

## Tasks

### Task 1: `many_to_many` navigation-property derivation (Python, TypeScript, Go)

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py:100-111`
- Modify: `Iverson.Clients/TypeScript/src/core.ts:102-110`
- Modify: `Iverson.Clients/Go/iverson/registrar.go:328-336`
- Test: `Iverson.Clients/Python/tests/test_schema_registrar.py`
- Test: `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`
- Test: `Iverson.Clients/Go/iverson/coordinator_test.go`

- [ ] **Step 1: Add the `many_to_many` branch to Python's `_relation_property_name`**

Insert before the final `return pascal` at `core.py:111`:

```python
    if relation["kind"] == "many_to_many":
        if len(pascal) > 3 and pascal.endswith("Ids"):
            return pascal[:-3] + "s"
```

Update the docstring's last sentence so it names the `many_to_many` rule alongside the existing `many_to_one` one.

- [ ] **Step 2: Add the same branch to TypeScript's `relationPropertyName`**

Insert before the final `return pascal;` at `core.ts:109`:

```typescript
    if (kind === 'many_to_many') {
        if (pascal.length > 3 && pascal.endsWith('Ids')) {
            return pascal.slice(0, -3) + 's';
        }
    }
```

Update the JSDoc comment above the function the same way.

- [ ] **Step 3: Add the same branch to Go's `relationPropertyName`**

Insert before the final `return fm.Name` at `registrar.go:335`:

```go
	if fm.RelationKind == KindManyToMany {
		name := fm.Name
		if len(name) > 3 && name[len(name)-3:] == "Ids" {
			return name[:len(name)-3] + "s"
		}
	}
```

Update the function's doc comment, which currently reads "For others: use the field name as-is."

- [ ] **Step 4: Add one test per client**

Each asserts, for a `many_to_many` member named `*_ids` / `*Ids`, that the emitted relation has `property_name != foreign_key`, that `property_name` is the plural form (e.g. `RegTags`), and that the FK column is still present in the type's properties. Follow the existing `many_to_one` equivalents at `test_schema_registrar.py:341-358` and `schema-registrar.test.ts:321-322`; for Go, follow the `propsByName` / `assertFkProperty` helpers already in `coordinator_test.go`.

Do NOT modify the existing `many_to_many` tests — they assert FK properties and must keep passing unchanged.

- [ ] **Step 5: Run each suite**

```bash
cd Iverson.Clients/Python     && pytest
cd Iverson.Clients/TypeScript && npm test
cd Iverson.Clients/Go         && go test ./... && go vet ./... && gofmt -l .
```

`gofmt -l` must print nothing. Report the pass counts; Python was 188 and TypeScript 184 before this task.

- [ ] **Step 6: Mutation-test**

Revert each of the three branches in turn, confirm that client's new test goes red, restore it. Report which test failed for each client.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/Python/iverson_client/core.py \
        Iverson.Clients/TypeScript/src/core.ts \
        Iverson.Clients/Go/iverson/registrar.go \
        Iverson.Clients/Python/tests/test_schema_registrar.py \
        Iverson.Clients/TypeScript/tests/schema-registrar.test.ts \
        Iverson.Clients/Go/iverson/coordinator_test.go
git commit -m "Python/TS/Go: derive a distinct nav property name for many_to_many relations"
```

### Task 2: Java `LIST_VALUE` deserialization and navigation-property skip

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java:58-79, 152-180`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java`

- [ ] **Step 1: Skip navigation properties and pass the generic type in `fromStruct`**

At `:63-66`, filter the field map, mirroring `toStruct:39`; at `:73`, pass the field's generic type through:

```java
            for (Field f : getAllFields(type)) {
                if (isNavigationProperty(f)) continue;
                fieldMap.put(toPascalCase(f.getName()).toLowerCase(), f);
            }

            for (Map.Entry<String, Value> entry : fields.entrySet()) {
                Field f = fieldMap.get(entry.getKey().toLowerCase());
                if (f == null) continue;
                f.setAccessible(true);
                f.set(instance, fromValue(entry.getValue(), f.getType(), f.getGenericType()));
            }
```

- [ ] **Step 2: Add the `LIST_VALUE` case to the typed `fromValue`**

Widen the typed overload to take the generic type and handle lists. Keep the existing two-argument overload delegating with a `null` generic type so nothing else has to change:

```java
    private static Object fromValue(Value value, Class<?> targetType) {
        return fromValue(value, targetType, null);
    }

    private static Object fromValue(Value value, Class<?> targetType, Type genericType) {
        if (value.getKindCase() == Value.KindCase.LIST_VALUE) {
            if (!Collection.class.isAssignableFrom(targetType)) return null;
            Class<?> elementType = elementTypeOf(genericType);
            if (elementType == null) return null;
            List<Object> items = new ArrayList<>();
            for (Value element : value.getListValue().getValuesList()) {
                items.add(fromValue(element, elementType, null));
            }
            return items;
        }
        // ...existing STRING_VALUE / NUMBER_VALUE / BOOL_VALUE / default arms unchanged
    }

    /** The single type argument of a parameterized collection, or null when not resolvable. */
    private static Class<?> elementTypeOf(Type genericType) {
        if (genericType instanceof ParameterizedType pt) {
            Type[] args = pt.getActualTypeArguments();
            if (args.length == 1 && args[0] instanceof Class<?> element) return element;
        }
        return null;
    }
```

Add `java.util.ArrayList` and `java.util.List` to the imports; `ParameterizedType`, `Type` and `Collection` are already imported.

Returning `null` for a raw or unresolvable collection preserves today's behaviour for those fields rather than introducing a new failure mode.

- [ ] **Step 3: Add the `LIST_VALUE` case to the untyped `fromValue`**

So `fromStructAsMap` stops dropping array columns:

```java
            case LIST_VALUE -> {
                List<Object> items = new ArrayList<>();
                for (Value element : value.getListValue().getValuesList()) {
                    items.add(fromValue(element));
                }
                yield items;
            }
```

- [ ] **Step 4: Add three tests**

1. A `Struct` carrying a `TagIds` list of UUID strings round-trips into a populated `List<UUID>` field.
2. `fromStructAsMap` returns a `List` for an array column rather than `null`.
3. A `Struct` carrying a hydrated `Tags` list of tag structs leaves the annotated navigation field null — not a list of nulls.

Test 3 is the regression guard for the skip in Step 1. Use the existing `StructTestArticle` / `StructTestTag` fixtures at `StructConverterTest.java:53-56`; `StructTestTag` must carry `@IversonEntity` for `isNavigationProperty` to fire, as `JavaTag` does.

- [ ] **Step 5: Run the suite**

```bash
cd Iverson.Clients/Java && mvn -pl client test
```

- [ ] **Step 6: Mutation-test**

Three reverts, each independently: drop the `LIST_VALUE` case from the typed `fromValue` (test 1 must go red), from the untyped one (test 2 red), and remove the `isNavigationProperty` skip (test 3 red). Restore after each. Report which test failed for each revert.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java
git commit -m "Java: deserialize LIST_VALUE into collection fields and skip nav properties on read"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope" section. A new spec → plan cycle is required to add any of these.

Java's array-read gap means the `array-column-mapping` initiative's Java arm was never working on the read path, and its suite went green regardless. Part B fixes the mechanism. Whether that initiative's Java coverage needs a wider re-audit is a separate decision.

A Java or .NET user who annotates the foreign-key member rather than a separate navigation member still produces a colliding descriptor, because neither client derives a navigation name. Neither client documents that shape.
