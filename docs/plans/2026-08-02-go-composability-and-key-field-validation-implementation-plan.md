# Go Declaration Composability and Key-Field Validation — Implementation Plan

**Source spec:** `docs/specs/2026-08-02-go-composability-and-key-field-validation-design.md` (last-modifying commit `59f4e12`; repo HEAD at plan-write time `59f4e12` — no drift)

Four tasks. Task 2 and Task 3 both depend on Task 1; Task 4 is independent of all three.

---

## Inherited from spec (do NOT re-verify)

The spec's `Verified assumptions` section (28 rows, reconfirmed by CDR rounds 1 and 2) is ground
truth for this plan. In particular:

- Go's `FieldMeta` has no name collision with the five new booleans (G1).
- Narrowing `ParseTag` to relations is safe at `coordinator.go` (G3).
- The five boolean-rewrite sites and four `RelationKind`-rename sites are the complete change
  surface (G5, G6, G7).
- The doc/comment reference set for the rewrite is `tags.go:1-43`, `:29-30`, `:68-69`, `:83-84`,
  `:134`, and `:247` (G10).
- Un-migrated tags fail loudly rather than degrading silently (G11).
- The server accepts-and-discards `{search_key, large_field, embedding, chunk, metadata}` on the
  key field, rejects the enrichment declarations loudly, and does not discard `tenant` (V5–V8).
- No existing declaration in any client breaks (V9).

**One inherited citation is corrected here.** The spec's `G12` cites `tags.go:249` for
`InspectType`'s declaration. The declaration is at **`tags.go:213`**; line 249 is blank. CDR round 2
§1 flagged this and deferred the fix to the spec's next edit. The fact `G12` attests — that
`InspectType` returns `(EntityMeta, error)` and can therefore raise — holds; only the pointer is
wrong. This plan uses `tags.go:213`. **The spec itself is still uncorrected.**

## Known issues inherited from spec

- Removing the `kind` axis makes server-rejected combinations newly expressible in Go, exactly as it
  did in Python. No client-side validation is added for them beyond §2's key-field check; the server
  rejects them with `InvalidArgument`, which is loud.
- The Go migration is hard-breaking with no compatibility shim. Every existing scalar struct tag
  changes spelling.
- `tenant` on the key field stays legal and unchecked. It is not discarded, so it is out of scope.

---

## Task 1 — Go: independent tag key per scalar declaration

**Modify:**
- `Iverson.Clients/Go/iverson/tags.go`
- `Iverson.Clients/Go/iverson/registrar.go`
- `Iverson.Clients/Go/iverson/coordinator.go`
- `Iverson.Clients/Go/iverson/coordinator_test.go`
- `Iverson.Clients/Go/iverson_test/tags_test.go`
- `Iverson.Clients/Go/iverson_test/registrar_test.go`
- `Iverson.Clients/Go/sample/main.go`
- `Iverson.Clients/Go/sample/models/article.go`
- `Iverson.Clients/Go/sample/models/author.go`
- `Iverson.Clients/Go/sample/models/tag.go`

**Atomic by necessity.** The package does not compile between the `FieldMeta` change and the
call-site conversions, and the suite stays red until every tag migrates. Splitting this into
"change the struct" / "convert the readers" / "migrate the tags" would hand two subagents a
non-compiling tree.

### Step 1 — Add the five tag-key constants

In `tags.go`, beside the eight existing `*TagKey` constants (`TagKey` `:54`, `DescriptionTagKey`
`:58`, `MetadataTagKey` `:62`, `TenantTagKey` `:68`, `SummaryTagKey` `:72`, `KeywordsTagKey` `:76`,
`ExtractTagKey` `:80`, `ContextualTagKey` `:85`), add:

```go
// KeyTagKey marks the primary key field: `iverson_key:"true"`.
const KeyTagKey = "iverson_key"

// SearchKeyTagKey declares a sort key at a 0-based position: `iverson_search_key:"0"`.
const SearchKeyTagKey = "iverson_search_key"

// LargeFieldTagKey excludes the column from the StarRocks materialized view:
// `iverson_large_field:"true"`.
const LargeFieldTagKey = "iverson_large_field"

// EmbeddingTagKey marks the field as an embedding source: `iverson_embedding:"true"`.
const EmbeddingTagKey = "iverson_embedding"

// ChunkTagKey marks the field for chunking. Value is "true" for defaults, "256" for a
// window size, or "256:32" for window size and overlap.
const ChunkTagKey = "iverson_chunk"
```

Follow the existing one-const-per-`const`-statement style at `:54-85`, not a grouped block.

### Step 2 — Restructure `FieldMeta`

In `tags.go`, replace the `Kind string` field (`:102-103`) with `RelationKind string`, and add the
five booleans. Preserve every other field unchanged.

```go
	// RelationKind is one of KindManyToOne, KindManyToMany, KindOneToMany or
	// KindOneToOne, or "" for scalar fields. Relations are mutually exclusive by
	// design: they serialize to RelationDescriptor, a different proto message.
	RelationKind string
	// IsKey reports whether the field carries `iverson_key:"true"`.
	IsKey bool
	// IsSearchKey reports whether the field carries `iverson_search_key:"N"`.
	IsSearchKey bool
	// IsLargeField reports whether the field carries `iverson_large_field:"true"`.
	IsLargeField bool
	// IsEmbedding reports whether the field carries `iverson_embedding:"true"`.
	IsEmbedding bool
	// IsChunk reports whether the field carries `iverson_chunk:"..."`.
	IsChunk bool
```

Every one of the five is independent of every other — do not document any of them as exclusive.
Update the three doc comments that reference the old axis while you are in this struct:
`:106` ("when Kind == KindSearchKey" → "when IsSearchKey"), `:108` and `:110` (same shape for
`IsChunk`), and `:130` ("Only valid when Kind == KindChunk" → "Only valid when IsChunk").

### Step 3 — Delete the five scalar `Kind*` constants

In `tags.go:89-93`, delete `KindKey`, `KindSearchKey`, `KindLargeField`, `KindEmbedding` and
`KindChunk`. Keep `KindManyToOne`, `KindManyToMany`, `KindOneToMany` and `KindOneToOne` (`:94-97`).
Retitle the block's comment from "Kind constants for tag values" to reflect that it now holds
relation kinds only.

### Step 4 — Narrow `ParseTag` to relations

`ParseTag(fieldName, tagValue string) (FieldMeta, error)` (`tags.go:136`) **keeps its exported
signature.** Delete the five scalar cases (`:147-183`) — `key`, `search_key`, `large_field`,
`embedding`, `chunk` — including the `search_key` order parsing and the `chunk` `maxTokens[:overlap]`
sub-grammar parsing, which move to Step 5. Keep the relation case (`:186-187`), assigning
`meta.RelationKind = kind` instead of `meta.Kind = kind`. Keep the empty-tag early return and the
`default:` unknown-kind error at `:194-195` **unchanged** — that error is what makes un-migrated
tags fail loudly (G11).

### Step 5 — Read the five new keys at the assembly point

In `InspectType` (`tags.go:213`), inside the field loop, after the existing independent-key reads at
`:232-236` and before the `ExtractTagKey` block at `:238`:

```go
		fm.IsKey = sf.Tag.Get(KeyTagKey) == "true"
		fm.IsLargeField = sf.Tag.Get(LargeFieldTagKey) == "true"
		fm.IsEmbedding = sf.Tag.Get(EmbeddingTagKey) == "true"

		if order, ok := sf.Tag.Lookup(SearchKeyTagKey); ok {
			n, err := strconv.Atoi(strings.TrimSpace(order))
			if err != nil {
				return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a non-integer search-key order %q; the value is the 0-based sort position", SearchKeyTagKey, sf.Name, order)
			}
			fm.IsSearchKey = true
			fm.SearchKeyOrder = n
		}

		if chunk, ok := sf.Tag.Lookup(ChunkTagKey); ok {
			fm.IsChunk = true
			fm.ChunkMaxTokens = 512
			fm.ChunkOverlap = 64
			if chunk != "true" {
				parts := strings.SplitN(chunk, ":", 2)
				maxTokens, err := strconv.Atoi(strings.TrimSpace(parts[0]))
				if err != nil {
					return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a non-integer chunk window size %q; use \"true\", \"256\" or \"256:32\"", ChunkTagKey, sf.Name, parts[0])
				}
				fm.ChunkMaxTokens = maxTokens
				if len(parts) == 2 {
					overlap, err := strconv.Atoi(strings.TrimSpace(parts[1]))
					if err != nil {
						return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a non-integer chunk overlap %q; use \"true\", \"256\" or \"256:32\"", ChunkTagKey, sf.Name, parts[1])
					}
					fm.ChunkOverlap = overlap
				}
			}
		}
```

`strconv`, `strings` and `fmt` are all already imported (`tags.go:1-6` import block). Use `Lookup`
rather than `Get` for `iverson_search_key` and `iverson_chunk` so that `"0"` and an absent tag are
distinguishable — `Get` returns `""` for both, and `search_key` order 0 is the common case.

The 512/64 defaults are transcribed from the current `chunk` parsing at `tags.go:167-168`, which
sets them before consulting the tag's value. Preserve them exactly — a chunk field declared without
windowing must keep producing the same descriptor it produces today.

### Step 6 — Convert the boolean-read sites

The spec counts five *sites*; `registrar.go:85-91` is one of them and contributes six lines, so the
table below has nine rows across those five sites.

| Site | Current | Becomes |
|---|---|---|
| `registrar.go:85` | `IsKey: fm.Kind == KindKey` | `IsKey: fm.IsKey` |
| `registrar.go:86` | `IsNullable: fm.Kind != KindKey` | `IsNullable: !fm.IsKey` |
| `registrar.go:87` | `IsSearchKey: fm.Kind == KindSearchKey` | `IsSearchKey: fm.IsSearchKey` |
| `registrar.go:89` | `IsLargeField: fm.Kind == KindLargeField` | `IsLargeField: fm.IsLargeField` |
| `registrar.go:90` | `IsEmbedding: fm.Kind == KindEmbedding` | `IsEmbedding: fm.IsEmbedding` |
| `registrar.go:91` | `IsChunk: fm.Kind == KindChunk` | `IsChunk: fm.IsChunk` |
| `coordinator.go:125` | `if f.Kind == KindKey` | `if f.IsKey` |
| `coordinator.go:151` | `if f.Kind == KindKey` | `if f.IsKey` |
| `tags.go:246` | `if fm.ChunkContextual && fm.Kind != KindChunk` | `if fm.ChunkContextual && !fm.IsChunk` |

`registrar.go:88` (`IsMetadata`) already reads an independent flag and does not change.

`tags.go:246`'s guard must run **after** Step 5's `iverson_chunk` parse sets `fm.IsChunk`. It
currently sits at `:245-248`, after the `ExtractTagKey` block; Step 5 inserts before that block, so
the ordering is already correct — verify it rather than assuming.

### Step 7 — Convert the four `RelationKind` rename sites

| Site | Current | Becomes |
|---|---|---|
| `registrar.go:106` | `relationKindToProto(fm.Kind)` | `relationKindToProto(fm.RelationKind)` |
| `registrar.go:227` | `switch fm.Kind` | `switch fm.RelationKind` |
| `registrar.go:244` | `fm.Kind == KindManyToOne \|\| fm.Kind == KindOneToOne` | `fm.RelationKind == ...` |
| `tags.go:250` | `switch fm.Kind { case KindManyToOne, ...: ... default: ... }` | `if fm.RelationKind != "" { meta.Relations = append(...) } else { <default branch verbatim> }` |

**`tags.go:250` is relation-vs-scalar routing plus tenant collection, not validation.** Its
`default:` branch — the `fm.Tenant` collection and the `meta.Fields` append at `:256-261` — moves
into the `else` **verbatim**, and the comment at `:252-255` explaining why relations must not reach
`meta.Fields` moves with the relation branch. Rewriting this site against the scalar booleans would
send every plain, untagged field into `meta.Relations`, dropping its column from the registered
schema and breaking the tenant check.

### Step 8 — Redefine plain-field detection in the sample

`sample/main.go:24` currently tests `if f.Kind == ""`. Under independent flags, "plain" means no
scalar flag and no relation kind:

```go
		if !f.IsKey && !f.IsSearchKey && !f.IsLargeField && !f.IsEmbedding && !f.IsChunk &&
			!f.Metadata && f.RelationKind == "" {
```

`sample/main.go:27` prints `f.Kind` and `:32` prints `r.Kind`. `:32` iterates relations, so it
becomes `r.RelationKind`. `:27` prints a scalar field's kind, which no longer exists as a single
value — replace it with the field name alone, or print the set flags; pick whichever keeps the
sample's output readable and say which in the commit message.

### Step 9 — Migrate the sample models

Rewrite every scalar `iverson:"..."` tag in `sample/models/article.go`, `sample/models/author.go`
and `sample/models/tag.go` to the new keys. Known instances: `article.go:7`, `tag.go:5` and
`author.go:5` each carry `iverson:"key"` → `iverson_key:"true"`. Sweep each file for
`iverson:"search_key`, `iverson:"large_field"`, `iverson:"embedding"` and `iverson:"chunk` as well —
the enumeration above covers only the key tags. Relation tags (`iverson:"many_to_one:..."`) are
unchanged.

Take the opportunity the spec's example offers: give one sample field a composed declaration
(`iverson_large_field:"true" iverson_chunk:"256:32"`) so the sample demonstrates the capability the
task adds. Only if a field there is already both conceptually — do not invent a field.

### Step 10 — Migrate the tests

Two packages, both must migrate:

- `iverson/coordinator_test.go` — internal package `iverson`. Known instance: `:27`
  `iverson:"key"`. Sweep for the other four scalar kinds.
- `iverson_test/` — external package `iverson_test`, which sees only the exported API. This is the
  larger surface:
  - `registrar_test.go` — 8 struct tags, including `:289` `iverson:"key" iverson_desc:"Primary
    identifier"` (the key+description combination that must keep working) and key tags at `:33`,
    `:246`, `:299`, `:385`, `:433`, `:447`, `:503`, `:520`.
  - `tags_test.go` — the largest surface. Roughly a dozen direct `ParseTag` calls passing scalar
    kind strings, plus assertions on `fm.Kind` at `:18`, `:31`, `:41`, `:62`, `:72`, `:82`, `:111`,
    `:135`, `:203`, `:223`, `:251`, `:274`, `:328`, `:353`, `:359`, `:368`, and struct tags at
    `:165`, `:303`, `:380`.

Migration rules for `tags_test.go`:

- A test that called `ParseTag(name, "key")` and asserted `fm.Kind == iverson.KindKey` no longer has
  a `ParseTag` path — the scalar keys are read at the assembly point, not by `ParseTag`. Convert
  those to `InspectType` tests over a struct carrying `iverson_key:"true"`, asserting `fm.IsKey`.
  Do **not** delete them; the coverage they provide is what proves Step 5 works.
- Relation `ParseTag` tests (`:111`, `:124-136`, `:274`) stay as `ParseTag` calls, asserting
  `fm.RelationKind`.
- `:18-19` asserts an empty kind for an untagged field. Convert to asserting `fm.RelationKind == ""`
  **and** that none of the five booleans is set — both halves, so the test cannot pass by a
  zero-valued struct that Step 5 never populated.
- `:359-360` asserts "metadata tag must not set a kind". Convert to asserting `iverson_meta` sets
  `fm.Metadata` and leaves all five scalar booleans and `RelationKind` unset.

Every converted assertion must name the specific flag it checks. An assertion rewritten as
"something is set" is weaker than what it replaced.

### Step 11 — Rewrite the documentation

All six references identified in the spec's G10:

- `tags.go:1-43` — the package doc's tag-format table. Rewrite the five scalar lines to the new
  keys, and rewrite the paragraph at `:16-17` ("A field may also carry these independent tags, each
  valid alongside any `iverson` kind (including `key`)") — there is no longer an `iverson` kind for
  scalars to be alongside. The new framing: `iverson` carries relation kinds only; every scalar
  declaration is its own independent key, and all of them compose.
- `tags.go:29-30` and `:83-84` — describe `iverson_contextual` as valid "only alongside
  `iverson:\"chunk...\"`". Becomes `iverson_chunk`.
- `tags.go:68-69` — asserts "kinds are mutually exclusive". Delete the assertion; it is now false
  for scalars and true only for relations. If the surrounding sentence is about `iverson_meta`
  composing, keep that and drop only the exclusivity claim.
- `tags.go:134` — `ParseTag`'s doc comment. Rewrite to say it parses relation kinds from the
  `iverson` tag.
- `tags.go:247` — the runtime error string `…is not a chunk field (iverson:\"chunk...\")…`. Becomes
  `iverson_chunk:\"...\"`. This one is escaped in the source, so `grep 'iverson:"'` will not find
  it — grep `'iverson:\\"'` to confirm you have caught every escaped instance.

### Step 12 — Build and test

```bash
cd Iverson.Clients/Go && gofmt -l . && go vet ./... && go build ./... && go test ./...
```

`gofmt -l` printing any filename is a failure. Expect the suite green with no change in test count
(Task 1 migrates tests, it does not add or remove them). If a test count changes, say why in the
commit message.

### Step 13 — Commit

```bash
cd /home/ben/repositories/Iverson
git add Iverson.Clients/Go
git commit -m "refactor(go-client): replace the kind axis with independent scalar tag keys"
```

`refactor(go-client)` matches the established scope — `git log` shows 8 prior `(go-client)` commits.

---

## Task 2 — Go: composition test

**Depends on Task 1.** Additive; touches only the test package.

**Modify:** `Iverson.Clients/Go/iverson_test/registrar_test.go`

### Step 1 — Add the fixture

A struct whose key carries only a key tag, with a tenant field (required — `InspectType` returns an
error for a type with no `iverson_tenant` field, so a fixture without one fails the tenant check
before reaching anything this test asserts), and one field carrying both `iverson_large_field` and
`iverson_chunk` with non-default windowing:

```go
type ComposedDeclArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Body     string `iverson_large_field:"true" iverson_chunk:"256:32"`
}
```

`ComposedDeclArticle` is a free name — grep confirms no collision in the Go test packages. (Avoid
`RegComposedDeclarationArticle`, which is taken in the *Python* test suite at
`tests/test_schema_registrar.py:110`; different language, but reusing it invites confusion.)

### Step 2 — Assert both halves

Build the schema request through the same path `registrar_test.go`'s existing tests use, locate the
`Body` property descriptor, and assert:

- `IsLargeField` is true
- `IsChunk` is true
- `ChunkMaxTokens == 256`
- `ChunkOverlap == 32`

All four in one test. **Both flag assertions are the point** — a test asserting only `IsChunk` would
pass while `large_field` was silently dropped, which is the exact failure the whole task exists to
prevent. The windowing assertions use non-default values so they cannot pass vacuously against the
512/64 defaults.

### Step 3 — Test and commit

```bash
cd Iverson.Clients/Go && go test ./...
cd /home/ben/repositories/Iverson
git add Iverson.Clients/Go/iverson_test/registrar_test.go
git commit -m "test(go-client): assert large_field and chunk compose on one field"
```

---

## Task 3 — Key-field validation in all five clients

**Depends on Task 1** — the Go half reads `fm.IsKey`, which Task 1 introduces. The other four halves
are independent of Task 1, but the task is atomic, so the whole task follows Task 1.

**Deliberately one task, not five.** Five per-client tasks would be genuinely independent work in
five languages with no shared code — normally real decomposition. But the spec's requirement is that
siting the check identically in all five "keeps one rule with one wording rather than five dialects
of it." Five fresh subagents would produce five dialects. The cost is one large multi-toolchain
commit; the benefit is the property the spec actually asked for.

**Modify:**
- `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs`
- `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java`
- `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`
- `Iverson.Clients/TypeScript/src/core.ts`
- `Iverson.Clients/TypeScript/tests/core.test.ts`
- `Iverson.Clients/Python/iverson_client/core.py`
- `Iverson.Clients/Python/tests/test_schema_registrar.py`
- `Iverson.Clients/Go/iverson/tags.go`
- `Iverson.Clients/Go/iverson_test/tags_test.go`

### Step 1 — Fix the rule and its wording once, before writing any code

The rejected set is exactly `{search_key, large_field, embedding, chunk, metadata}`. `description`
stays legal. `tenant`, `summary`, `keywords` and `extract_hint` are out of scope — the first is not
discarded, the last three already throw at `SchemaRegistrationOrchestrator.cs:142-150`.

One message template, adapted only for each language's naming of the declaration:

> `{TypeName}.{KeyFieldName} is the primary key and also declares {declaration}; the server builds
> every per-property declaration from non-key properties only, so this would be accepted and
> silently discarded. Remove it from the key field. (Only a description is valid on a key.)`

Write this template down before touching any client. Every client's message must say the same thing
in the same order: type, field, offending declaration, why it is dropped, what to do.

When more than one rejected declaration is present, name them all in one error rather than raising
on the first — a user who fixes one and re-runs to find another has been served badly by the check.

### Step 2 — .NET

`SchemaRegistrar.cs`. `descriptor.KeyProperty` is available; the key descriptor is built at `:63`
via `BuildKeyDescriptor(descriptor.KeyProperty)` and the non-key loop skips it at `:68`. Add the
check **before** `:63`, so an invalid key fails before any descriptor is built.

Inspect `descriptor.KeyProperty` for `IversonSearchKeyAttribute`, `IversonLargeFieldAttribute`,
`IversonEmbeddingAttribute`, `IversonChunkAttribute` and `IversonMetadataAttribute` via
`GetCustomAttribute<T>()`. Confirm each attribute's exact type name against
`Iverson.Clients/DotNet/Iverson.Client.Attributes` before writing them — do not assume the names
above are spelled correctly.

Throw `ArgumentException`, matching `ResolveTenantField`'s idiom at `:101` and `:107`. (The spec's
table says `InvalidOperationException`; the actual established idiom in this file is
`ArgumentException`. Follow the file, and note the divergence in the commit message.)

### Step 3 — Java

`SchemaRegistrar.java`. `keyField` is identified in the first pass at `:82-95` and null-checked at
`:97-100`; the key descriptor is built at `:103`. Add the check between the null check and `:103`.

Inspect `keyField.getAnnotation(...)` for the five annotation classes. Throw
`IllegalArgumentException`, matching `resolveTenantField`'s idiom at `:127`.

### Step 4 — TypeScript

`core.ts`, in `describeEntity` (`:180`). `keyField` is bound at `:186` and the five accessor results
at `:187-195` — `searchKeysByField`, `largeFields`, `embeddingFields`, `chunkFieldsByName`,
`metadataFields`. Add the check immediately after `:195`, before the property loop.

Note the accessors are declared in `annotations.ts:74-255` and imported at `core.ts:50`; the check
site is `core.ts`. `keyField` is `string | undefined` — if it is undefined there is no key to check
and the existing downstream handling applies; do not add a new no-key error here.

Throw `Error`, matching the tenant checks at `:256` and `:262`.

### Step 5 — Python

`core.py`. `key_field` is bound at `:133` alongside `search_keys_by_field` `:134`,
`large_fields_set` `:135`, `embedding_fields_set` `:136`, `chunk_fields_by_name` `:137` and
`metadata_fields_set` `:139`. Add the check after `:143`, before `tenant_field` is resolved at
`:144`.

Raise `ValueError`, matching `_resolve_tenant_field`'s idiom at `:206` and `:212`.

### Step 6 — Go

`tags.go`, inside `InspectType`'s field loop (`:213`), after Step 5 of Task 1 has populated the five
booleans and after the `ChunkContextual` guard at `:245-248`. When `fm.IsKey` is true, reject
`fm.IsSearchKey`, `fm.IsLargeField`, `fm.IsEmbedding`, `fm.IsChunk` or `fm.Metadata`.

Return an `error`, matching the tenant checks at `:264-270`. Note Go's tenant check runs *after* the
loop while this one runs *inside* it — that is correct, since this rule is per-field and the tenant
rule is per-type.

### Step 7 — Tests: both halves, in all five clients

For each client, two tests:

1. **Rejection** — an entity whose key carries `metadata` fails registration with the new error.
2. **Acceptance** — an entity whose key carries only `description` still registers successfully.

The second is not optional. Without it, a client that rejected every key declaration including
`description` would pass the suite while breaking a documented, currently-working case
(`registrar_test.go:289` in Go and `AnnotationTest.java:22-24` in Java both rely on key+description).

**Every fixture must carry a tenant field.** All five clients raise on a missing tenant marker, and
in Python that check runs at `core.py:144` — *before* where Step 5 sites the key check. A fixture
without a tenant field would raise the tenant error, and a bare "raises" assertion would pass
vacuously. Assert on the key-field message specifically, not merely that an exception was raised.

Confirmed as a non-issue: no existing fixture in any of the five clients declares a rejected flag on
its key field — every key fixture carries at most `description`. The new check turns no green test
red, so no fixture migration is needed.

### Step 8 — Run all five suites

```bash
cd /home/ben/repositories/Iverson/Iverson.Clients/Go && go test ./...
cd /home/ben/repositories/Iverson && dotnet test Iverson.Clients/DotNet/Iverson.Client.slnx
cd /home/ben/repositories/Iverson/Iverson.Clients/Java && mvn -q test
cd /home/ben/repositories/Iverson/Iverson.Clients/TypeScript && npx vitest run
cd /home/ben/repositories/Iverson/Iverson.Clients/Python && python3 -m pytest tests/ -q
```

`python` is not on PATH — `python3` only. TypeScript's `test` script is `vitest run`
(`package.json:15`). All five must be green before committing; report each suite's count.

### Step 9 — Commit

```bash
cd /home/ben/repositories/Iverson
git add Iverson.Clients
git commit -m "feat(clients): reject silently-discarded declarations on the key field"
```

Scope `clients` rather than a per-language scope — the commit spans all five deliberately.

---

## Task 4 — Python spec documentation corrections

**Independent of Tasks 1-3.** Two edits to one markdown file. No code, no tests.

**Modify:** `docs/specs/2026-08-01-python-declaration-composability-design.md`

### Step 1 — Correct the parity claim

At `:231-232`, the text reads:

> **This fixes Python only.** The other four clients already compose correctly; Go was fixed at
> `e4a77ff` and .NET has always used independent attributes.

`e4a77ff` moved only `metadata` off Go's axis; Go retained the mutually-exclusive scalar kinds.
Rewrite to record that Go carried the same axis and is fixed by
`docs/specs/2026-08-02-go-composability-and-key-field-validation-design.md`, with .NET, Java and
TypeScript verified composable.

### Step 2 — Amend the Known issues surface

That spec's Known issues names only `metadata` + embedding/chunk/array/large_field as newly
expressible and server-rejected. Add two classes:

- `search_key` + `large_field`/`chunk`/`embedding`, rejected at `SchemaBuilder.cs:117-121`. Note
  that `IsEmbedding` and `IsChunk` implicitly add the column to `largeFields` at `:63` and `:76`, so
  `search_key`+`chunk` trips the same rule without an explicit `large_field`.
- The key-field class — recorded as **no longer** accepted-and-discarded, because Task 3 adds the
  client check.

### Step 3 — Commit

`docs/` is gitignored in this repo. Force-add:

```bash
cd /home/ben/repositories/Iverson
git add -f docs/specs/2026-08-01-python-declaration-composability-design.md
git commit -m "docs: correct the Python spec's cross-client parity claim"
```

---

## Tasks NOT in this plan

- Correcting `G12`'s `tags.go:249` → `:213` citation in the 2026-08-02 spec. That is an edit to the
  spec this plan consumes, not to the plan's own scope; it belongs to a spec-editing pass.
- Client-side validation for the other newly-expressible-but-server-rejected combinations. The spec
  records the accepted regression; the server rejects them loudly.
- `tenant` on the key field. Not discarded, so out of scope by the spec's own rule.

## Verified plan-level assumptions

Verified against `main@59f4e12`.

| # | Assumption | Evidence |
|---|---|---|
| P1 | Go source files are at the cited paths | `ls Iverson.Clients/Go/iverson` — `tags.go`, `registrar.go`, `coordinator.go`, `coordinator_test.go` all present |
| P2 | **Go tests span two packages** | `Iverson.Clients/Go/iverson/coordinator_test.go` is internal; `Iverson.Clients/Go/iverson_test/` is `package iverson_test` (line 1) and holds `tags_test.go`, `registrar_test.go` and five others. The external package sees only the exported API, which is why `fm.Kind` appears ~20× in `tags_test.go` |
| P3 | The Go sample is at `Go/sample/main.go` with models in `Go/sample/models/` | `find Iverson.Clients/Go -name main.go`; `article.go:7`, `author.go:5`, `tag.go:5` each carry `iverson:"key"` |
| P4 | `ParseTag`'s signature is as the spec states | `tags.go:136` — `func ParseTag(fieldName, tagValue string) (FieldMeta, error)` |
| P5 | `InspectType` is at `tags.go:213`, **not `:249`** | `grep -n '^func InspectType'` → `213`. Line 249 is blank. Corrects the spec's `G12` citation |
| P6 | `strconv`, `strings` and `fmt` are already imported in `tags.go` | import block at `tags.go:1-6` — `fmt`, `reflect`, `strconv`, `strings` |
| P7 | The eight existing `*TagKey` constants establish the naming convention | `tags.go:54,58,62,68,72,76,80,85` — one const per statement, each with a doc comment |
| P8 | The five boolean-read sites are exactly as tabulated | `registrar.go:85,86,87,89,90,91`; `coordinator.go:125,151`; `tags.go:246`. `registrar.go:88` reads `fm.Metadata` and is unaffected |
| P9 | The four rename sites are exactly as tabulated | `registrar.go:106,227,244`; `tags.go:250`. `tags.go:250-262`'s `default:` branch carries the tenant collection and `meta.Fields` append |
| P10 | The nine `Kind*` constants have no referents outside the Go client | Repo-wide grep for `Kind[A-Z]` in `*.go`: hits are confined to `Iverson.Clients/Go/iverson/`, `sample/main.go` and `iverson_test/`. `generated/*.pb.go` hits are proto oneof `.Kind` fields, unrelated |
| P11 | Client registrar and test files are at the cited paths | `DotNet/Iverson.Client.Core/SchemaRegistrar.cs` + `Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`; `Java/client/src/{main,test}/java/io/iverson/client/core/SchemaRegistrar{,Test}.java`; `TypeScript/src/core.ts` + `tests/core.test.ts`; `Python/iverson_client/core.py` + `tests/test_schema_registrar.py` |
| P12 | **TypeScript's accessors are in `annotations.ts`, not `core.ts`** | `getKeyField` etc. declared at `annotations.ts:74-255`, imported at `core.ts:50`, used at `core.ts:186`. The spec's CDR round 2 said "core.ts exposes" — it consumes them |
| P13 | Each client's check site has the key field and all five flag sets in scope | .NET `SchemaRegistrar.cs:63` (`descriptor.KeyProperty`, reflection); Java `:82-95` (`keyField`) + `:103`; TS `core.ts:186-195`; Python `core.py:133-143`; Go `tags.go:213` loop |
| P14 | Each client's error idiom at that point | .NET `ArgumentException` (`SchemaRegistrar.cs:101,107`) — **not** the `InvalidOperationException` the spec's table names; Java `IllegalArgumentException` (`:98,127,139`); TS `Error` (`core.ts:256,262`); Python `ValueError` (`core.py:206,212`); Go returned `error` (`tags.go:264,268`) |
| P15 | **No existing key fixture in any client carries a rejected flag** | Grepped `@IversonKey`/`iverson:"key"`/`iversonKey`/`key=True` with context in all five clients. Every hit carries at most a description — e.g. Go `registrar_test.go:289` `iverson:"key" iverson_desc:"Primary identifier"`, Java `AnnotationTest.java:22-24`. Task 3 turns no green test red; no fixture migration needed |
| P16 | Every client raises on a missing tenant field, and Python's check precedes the key check | Go `tags.go:264-270` (after the loop); Python `core.py:144` → `_resolve_tenant_field` `:206` (before the property loop); .NET `:93`; Java `:105`; TS `core.ts:255-266`. Fixtures without a tenant field would raise the wrong error |
| P17 | Test commands | Go `go test ./...` (`go.mod` present); TS `vitest run` (`package.json:15`); Python `python3 -m pytest tests/ -q` (`python` absent from PATH); .NET `dotnet test Iverson.Clients/DotNet/Iverson.Client.slnx`; Java `mvn test` at `Iverson.Clients/Java/pom.xml` (modules `client`, `sample`) |
| P18 | Commit type/scope conventions | `git log --oneline -400`: `(go-client)` ×8, `(dotnet-client)` ×6, `(java-client)` ×5, `(python-client)` ×11 |
| P19 | Task 2's and Task 3's fixture names are free | Grep across all five test suites: `ComposedDeclArticle` unused. `RegComposedDeclarationArticle` **is** taken (`Python/tests/test_schema_registrar.py:110`) — avoided |
| P20 | Task ordering | Task 2 uses `iverson_large_field`/`iverson_chunk`, introduced by Task 1. Task 3's Go half reads `fm.IsKey`, introduced by Task 1. Task 4 touches only a markdown file no other task reads. So: T1 → {T2, T3}, T4 anytime |
| P21 | The Python spec's parity claim is where Task 4 says | `docs/specs/2026-08-01-python-declaration-composability-design.md:231-232` — "The other four clients already compose correctly; Go was fixed at `e4a77ff`…" |
| P22 | `docs/` is gitignored, so Task 4's commit needs `-f` | Established repo-wide; every prior spec and plan in this project was committed with `git add -f` |
