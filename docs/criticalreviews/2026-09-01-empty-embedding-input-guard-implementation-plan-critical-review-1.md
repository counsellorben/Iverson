# Critical Implementation Review: 2026-09-01-empty-embedding-input-guard-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-09-01-empty-embedding-input-guard-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `f956a25`); cited file:line references re-checked under §1. The commit is `a667a8f`, the plan's own addition — no source file changed.

## 0. Coverage enumeration

### Task 1 × surfaces

| # | Surface | Disposition |
|---|---|---|
| T1-prose | Steps 1-3 prose (guard placement rule, "assert no HTTP request" rationale) | ok — the placement rule ("very top of the method body") is correct and unambiguous; see §1/P-note on the line citation |
| T1-code-1 | Exception type block | ok — `namespace Iverson.Embeddings`, primary-constructor form, `Exception` under implicit usings. Byte-for-byte the shape of `FilterTranslationException.cs:8` |
| T1-code-2 | `EmbedAsync` guard block | ok — the block shows the guard as the first statement after `{`, which is what an implementer copies. `Telemetry.Source` / `ActivityKind.Client` unchanged from `:50` |
| T1-code-3 | `[Theory]` test block | ok — every identifier resolves: `FakeHttpMessageHandler` (`EmbeddingServiceTests.cs:14`), `SuccessResponse` (`:51`), `CreateService` (`:31`), `LastRequest` (`:16`). `Theory`/`InlineData` under `using Xunit;` (`:8`); `Should()` under `using FluentAssertions;` (`:4`). `SuccessResponse([1f, 0f, 0f])` — collection expression targets `float[]`. No `using Iverson.Embeddings;` needed: `Iverson.Embeddings.Tests` is a child namespace |
| T1-commands | `dotnet test …Iverson.Embeddings.Tests.csproj`; `git add`/`git commit` | ok — csproj exists at that path; the three added/modified files are exactly the ones staged; message is lowercase imperative per `git log --oneline -12` |

### Task 2 × surfaces

| # | Surface | Disposition |
|---|---|---|
| T2-prose-1 | Step 1's comment prose (filter-after-generator rationale, PrefixWithContext masking) | ok — matches `:667` and `:628`; this is executable rationale, and it is the reason a reviewer can catch a wrongly-placed filter |
| T2-prose-2 | Step 2's ordering prose | ok — restates the Global Constraint; `:203` and `:374` both confirmed as the catch-all lines |
| T2-prose-3 | Step 3's window arithmetic | ok — **hand-simulated, see R3.** Not taken on faith |
| T2-prose-4 | Step 4's "pins the mapping, not the guard" prose | ok — accurate characterization; the substitute cannot exercise the real guard |
| T2-code-1 | Consumer filter block | ok — replaces `:229` exactly; `cf.MaxTokens`/`cf.Overlap`/`.Text` all resolve; result type unchanged (see §1/P12) |
| T2-code-2 | Catch-clause block | ok — the reproduced catch-all body matches `:205-206` and `:376-377` verbatim, so an implementer retyping it introduces no drift |
| T2-code-3 | Consumer test block | ok — every identifier present in the existing file; `$$"""…{{body}}…"""` is valid (with two `$`, a lone `{` is literal and `{{ }}` interpolates); `.Returns(ci => …)` on a stubbed upsert is the established idiom at `:707`; `(string)idx` matches the boxed `chunkIndex.ToString()` at `:297` |
| T2-code-4 | gRPC test block | ok — `.Returns<float[]>(_ => throw …)` is the codebase's idiom **on `EmbedAsync` specifically** (`IntelligenceStoreConsumerTests.cs:513`); `MakeStream<SearchResponse>()`, `TestServerCallContext.Create()`, and the `(await act.Should().ThrowAsync<RpcException>()).Where(…)` form all match `:1137-1141` |
| T2-commands | `dotnet test` ×2; `git add`/`git commit` | ok — both csprojs exist; the four staged files are exactly those modified |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | Task 1 produces `EmptyEmbeddingInputException` → **call site 1:** Task 2 Step 2's `catch` clause | ok — `ObjectSearchGrpcService.cs:8` already carries `using Iverson.Embeddings;`, so the plan's conditional "add it if absent" is a no-op rather than a missing step |
| C2 | Task 1 produces `EmptyEmbeddingInputException` → **call site 2:** Task 2 Step 4's test constructs one | ok — separate call site, separately checked: `ObjectSearchGrpcServiceTests.cs:9` also carries `using Iverson.Embeddings;`, and the type's single-string constructor matches the plan's `new EmptyEmbeddingInputException("…")` |
| C3 | Ordering: Task 1 before Task 2 | ok — C1 and C2 are the only cross-task references; Task 2 Step 1 references nothing from Task 1, as the plan states |

### Rule-like content (both failure directions)

| # | Rule | Disposition |
|---|---|---|
| R1 | `IsNullOrWhiteSpace` guard | ok — **over:** rejects only inputs no caller wants embedded. **under:** `""`, `"   "`, `"\t\n"` all caught; the `[InlineData]` set covers all three |
| R2 | `.Where(c => c.Text.Length > 0)` | ok — **over:** `c.Text` is pre-`Trim()`ed, so only zero-content windows drop. **under:** no content-bearing window can strip to length 0 |
| R3 | "body 370 chars → windows at 0/160/320, index 1 empty, survivors 0 and 2" | ok — **hand-simulated against `SplitIntoChunks`.** `maxChars` 200, `overlapChars` 40, `step` max(160,100)=160. w0 `[0,200)` → `"alpha"`; w1 `[160,360)` → all spaces → `""`; w2 `[320,370)` → `"omega"`; `start=480` ends the loop. The word-boundary branch fires on none of them: `text[200]` and `text[360]` are spaces (run spans 5–364), and w2's `end == text.Length`. Survivors are exactly `0` and `2` |
| R4 | Catch-clause ordering | ok — both directions considered: placed first it catches (correct); placed second it compiles clean and never fires, which is why the plan hoists it to a Global Constraint and pins it with a test per endpoint |
| R5 | "the custom schema leaves `Contextual` false" | ok — `ChunkDescriptor`'s 5-argument form takes the default; `:1207` and `:1392` pass a 6th argument explicitly, proving the parameter is optional and defaulted |

## 1. Verified-plan-assumptions cross-check

All 16 reconfirmed under a fresh read. Notes where this round re-derived rather than re-read:

- **P1–P8, P10, P11, P13, P14, P15, P16** — re-read at the cited evidence; all hold.
- **P9** — reconfirmed at `ObjectSearchGrpcServiceTests.cs:41`, and extended: the constructor configures no default `EmbedAsync` behavior, so the per-test stub is the only matching configuration. The plan's stub cannot be shadowed.
- **P12** — a language fact rather than a file fact: tuple element names propagate through `Where`/`ToList`, and both downstream uses (`.Select` at `:237`, `.Count` at `:329`) are element-wise, not positional. Holds.
- **Citation note (not a finding):** Task 1 Step 2's prose says `text.Length` is dereferenced at `:51`; it is at `:52`, and the activity block spans `:50-52` rather than `:50-51`. The operative instruction — "the very top of the method body" — and the Step 2 code block are both unambiguous and correct, so nothing an implementer writes changes. Recorded for accuracy only.

### Span check

Four plan dependencies had no covering assumption as scoped. All verified in-round; none is a risk.

1. **The gRPC test must reach `EmbedAsync` before anything earlier throws.** P8 covers invocation shape and P9 the substitute, but nothing states the request survives schema lookup, vector-field resolution and authorization. Verified: `ArticleSchema` declares `VectorFields = [VectorDescriptor("Title", …)]` and `ChunkFields = [ChunkDescriptor("Body", …)]` (`SchemaFixtures.cs:67` region) with `Authorization = BypassAuthorization()`, and the existing test at `:1127-1141` exercises this exact path with `Query = "q"`.
2. **The chunk upserts must arrive in index order** for `indexes.Should().Equal("0", "2")` to be a valid assertion. Verified: `Task.WhenAll` preserves input order and the write loop is a sequential `foreach` over `chunkResults`.
3. **The test-level upsert stub must take precedence over the constructor's `Arg.Any` stub** for the chunks collection, while the object-point upsert still falls through to the constructor's. Verified: NSubstitute resolves to the most recently configured matching call, and the constructor's stub at `IntelligenceStoreConsumerTests.cs:61` uses `Arg.Any<string>()` for the collection.
4. **`ChunkDescriptor`'s `contextual` parameter is optional.** P16 cites its omission at `:684` but not the parameter's defaulting. Verified via `:1207` (6th argument passed) and `:1392` (`true` passed).

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

✅ **Approve as-is.** §1 has no failed assumptions; §2 and §3 are both empty. The plan is ready for `subagent-driven-development`.

The two things that would ordinarily break a plan of this shape are both already closed: the test's window arithmetic is correct under simulation rather than by assertion, and the one constraint the compiler cannot enforce — catch-clause ordering — is hoisted to Global Constraints and pinned by a separate test per endpoint.
