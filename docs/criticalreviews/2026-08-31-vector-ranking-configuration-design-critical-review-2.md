# Critical Design Review: 2026-08-31-vector-ranking-configuration-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-31-vector-ranking-configuration-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration built before re-reading round 1; round 1's fixes are rows here, not the search area.

### Sections

| Section | Disposition |
|---|---|
| Header / scope | ok — "defaults equal whatever is shipped at implementation time" still agrees with §"Which defaults"; no drift introduced by round 1's edits |
| `## Why` | ok — both cited effects re-checked against the source document lines 20-25 (FreshStack AP +0.0059 p=0.0008 Holm-sig; SciFact nDCG@10 -0.0082 t=-2.54) and the model control (+0.0563, t=7.73); reproduced accurately |
| `### VectorRankingOptions` | ok — class shape still mirrors `EmbeddingServiceOptions.cs`; `public` is consistent with `ResultReranker`'s own accessibility |
| `### DecayOptions` | ok — now `public`; the stated reason (CS0051) and the ruled-out alternative (`MapGrpcService<ObjectSearchGrpcService>()` needs an accessible constructor) both check out, and `Program.cs:443` is confirmed as that call's location |
| `### Registration and validation` | ok — finiteness guard is now first, ahead of the range checks, and covers all four values by name |
| `### Which defaults` | ok — `main` `ResultReranker.cs:12` = 0.60/0.30/0.10 and `decay-share-triple-b` `:19` = 0.45/0.45/0.10 both re-confirmed; the instruction to read from the target branch rather than copy the literals is unchanged |
| `### Tests` | ok — now names all three `ObjectSearchGrpcService` sites including the target-typed one, and five validation tests |
| `## Verified assumptions` | see §1 |
| `## The fifth constant` | ok — still accurately attributes `HalfLifeDays` to the sibling sweep rather than the source document |
| `## A consequence worth naming` | ok — `VectorRanking__WBase` binding re-checked: `builder.Configuration` includes environment variables by default, and `docker-compose.yml:372,:457` already relies on the same `__` convention for `Embeddings__ModelId` |
| `## Out of scope` | ok — item-2 deferral premise re-verified this round: `git show main:...EmbeddingService.cs \| grep -c Prefix` returns **0**, so `main` still has no prefixing at all |
| `## Known issues inherited` | ok — all three staleness claims re-checked and still true |

### Rules and operands (both failure directions)

| Rule | Disposition |
|---|---|
| Finiteness guard (new this round) | over-inclusion: ok — rejects no finite value. under-inclusion: ok — `double.IsFinite` is false for `NaN`, `+Infinity`, `-Infinity`, and the guard names all four values explicitly, so no operand is left unchecked |
| Weight non-negativity | over-inclusion: ok — admits 0 for individual weights, which is meaningful (disable one signal). under-inclusion: ok — the non-finite escape found in round 1 is now closed by the preceding guard |
| All-zero guard | over-inclusion: ok — fires only when all three are 0. under-inclusion: ok — `NaN`/`Infinity` can no longer reach it |
| `Lambda is < 0 or > 1` | over-inclusion: ok — admits both endpoints, both meaningful (pure relevance / pure diversity). under-inclusion: ok — `NaN` now caught upstream |
| `HalfLifeDays` finite and `> 0` | over-inclusion: ok. under-inclusion: ok — the `Infinity`-disables-decay case named in the spec text is closed by the finiteness half |
| Unparseable value (`"abc"`) | ok — already throws at `Bind`; spec now states this explicitly, so the rule set's boundary is documented rather than implied |
| "Defaults equal the shipped constants" | ok — both branches' constants re-read; the spec states the invariant as an equality the tests assert, not a literal to copy |
| *Candidate:* astronomically large finite weights (`1e308`) overflow the weight sum to `Infinity`, yielding `NaN` fused scores while passing every check | **dropped** — fails literal-wrongness. `1e308` is not a plausible operator misconfiguration, and the class of silent-`NaN` misconfiguration was already raised and closed in round 1; re-raising it through an absurd input would be manufacturing a finding |

### Data-flow arrows (persistence boundaries flagged)

| Arrow → consuming operation | Disposition |
|---|---|
| config section → `Bind(opts)` | ok — absent-section and partial-section behaviour, and invariant `double` parsing, all established empirically and unchanged by round 1's edits |
| bound instance → `AddSingleton(Options.Create(opts))` → `IOptions<T>` | ok — probe confirmed `GetService<IOptions<VectorRankingOptions>>()` resolves non-null |
| `IOptions<VectorRankingOptions>` → `ResultReranker._o` → `Rerank` | ok — `WBase`/`WCentroid`/`WDecay` all exist on the options class |
| `IOptions<VectorRankingOptions>` → `ResultDiversifier` → `Diversify` | ok — `Lambda` exists on the same class |
| `IOptions<DecayOptions>` → `ObjectSearchGrpcService` → `DecayFor` → `ComputeDecay` | ok — the CS0051 blocker is removed; `Program.cs:7` already carries `using Iverson.Api.Grpc;` and `:13` `using Iverson.Vector;`, so both options types resolve where they are bound |
| `DecayFor` call site `:260` | ok — inside an instance method, so it can source `decayOptions.Value.HalfLifeDays`; one row per call site, not one per operation |
| `DecayFor` call site `:447` | ok — same, verified separately rather than assumed a copy of `:260` |
| env var `VectorRanking__WBase` → config → options | ok — same mechanism as the deployed `Embeddings__ModelId`; no persistence boundary crossed |
| No arrow crosses a persistence or serialization boundary | ok — configuration is read once at startup into memory; nothing is written and re-read |

### Registration move (new surface this round)

| Check | Disposition |
|---|---|
| Consumers of `IResultReranker`/`IResultDiversifier` outside `Iverson.Vector` and tests | ok — grep returns exactly `ObjectSearchGrpcService.cs:39-40`. No hosted service, consumer, or reconciliation worker takes either |
| Eager resolution that would fire before `AddVectorRanking` | ok — `Program.cs` has no `BuildServiceProvider`; its `GetRequiredService` calls (`:199-216`) are inside factory lambdas for record-store repositories, none touching either interface. The three `AddHostedService` registrations (`:256-258`) do not either |
| `AuthTestWebApplicationFactory` | ok — it does not call `AddQdrant` itself (only references it in a comment and sets `Qdrant__ApiKey`); it boots the real `Program`, so it picks up `AddVectorRanking` automatically and, with no `VectorRanking`/`Decay` section configured, binds to defaults that pass validation |
| `ServiceCollectionExtensionsTests` after the move | ok — asserts only `GetRequiredService<QdrantClient>()`; `ServiceCollectionExtensions.cs:50-51` re-confirmed as the two lines being relocated |
| Config section-name collision | ok — `appsettings.json` top-level keys are `Logging`, `AllowedHosts`, `Kestrel`, `ConnectionStrings`, `Qdrant`, `Kafka`, `Otel`, `Authentication`. Neither `VectorRanking` nor `Decay` collides |
| Residual `internal` references to the options class after round 1's fix | ok — finish-the-surface check: five remaining `internal` mentions, all correct in context (three explain why `DecayOptions` is *not* internal; one refers to `DecayFieldResolver`, which genuinely stays internal; one is A19) |

## 1. Verified-assumptions cross-check

All 23 listed assumptions still hold under a fresh read. Re-confirmed this round with new reads rather than carried forward: A2 (`Iverson.Vector.csproj` still has no Options package), A6 (`ObjectSearchGrpcService.cs:39-40`), A8 (`Program.cs:24`), A15 (both branches' constants), A20 (`ComputeDecay` still 1 declaration + 1 production call + 7 test calls), A21 (`DecayFor` at `:764`, called at `:260` and `:447`), A22 (the CS0051 probe result), A23 (all three construction sites, including the target-typed `new(` at `ObjectSearchVectorIntegrationTests.cs:80`).

**A19 — still holds; its subject has moved.** The cited evidence (`Iverson.Api.csproj:10-12`, `InternalsVisibleTo` → `Iverson.Api.Tests`) is intact, so the assumption is reconfirmed as written. Worth recording that after round 1's fix there is no longer an internal *options* class: what A19 now covers in practice is `DecayFieldResolver` remaining `internal static` while `DecayFieldResolverTests` calls its `ComputeDecay` seven times. The dependency is real and still covered by this assumption's evidence; only the row's wording describes the pre-fix design. Not a finding — the implementation is unaffected either way.

### Span check

Span check found no uncovered dependency. The two gaps round 1 raised are now covered by A22 (accessibility of an options type in a public constructor signature) and A23 (the three `ObjectSearchGrpcService` construction sites). The registration-move surface examined in §0 — sole consumer, no eager resolution, test-factory behaviour, section-name collision — traces back to A6, A7 and A9 as scoped.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (`internal DecayOptions` cannot be a parameter of `ObjectSearchGrpcService`'s public primary constructor — CS0051). Resolved: the type is now `public`, with the CS0051 reason and the ruled-out internal-constructor alternative both recorded in the spec.
- **Round 1 §2.2** (non-finite values pass every validation check). Resolved: a `double.IsFinite` guard is now the first check in the `VectorRankingOptions` block and is required for `HalfLifeDays`; the spec explains the `Infinity`-disables-decay and `NaN`-passes-comparisons mechanisms and notes that `"abc"` already throws at `Bind`. Validation tests went from four to five.
- **Round 1 §1 span 1.a** (no assumption covered the accessibility rule). Resolved: A22 added, with the probe error as evidence and an explicit note that `InternalsVisibleTo` does not bear on it.
- **Round 1 §1 span 1.b** (no assumption enumerated `ObjectSearchGrpcService`'s construction sites). Resolved: A23 added listing all three, and the Tests section now names them, flagging the target-typed `new(` that a grep would miss.

## 5. Recommendation

✅ **Approve as-is** — §2 and §3 are both empty. The surface enumerated in §0 is fully disposed, the one candidate generated this round failed the literal-wrongness test and was dropped rather than promoted, and every round-1 finding is closed. Spec is ready for implementation planning.
