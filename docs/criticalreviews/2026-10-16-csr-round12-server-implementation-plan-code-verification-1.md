# Dafny Code Verification: 2026-10-16-csr-round12-server-implementation-plan (Round 1)

**Implementation plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md
**Plan-stage review:** /home/ben/repositories/Iverson/docs/criticalreviews/2026-10-16-csr-round12-server-implementation-plan-formal-verification-1.md
**Code ref:** f7a0842fc0e707359e777ddf44f7c2e23050c968 (branch csr12-server) | **Diff base:** 9bd10927bf780f818ecb0c3657e80f2559ee747a | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-16-csr-round12-server-implementation-plan/

## 0. Coverage

### Run notes

- **Diff base.** The user chose 9bd10927, where csr12-server left main. The only commit between it and the plan's last commit (c79d7de0) is the plan-stage verification commit, which changes docs only.
- **Verifier setting, carried from plan stage.** Every model was verified, and every G5 mutant run, with `--manual-lemma-induction`: Dafny 4.11's automatic lemma induction can prove false statements (recorded in the plan-stage review §0). Each `.dfy` file's `Re-run:` line carries the flag.
- **Provers ran in waves** to bound memory use, each verifier run under a 2 GB `systemd-run` scope.
- **Gates.** All 31 candidates passed G0-G4; no new requiring code declarations (G4 requires identical to the starting model in every candidate).
- **G5 method.** For 80 obligations the plan-stage killing mutant was replayed as the same one-line change on the code-stage model; 3 of those kills land on helpers deeper in the proof closure. The other 16 obligations, whose plan-stage mutated line had changed (or whose plan-stage mutant predated a model repair), got fresh mutants from G5 agents. The controller re-ran the 7 Canonicalize/OwnerQueries kills itself after a scratch-script collision between two agents. Every one of the 96 obligations was killed.
- **Warnings.** Three models print "unusual indentation" warnings that sit in frozen property text: StructSerializer.FoldKeys P2, EnrichmentTargetRules.Violation P6/P7, PopularitySignalEligibility.IsEligible P2. They are cosmetic, were present at plan stage, and do not affect verification.
- **Witnesses.** None were needed: no obligation failed.
- **Relabelling.** No member was `not found`, `signature changed` or `kept by cascade`, so nothing was relabelled.

### Per candidate

#### `SchemaBuilder.NormalizeDeclaredNames`

Members:

- `NormalizeDeclaredNames`: found, Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:279-302. Code changed: **yes**, the column-spelling dictionary is now built from the new shared helper `ColumnSpellingsByName` (SchemaBuilder.cs:311-323, commit 049709a1) instead of the inline `Distinct` → `GroupBy` → `Single` chain; the element selector is now `kv.Value[0]`. Signature unchanged.

Correspondence:

| Worktree file:line (introduced by) | Dafny lines | Note |
|---|---|---|
| SchemaBuilder.cs:279-280, 289-302 (T1 S3, commit 2be6ca19) | 207-208, 213-227 `NormalizeDeclaredNames` | Same as the starting model. `descriptor with {...}` becomes a datatype update. |
| SchemaBuilder.cs:281-284 (commit 049709a1, the refactor that shares the map with PayloadKeyCanonicalizer) | 209-212; `WhereCountIsOne` 81-88; `Index0` 91-94; `ToDictionaryFrom` 106-116 | **Deviation from the starting model.** The source is `ColumnSpellingsByName(d)`. `.Where(kv => kv.Value.Count == 1)` maps to `WhereCountIsOne`. The element selector is now `kv.Value[0]` (the List indexer), modelled as `Index0` with Threw on an empty list, where it was `Single()`. `Dictionary.Add` still gives Threw on a duplicate key ignoring case. |
| SchemaBuilder.cs:286-287 (T1 S3) | 180-185 `ColumnSpelling`, 119-129 `FindEntry` | Unchanged. |
| SchemaBuilder.cs:291-293 (T1 S3) | 188-192 `SelectRelations` | Unchanged. |
| SchemaBuilder.cs:294-300 (T1 S3); SchemaDescriptor.cs:162-166 `OwnerField` init accessor | 219-226, `InitOwnerField` 201-205, `SelectFieldPermissions` 195-198 | Unchanged. SchemaDescriptor.cs differs from the plan only in a comment. |
| SchemaBuilder.cs:311, 313, 322-323 (commit 049709a1) | 170-173 `ColumnSpellingsByName` | New. The `Dictionary<string, List<string>>(OrdinalIgnoreCase)` is modelled as `seq<Grouping>` in insertion order, with Key = the key the entry was created under and Elements = the List value. Insertion order matches .NET enumeration order for a Dictionary with no removals. The result is used only through lookups, so the order does not affect any property. |
| SchemaBuilder.cs:314, 316 (049709a1) | 159-165 `ByNameFrom` (input 172: `Prepend(SelectName(...), KeyColumn.Name)`) | The foreach loop is a recursion over the sequence. The tenant `continue` is the `IsTenantColumn(xs[0])` branch, and the branch order is kept. |
| SchemaBuilder.cs:317-318 (049709a1) | 138-144 `TryGetOrAddEmpty`, using `FindGrouping` 68-78 | `TryGetValue` is lookup ignoring case. On a miss, `columns[name] = spellings = []` appends the entry `(name, [])`. The function returns the dictionary and the index that `spellings` aliases. |
| SchemaBuilder.cs:319-320 (049709a1) | 149-156 `AddSpelling` | `Contains(name, StringComparer.Ordinal)` is sequence membership (`in`). `Add` appends to the aliased entry's list. |
| (proof only) | 229-609 | `WhereNotTenant`, `W`, `Groups`, `DictR` and `AddToGrouping` are proof helpers, not transliteration. The starting model's `DistinctOrdinal`, `DistinctFrom`, `GroupByIgnoreCase`, `GroupFrom` and `Single`, and the lemmas `DistinctFromProps`, `DistinctMembers` and `GroupFromInv`, are removed. New lemmas: `AddSpellingEq` (the loop body leaves the dictionary unchanged for a spelling already seen, and equals `AddToGrouping` otherwise) and `ByNameFromInv` (the grouping invariant over the raw name list, with duplicates allowed in P). `GroupsInv` and `ColumnSpellingRenames` now use `W(d)` in place of `Distinct(d)`. |
| Frozen properties | 610-end | Byte-identical to the starting model (checked with diff). |

Modelling caveats:

- The `Dictionary<string, List<string>>(OrdinalIgnoreCase)` is modelled as an insertion-ordered `seq<Grouping>`; the result is used only through lookups, so order affects no property.
- Proof repair replaced the starting model's `DistinctOrdinal`/`GroupByIgnoreCase`/`Single` helpers and lemmas with `AddSpellingEq` and `ByNameFromInv`; the frozen properties (Dafny 610-end) are byte-identical to the starting model.

Obligations:

- `P1_OwnerAndFieldPermissionNamesTakeTheColumnsSpelling`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_OwnerAndFieldPermissionNamesTakeTheColumnsSpelling.r3 (line 183: case Some(k) => columnSpelling[k].Value -> case Some(k) => name; failed: ColumnSpellingRenames) ``
- `P2_OnlyNonOneToManyForeignKeysAreRewritten`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_OnlyNonOneToManyForeignKeysAreRewritten.r1 (line 191: if rs[i].Kind == OneToMany then rs[i] else rs[i].(ForeignKey := ColumnSpelling(c -> if rs[i].Kind != OneToMany then rs[i] else rs[i].(ForeignKey := ColumnSpelling(c; failed: P2_OnlyNonOneToManyForeignKeysAreRewritten) ``
- `P3_NormalizationNeverFails`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_NormalizationNeverFails.r2 (failed: AddInv, AddSpellingEq — helper(s) in the proof closure of P3_NormalizationNeverFails) ``

#### `SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties`

Members:

- `RejectCaseDuplicateProperties`: found, Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:453-467 (call site :85). Code changed: **yes**, commit c160ab79 replaced `FirstOrDefault(g => g.Count() > 1)` with `Where(g => g.Count() > 1).ToList()` and the refusal now names every colliding group, joined with "; ". Signature unchanged (`void(TypeDescriptor)`).

Correspondence:

| Worktree file:line (SchemaRegistrationOrchestrator.cs) | Introduced by | Dafny lines | Note |
|---|---|---|---|
| 453 `private static void RejectCaseDuplicateProperties(TypeDescriptor typeDesc)` | T1 S3 (2be6ca19) | 101 | void-or-throw is modelled as `Check` (Pass / Refused), per "exceptions are outcomes". TypeDescriptor/PropertyDescriptor are reduced to the fields read (29, 32), as in the starting model. |
| 456-457 `typeDesc.Properties.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)` | T1 S3 (2be6ca19) | 37-70 (Group, FindGroup, AddTo, GroupByName), 103 | Unchanged from the starting model. Lookup semantics: groups appear in order of first key appearance, the key is the first element's name, and EqIC comes from the approved `OrdinalUpper` (19-22). |
| 458-459 `.Where(g => g.Count() > 1).ToList()` | changed post-plan (c160ab79), within the T1 S3 member | 72-78 (WhereDup), 103 | **Deviation from the starting model:** `FirstOrDefaultDup` (FirstOrDefault) became `WhereDup`, an order-preserving filter. ToList is the identity on the sequence. |
| 460 `if (duplicates.Count == 0) return;` | changed (c160ab79) | 104 | `\|duplicates\| == 0` → Pass. This was `duplicate.None?`. |
| 462 `var groups = string.Join("; ", duplicates.Select(g => string.Join(", ", g.Select(p => $"'{p.Name}'"))));` | new (c160ab79) | 80-85 (Join), 87-92 (QuotedNames), 94-99 (GroupTexts, new), 106 | **New.** GroupTexts maps each group to its ", "-joined quoted names, and the outer Join uses "; ". |
| 463-466 `throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{TypeName}' declares properties {groups} whose names ... rename one of them."))` | T1 S3, message changed (c160ab79) | 107-110 | Refused(InvalidArgument, detail). The detail interpolates `groups` (it was `Join(", ", QuotedNames(duplicate.Elems))`). The literal text is identical to the code's. |
| proof helpers (no source) | n/a | 113-282 | Filter/GroupInv/AddToInv/GroupByInv are kept from the starting model. `FirstOrDefaultDupSome` was replaced by `WhereDupNonEmpty` (268-282), which has the same shape and proves `\|WhereDup(gs)\| > 0 <==> exists k :: \|gs[k].Elems\| > 1`. The P1 body calls it in place of FirstOrDefaultDupSome (one line). |
| property P1 (frozen) | n/a | 288-311 | The citation, header and clauses are byte-identical to the brief and the starting model (checked by diff). |

Modelling caveats:

- `FirstOrDefaultDupSome` was replaced by `WhereDupNonEmpty` (same shape); the P1 body calls it in place of the old helper (one line).

Obligations:

- `P1_PropertiesEqualIgnoringCaseAreRefused`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P1_PropertiesEqualIgnoringCaseAreRefused.1 (line 77, WhereDup filter `|gs[0].Elems| > 1` mutated to `> 2`; failed row: WhereDupNonEmpty (correctness), a helper in P1 proof closure; further mutants a2 line 104 `== 0`->`> 0` failed P1 (correctness), a3 line 61 and a4 line 51 failed AddToInv (correctness)) ``

#### `SchemaRegistrationOrchestrator.NavigationColumnCheck`

Members:

- `RegisterAsync` navigation/column loop: found, Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:274-292. Code changed: no.

Correspondence:

| Worktree file:line (SchemaRegistrationOrchestrator.cs) | Introduced by | Dafny lines | Note |
|---|---|---|---|
| 274-278 (comment: runs after the collision loop at 263-272) | T1 S3 | 68, 85-88 | The model checks only this loop. The earlier loops (FK membership/type/name at 201-256, collision at 263-272) are outside the candidate, so the model ignores them, same as the starting model |
| 279 `foreach (var relation in descriptor.Relations)` | T1 S3 | 69-74, 82 (`NavigationColumnLoop(d, i)`, recursion on i+1) | foreach becomes index recursion; same order |
| 281-283 `ScalarColumns.Select(c => c.Name).Prepend(KeyColumn.Name)` | T1 S3 | 31-43 (`ScalarNames`, `ColumnNames`) | Same order: key first, then scalars. The tenant column is an entry of ScalarColumns, so it is included (no IsTenantColumn filter here, unlike the FK loop at 216-218) |
| 284 `.FirstOrDefault(name => string.Equals(name, relation.PropertyName, OrdinalIgnoreCase))` | T1 S3 | 59-66 (`FirstMatchIC`), 14-17 (`OrdinalUpper` approved axiom, `EqIC`) | null default becomes `None`. Names are modelled as non-null `CsString`, the same choice the starting model makes (`string Name` is non-nullable in the record) |
| 285 `if (column is not null)` | T1 S3 | 76 | |
| 286-291 `throw new RpcException(new Status(InvalidArgument, …))` | T1 S3 | 77-80 (`Refused(InvalidArgument, msg)`) | Exception as outcome. The message is transliterated verbatim; `{relation.Kind}` becomes `KindName` (lines 50-57, enum ToString, matches SchemaDescriptor.cs:143) |
| 292 (end of loop, fall through to `descriptors.Add` at 294) | T1 S3 | 72 (`Pass`), 81-82 | |

Modelling caveats:

- Only this loop is modelled; the earlier FK and collision loops (:201-256, :263-272) are outside the candidate, as at plan stage.

Obligations:

- `P1_ANavigationNamedLikeAnyColumnIsRefused`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ANavigationNamedLikeAnyColumnIsRefused.r1 (line 75: var column := FirstMatchIC(ColumnNames(d), relation.PropertyName); -> var column := FirstMatchIC(ScalarNames(d.ScalarColumns), relation.PropertyName);; failed: LoopSpec) ``

#### `PayloadKeyCanonicalizer.Canonicalize`

Members:

- `Canonicalize`: found, Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs:36-73. Code changed: **yes**, the column-map construction moved into `SchemaBuilder.ColumnSpellingsByName` (SchemaBuilder.cs:311-323, commit 049709a1) and is wrapped in a per-descriptor `ConditionalWeakTable` cache (PayloadKeyCanonicalizer.cs:34, :38, commit 158254cc); the builder body and the scan/rename code are otherwise the plan's.

Correspondence:

| Worktree file:line | Introduced by | Dafny lines | Notes |
|---|---|---|---|
| Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs:34 (the `ColumnSpellings` ConditionalWeakTable) | post-plan commit 158254cc (not a plan step) | 187-189 (comment), 191 | **Deviation:** the cache is modelled as a direct call to its factory. `GetValue(schema, f)` returns `f(schema)` on the first call and that same value afterwards. The value depends only on the instance's `KeyColumn.Name` and `ScalarColumns[*].Name`. Those are `init`-only members of a sealed record, and `ScalarColumns` is an `IReadOnlyList`, so a cached value always equals a fresh build. A racing double build is possible, but `GetValue` publishes one result, and every build is the same function of the instance. Callers only read the cached map. |
| PayloadKeyCanonicalizer.cs:36-37 (signature) | T2 S3 | 190 | `SchemaDescriptor schema` becomes `d: Desc`, holding only the fields that are read. The `Struct payload` (MapField) becomes `p: Payload`, an insertion-ordered sequence. In-place mutation becomes a returned `Canonical(q)`. Both parameters are non-nullable, so no `Option` is needed. |
| PayloadKeyCanonicalizer.cs:38 (`var columns = ColumnSpellings.GetValue(schema, SchemaBuilder.ColumnSpellingsByName)`) | 158254cc + 049709a1 (post-plan) | 191 | See the line 34 row. |
| Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:311-323 (`ColumnSpellingsByName`) | 049709a1 (post-plan extraction of the T2 S3 builder; the body is identical to plan lines 32-40) | 108-132 (`BuildColumns` 109-127, `ColumnSpellingsByName` 130-132), with `ColumnNames` 34-36 | `ScalarColumns.Select(c => c.Name).Prepend(KeyColumn.Name)` becomes `ColumnNames(d)`. The OrdinalIgnoreCase `Dictionary` becomes an association list with case-insensitive lookup `IcTryGet` (lines 91-102), keyed by the spelling that first created the entry. `spellings.Contains(name, Ordinal)` becomes `name in spellings`. `IsTenantColumn` is `EqIC(n, "__TenantId")` over the approved `OrdinalUpper`. |
| PayloadKeyCanonicalizer.cs:40-41 (`keyFor`, `renames`) | T2 S3 | 192 (`map[]`, `[]`) | An ordinal `Dictionary` becomes a Dafny `map`. The `List` becomes a `seq`. |
| PayloadKeyCanonicalizer.cs:42-44 (foreach + TryGetValue/continue) | T2 S3 | 142-151 | Iteration over `payload.Fields.Keys` becomes recursion over the index `n` in insertion order. |
| PayloadKeyCanonicalizer.cs:46-52 (`spellings.Count > 1`: throw InvalidArgument) | T2 S3 | 153-158 | The thrown `RpcException` becomes `ScanRefused(InvalidArgument, msg)`. `string.Join` with quotes becomes `QuoteJoin` (134-138). |
| PayloadKeyCanonicalizer.cs:54 (`spellings[0]`) | T2 S3 | 159-161 | An empty list would throw `ArgumentOutOfRangeException`, modelled as `ScanFaulted`. It cannot happen, because every entry gets at least one spelling (`SpellingsDistinct`). |
| PayloadKeyCanonicalizer.cs:55-60 (`keyFor.TryGetValue(column)`: throw InvalidArgument) | T2 S3 | 162-165 | The thrown exception becomes `ScanRefused(InvalidArgument, msg)`. |
| PayloadKeyCanonicalizer.cs:62-64 (`keyFor[column] = key`; ordinal-inequality rename) | T2 S3 | 167-169 | |
| PayloadKeyCanonicalizer.cs:67-72 (apply renames: read, Remove, indexer set) | T2 S3 | 172-185, with `PIndex` 57-68, `PRemove` 71-75, `PSet` 78-82 | A missing `Fields[from]` would throw `KeyNotFoundException`, modelled as `None`, then `Faulted`. The MapField indexer replaces a present key in place and appends an absent one. |
| PayloadKeyCanonicalizer.cs:36-73 (overall result) | T2 S3 | 190-199 | A refusal becomes `Refused`, a fault becomes `Faulted`, and success becomes `Canonical(q)`. |

Modelling caveats:

- The `ConditionalWeakTable` cache is modelled as a direct call to its pure factory: the cached value depends only on init-only members of a sealed record, so a cached value always equals a fresh build.

Obligations:

- `P1_AKeyNamingOneColumnTakesItsSpelling`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P1_AKeyNamingOneColumnTakesItsSpelling.a1 (line 168 `key != column` flipped to `key == column`, so no rename is recorded; failed rows: ScanOkIfGood (correctness), ScanRefusesIfTaken (correctness), ScanNoFault (correctness), ScanRefusesMulti (correctness), ScanRefusesPair (correctness); P1's own row did not fail, kill is via helpers in its call closure) ``
- `P2_TwoKeysNamingOneColumnAreRefused`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_TwoKeysNamingOneColumnAreRefused.r1 (line 167: var keyFor1 := keyFor[column := key]; -> var keyFor1 := keyFor;; failed: ScanNoFault, ScanRefusesPair) ``
- `P3_AKeyMatchingSeveralColumnsIsRefused`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_AKeyMatchingSeveralColumnsIsRefused.r1 (line 154: if |spellings| > 1 then -> if |spellings| > 2 then; failed: ScanNoFault, ScanRefusesMulti) ``
- `P4_KeysNamingNoColumnAndTheTenantKeyAreLeftAlone`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P4_KeysNamingNoColumnAndTheTenantKeyAreLeftAlone.a1 (line 115 `if IsTenantColumn(name)` replaced by `if false`, so the tenant column is no longer skipped when building the column dictionary; failed row: BuildColumnsInv (correctness), a helper in P4's proof closure) ``
- `P5_NothingElseIsRefused`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P5_NothingElseIsRefused.a1 (line 154 `|spellings| > 1` changed to `|spellings| > 0`, so a single-column match is refused; failed row: ScanOkIfGood (correctness), a helper in P5's proof closure via ApplyPrep) ``

#### `StructSerializer.FoldKeys`

Members:

- `FoldKeys`: found, Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs:36-53. Code changed: no.
- `ProtoValueToObject`: found, ProtoPayloadHelper.cs:55-64. Code changed: no.
- `SerializePayload`'s column set: found, ProtoPayloadHelper.cs:27-31 (cache field :17). Code changed: **yes**, the set is now cached per descriptor instance in a `ConditionalWeakTable`; model text changed only in comments.

Correspondence:

| Worktree file:line | Task/step | Dafny lines | Note |
|---|---|---|---|
| ProtoPayloadHelper.cs:13 (`s_noColumns`) | pre-existing, used by T2 S3 | 73 (`{}` in ColumnSet), 124 (`FoldKeys(fs, {})`) | empty ordinal HashSet modelled as `{}` |
| ProtoPayloadHelper.cs:17 (`s_columnNames` ConditionalWeakTable) | T2 S3 as built (not in plan) | 70-75 (comment on ColumnSet) | Deviation (cache, not modelled as state): keyed on reference identity; `SchemaDescriptor.KeyColumn`/`ScalarColumns` are init-only (SchemaDescriptor.cs:34-35) and `ColumnDescriptor` is a positional record (SchemaDescriptor.cs:117), so the cached set equals the set computed from the descriptor value. Modelled as the pure function ColumnSet(d). |
| ProtoPayloadHelper.cs:27-31 (`SerializePayload`, column set) | T2 S3 | 77-81 (SerializeFold), 70-75 (ColumnSet) | `schema is null` -> `Option.None` -> `{}`; `JsonSerializer.Serialize` excluded (properties are about the folded dictionary). `Select(c=>c.Name).Append(KeyColumn.Name).ToHashSet(Ordinal)` -> set union. |
| ProtoPayloadHelper.cs:33-34 (`UpperFirst`) | pre-existing | 55-59 | `char.ToUpperInvariant` = approved axiom CharToUpperInvariant |
| ProtoPayloadHelper.cs:36-53 (`FoldKeys`) | T2 S3 | 83-88 (FoldKeys), 90-111 (FoldLoop) | foreach as recursion over index; MapField iteration order = seq order; the RpcException (45-47) is the `Refused(InvalidArgument, ...)` outcome; `originalNameOf` Dictionary -> map; `folded[upper] =` -> DictSet (61-68). |
| ProtoPayloadHelper.cs:42 | T2 S3 | 100 | `columnNames.Contains(key) ? key : UpperFirst(key)` |
| ProtoPayloadHelper.cs:43-48 | T2 S3 | 101-104 | TryGetValue collision -> Refused |
| ProtoPayloadHelper.cs:49-50 | T2 S3 | 106-110 | a Raised from ProtoValueToObject propagates as Refused |
| ProtoPayloadHelper.cs:55-64 (`ProtoValueToObject`) | T2 S3 | 113-127 | `_ => null` (KindCase None) not representable in Val, as in starting model; nested struct uses `s_noColumns` -> `{}` (124) |
| ProtoPayloadHelper.cs:61 (List Select/ToList) | T2 S3 | 129-140 (ConvertList) | in order, first throw propagates |

Modelling caveats:

- The cache is modelled as the pure function `ColumnSet(d)`; the descriptor members it reads are init-only, so the cached set equals the computed set.
- The verifier prints one "unusual indentation" warning in the frozen P2 text (cosmetic, present at plan stage).

Obligations:

- `P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted.1 (line 100: `if key in columnNames then key else UpperFirst(key)` -> `UpperFirst(key)`, column-name keys no longer keep their spelling; failed row: FoldLoopFacts (correctness), a helper in P1's call closure via FoldKeysFacts) ``
- `P2_NestedKeysAreAlwaysUpperFirsted`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P2_NestedKeysAreAlwaysUpperFirsted.1 (line 124: `FoldKeys(fs, {})` -> `FoldKeys(fs, set f | f in fs :: f.0)`, nested keys keep their spelling instead of UpperFirst; failed row: P2_NestedKeysAreAlwaysUpperFirsted (correctness)) ``

#### `EngagementRepository.UpsertEntries`

Members:

- `UpsertEntries`: found, Iverson.Server/Iverson.StarRocks/EngagementRepository.cs:522-537 (T3 S3, commit b60576f4). Code changed: no (byte-identical to the plan block).

Correspondence:

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| EngagementTableSchema.cs:3-9 (pre-existing) `EngagementTableSchema(TableName, KeyColumn, Columns)` + `SortKey` | 28-29 `EngTable` | `SortKey` is left out because UpsertEntries never reads it. `IReadOnlyList` is modelled as `seq`. The starting model already made both choices. |
| EngagementTableSchema.cs:11-14 (pre-existing) `EngagementColumnSchema(Name, SrType, IsNullable)` | 26-27 `EngCol` | `SrType` is named `SqlType`, which is a rename only (the starting model's choice). The field is unused. |
| EngagementRepository.cs:522 (T3 S3) signature | 63 | The return type `List<KeyValuePair<string, JsonElement>>` is modelled as `Result<seq<(CsString, JsonEl)>>`. A thrown exception is the `Threw` outcome. |
| EngagementRepository.cs:524 (T3 S3) `JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)` | 32 (approved axiom), 67-68 | The approved axiom `DeserializeToDictionary` stands in for the call. A `JsonException` becomes `ParseFailed`, which maps to `Threw`. A non-null `payloadJson` is signature-forced (non-nullable `string`). The dictionary's enumeration order is the order of `entries`. |
| EngagementRepository.cs:525 (T3 S3) `?? new Dictionary<…>()` | 70 | `ParsedNull` maps to `[]`. |
| EngagementRepository.cs:527-530 (T3 S3) `Columns.Select(c => c.Name).Append(KeyColumn.Name).ToHashSet(StringComparer.Ordinal)` | 34-45 `ColumnNameSet`, `KnownCols`; 72 | Ordinal comparison is UTF-16 code-unit equality, so a Dafny `set<seq<char>>` models it. The set loses no information, because the code only tests membership in it. |
| EngagementRepository.cs:532-535 (T3 S3) `.Where(kv => knownCols.Contains(kv.Key) && ValueKind != Object && ValueKind != Undefined)` | 47-53 `Keep` | The `&&` order is the same as the code's. |
| EngagementRepository.cs:532, 536 (T3 S3) `row.Where(...).ToList()` | 55-61 `FilterEntries`; 74 | The filter is order-preserving and its result is wrapped in `Ok`. |

Obligations:

- `P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected.r1 (line 51: && kv.1.kind != JObject -> && true; failed: P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected) ``

#### `EngagementStoreConsumer.WithOwnerValue`

Members:

- `WithOwnerValue`: found, Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs:130-139. Code changed: **yes**, the inline case-variant removal loop now calls the shared helper `JsonObjectKeys.RemoveIgnoringCase` (Iverson.Server/Iverson.Sql/JsonObjectKeys.cs:14-21, commit 049709a1); the helper body is the plan's loop. Signature unchanged.

Correspondence:

| Worktree file:line | Introduced by | Dafny lines | Note |
|---|---|---|---|
| Iverson.Api/Consumers/EngagementStoreConsumer.cs:130 (signature) | pre-existing (f0223125); in plan T3 S3 | 98-99 | `string?` becomes `Option<CsString>`; `string` becomes `CsString` |
| EngagementStoreConsumer.cs:132 `JsonNode.Parse(payloadJson)!.AsObject()` | pre-existing; plan T3 S3 | 101-102 | Parse failure (JsonException, or a non-object root / null literal that makes `!`/`AsObject` throw) is the `Threw` outcome through the approved `ParseJsonObject` axiom. Unchanged from the start model |
| EngagementStoreConsumer.cs:133 `JsonObjectKeys.RemoveIgnoringCase(node, ownerField)` | 049709a1 (post-plan refactor of the T3 S3 inline loop) | 93 | **Deviation from the start model:** it now calls the new model function `RemoveIgnoringCase` instead of inlining `RemoveAll(o, FilterEqIC(Keys(o), ownerField))`, which matches the built call structure. The two are equal by definition |
| Iverson.Sql/JsonObjectKeys.cs:14-21 `RemoveIgnoringCase` | 049709a1 | 68-77 (uses 39-66) | New model function. In-place mutation of `obj` is modelled as returning the mutated object. `.Select(kv => kv.Key)` maps to `Keys`, `.Where(OrdinalIgnoreCase)` to `FilterEqIC`, `.ToList()` + foreach `Remove` to `RemoveAll`/`RemoveKey`. All are unchanged from the start model |
| EngagementStoreConsumer.cs:135-136 `if (authoritativeOwnerValue is not null) node[ownerField] = JsonValue.Create(...)` | b60576f4 (T3 S3) / f0223125 | 94-95 | `SetKey` models the indexer set, which replaces in place when the key is present and appends when absent. Unchanged |
| EngagementStoreConsumer.cs:138 `node.ToJsonString()` | pre-existing | 103 | Approved axiom `ToJsonString`. Unchanged |

Modelling caveats:

- `RemoveIgnoringCase` is a new model function equal by definition to the starting model's inlined `RemoveAll(o, FilterEqIC(Keys(o), ownerField))`; no proof repair was needed.

Obligations:

- `P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey.r1 (line 187: var node := RemoveAll(o, FilterEqIC(Keys(o), ownerField)); -> var node := o;; failed: P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey) ``
- `P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains.r1 (line 95: else node -> else o; failed: P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains) ``

#### `PopularitySignalEligibility.IsEligible`

Members:

- `IsEligible(registry)` overload: found, Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs:21-22. Code changed: no.
- `IsEligible(child)` overload: found, PopularitySignalEligibility.cs:24-58. Code changed: no.
- `Readers`: found, PopularitySignalEligibility.cs:60-61. Code changed: no.

Correspondence:

| Worktree file:line (PopularitySignalEligibility.cs, T4 S5) | Dafny | Note |
|---|---|---|
| :21-22 `IsEligible(parent, relation, SchemaRegistry)` | `IsEligibleIn` | Unchanged. |
| :24-58 `IsEligible(parent, relation, SchemaDescriptor?)` | `IsEligible`, with helpers `WhereKeep`, `EqualsICNullable`, `IsSubsetOf`, `WhereAll` | :28-29 structural check first; :32-33 parent auth null gives true; :37-38 OwnerField; :41-42 child auth null; :46-48 parentReaders subset; :53-57 `Where`/`All` becomes `WhereAll`. |
| :60-61 `Readers` | `Readers` | Unchanged. |
| (deviations) | | None beyond the starting model's: LINQ `Where`/`All` modelled as the recursive `WhereAll`; `string.Equals` with a null `child.PopularitySignalColumn` is false; `OwnerField` init normalisation not modelled. |

Modelling caveats:

- The verifier prints one "unusual indentation" warning in the frozen P2 text (cosmetic, present at plan stage).

Obligations:

- `P1_NotOneToManyOrMissingChild_IsNeverEligible`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_NotOneToManyOrMissingChild_IsNeverEligible.r1 (line 96: if relation.Kind != OneToMany || child.None? then false                     // : -> if relation.Kind != OneToMany || child.None? then true                     // :2; failed: P1_NotOneToManyOrMissingChild_IsNeverEligible) ``
- `P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow.r1 (line 105: if !(parentReaders <= Readers(childRules)) then false                  // :47-48 -> if (parentReaders <= Readers(childRules)) then false                  // :47-48; failed: P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow) ``
- `P3_AnUnregisteredChildIsNeverEligible`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_AnUnregisteredChildIsNeverEligible.r1 (line 55: IsEligible(parent, relation, RegistryGet(registry, relation.RelatedTypeName)) -> IsEligible(parent, relation, Some(parent)); failed: P3_AnUnregisteredChildIsNeverEligible) ``

#### `ObjectSearchGrpcService.EligiblePopularitySignal`

Members:

- `EligiblePopularitySignal`: found, Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:1118-1129 (plan 1092-1103). Code changed: no.

Correspondence:

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| ObjectSearchGrpcService.cs:1118-1129 `EligiblePopularitySignal(SchemaDescriptor)` (T4 S6) | 128-138 | Instance fields `_popularitySignal.Signals` (:54) and the primary-constructor `registry` (:34) become explicit parameters `signals`, `registry`. |
| ObjectSearchGrpcService.cs:1120-1121 `Signals.FirstOrDefault(... ParentType ... OrdinalIgnoreCase)` (T4 S6) | 110-117 (`FirstSignalFor`), 131 | LINQ FirstOrDefault becomes a first-match recursion. OrdinalIgnoreCase equality becomes `EqIC` over the approved `OrdinalUpper`. |
| ObjectSearchGrpcService.cs:1122 `if (signal is null) return null;` (T4 S6) | 132 | Same early return. |
| ObjectSearchGrpcService.cs:1124-1125 `Relations.FirstOrDefault(... PropertyName ... OrdinalIgnoreCase)` (T4 S6) | 119-126 (`FirstRelationNamed`), 134 | Same as above. |
| ObjectSearchGrpcService.cs:1126-1128 `relation is not null && IsEligible(schema, relation, registry) ? signal : null` (T4 S6) | 135-137 | `&&` short-circuit is kept (`relation.Some? &&`). |
| PopularitySignalEligibility.cs:21-22 `IsEligible(parent, relation, SchemaRegistry)` (T4 S5) | 54-58 | Unchanged from the starting model. |
| PopularitySignalEligibility.cs:24-58 `IsEligible(parent, relation, SchemaDescriptor?)` (T4 S5) | 90-104, plus helpers 66-88 | Unchanged from the starting model. `.Where(...).All(...)` becomes `WhereAll`, and `HashSet.IsSubsetOf` becomes set `<=`. |
| PopularitySignalEligibility.cs:60-61 `Readers` (T4 S5) | 60-64 | Ordinal HashSet becomes a Dafny set (ordinal = value equality). |
| SchemaRegistry.cs:16-17 `Get` (pre-existing) | 41-48 | OrdinalIgnoreCase dictionary becomes the first EqIC TypeName (starting model's choice). The plan-vs-ref SchemaRegistry hunks, at :221 and later, do not touch `Get`. |
| PopularitySignalOptions.cs:10 `PopularitySignalEntry` (pre-existing) | 35-36 | Only the quote comment was updated. |

Modelling caveats:

- Instance fields `_popularitySignal.Signals` and the primary-constructor `registry` are explicit parameters, as at plan stage.

Obligations:

- `P1_OnlyAnEligibleSignalIsRankedOn`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P1_OnlyAnEligibleSignalIsRankedOn.1 (line 135: dropped the `&& IsEligibleIn(schema, relation.value, registry)` conjunct, leaving `if relation.Some?`; failed row: P1_OnlyAnEligibleSignalIsRankedOn (correctness)) ``

#### `ObjectSearchGrpcService.ReadableDecayField`

Members:

- `ReadableDecayField`: found, Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:1138-1144 (commit 94698da8, T4 S6). Code changed: no.

Correspondence:

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| ObjectSearchGrpcService.cs:1138 signature `string? ReadableDecayField(SchemaDescriptor schema, AuthorizationDecision decision)` (T4 S6) | 42 | Return `string?` becomes `Option<CsString>`. `decision` is modelled as `decision.AllowedFields` alone (`Option<seq<CsString>>`), the only field the body reads. The starting model already made this choice, so it is not a signature change. |
| :1140 `var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);` (T4 S6) | 44 | Approved axiom `ResolveDecayField(schema)`. The `logger` argument is dropped: it only logs and does not affect the result. The starting model carried this too. |
| :1141 `decayField is not null && decision.AllowedFields is not null && !decision.AllowedFields.Contains(decayField)` (T4 S6) | 45 | Same conjunct order. `Contains` becomes `ContainsIC` (lines 36-39, EqIC membership), because every non-null `AllowedFields` is built with `ToHashSet(StringComparer.OrdinalIgnoreCase)` (RowFieldAuthorizationEvaluator.cs:121). Every other `AuthorizationDecision` construction passes null for AllowedFields (RowFieldAuthorizationEvaluator.cs:12,15,30,33,55,63). OrdinalIgnoreCase equality is modelled as `EqIC` over the approved `OrdinalUpper` axiom. |
| :1142 `? null` | 46 | `None` |
| :1143 `: decayField;` | 47 | `decayField` |

Modelling caveats:

- `AllowedFields.Contains` is modelled as case-insensitive membership because every non-null `AllowedFields` is built with `OrdinalIgnoreCase` (RowFieldAuthorizationEvaluator.cs:121), as at plan stage.

Obligations:

- `P1_ADeniedDecayColumnGivesNoDecay`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ADeniedDecayColumnGivesNoDecay.r1 (line 45: if decayField.Some? && allowed.Some? && !ContainsIC(allowed.value, decayField.va -> if decayField.Some? && allowed.Some? && ContainsIC(allowed.value, decayField.val; failed: P1_ADeniedDecayColumnGivesNoDecay) ``
- `P2_AReadableOrUnrestrictedDecayFieldIsKept`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_AReadableOrUnrestrictedDecayFieldIsKept.r1 (line 45: if decayField.Some? && allowed.Some? && !ContainsIC(allowed.value, decayField.va -> if decayField.Some? && allowed.Some? && ContainsIC(allowed.value, decayField.val; failed: P2_AReadableOrUnrestrictedDecayFieldIsKept) ``

#### `ObjectSearchGrpcService.SimilarResponseAllowList`

Members:

- `WriteSimilarResponsesAsync` (columnLookup build through the per-row payload loop, before `MaskDisallowedFields`): found, Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:482-495 (T4 S6). Code changed: no.

Correspondence:

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.Api/NamingExtensions.cs:16-17 `ToCamelCase` (pre-existing helper) | 36-41 `ToCamelCase` | `char.ToLowerInvariant` is the approved axiom `CharToLowerInvariant`. |
| ObjectSearchGrpcService.cs:482 `new Dictionary<...>(StringComparer.Ordinal)` (T4 S6) | 56 `columnLookup0 := map[]` | Ordinal comparer = Dafny seq<char> equality. |
| ObjectSearchGrpcService.cs:483-484 `foreach (var col in schema.ScalarColumns) columnLookup[col.Name.ToCamelCase()] = col;` (T4 S6) | 43-51 `AddScalarColumns`, 57 | Indexer assignment = map update, later overwriting earlier, in code order. |
| ObjectSearchGrpcService.cs:485 `columnLookup[schema.KeyColumn.Name.ToCamelCase()] = schema.KeyColumn;` (T4 S6) | 58 | — |
| ObjectSearchGrpcService.cs:486 `columnLookup["key"] = schema.KeyColumn;` (T4 S6) | 59 | — |
| ObjectSearchGrpcService.cs:488 `foreach (var (payload, score) in rows)` (T4 S6) | 80-84 `ResponseFields` | Modelled per row; the properties are per-row, so the outer loop is one `ResponseFields` call per row. Score/traceId/stream write are not modelled (no property reads them). |
| ObjectSearchGrpcService.cs:490 `var protoStruct = new Struct();` (T4 S6) | 83 `map[]` | Struct.Fields modelled as map<CsString, PValue>. |
| ObjectSearchGrpcService.cs:491-495 `foreach (var kvp in payload) if (columnLookup.TryGetValue(kvp.Key, out var col)) protoStruct.Fields[col.Name] = ConvertPayloadValue(kvp.Value, col.SqlType);` (T4 S6) | 62-78 `FillRow` | Payload (IReadOnlyDictionary<string,string>) modelled as seq of pairs in enumeration order; this is more general (permits duplicate keys), so the proofs cover every real dictionary. `ConvertPayloadValue` is the approved axiom (PValue uninterpreted). |
| ObjectSearchGrpcService.cs:497 `MaskDisallowedFields` | — | Out of candidate range by brief. |

Modelling caveats:

- The payload dictionary is modelled as a sequence of pairs, which is more general (permits duplicate keys), so the proofs cover every real dictionary.

Obligations:

- `P1_ResponsesCarryOnlyDescriptorColumns`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ResponsesCarryOnlyDescriptorColumns.r1 (line 75: then fields[columnLookup[kvp.0].Name := ConvertPayloadValue(kvp.1, columnLookup[ -> then fields[kvp.0 := ConvertPayloadValue(kvp.1, columnLookup[kvp.0].SqlType)]; failed: FillRowKeys) ``
- `P2_ThePayloadKeyIsAnsweredAsTheKeyColumn`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P2_ThePayloadKeyIsAnsweredAsTheKeyColumn.1 (line 59: `columnLookup2["key" := schema.KeyColumn]` mutated to `"kex"`; failed row: P2_ThePayloadKeyIsAnsweredAsTheKeyColumn (correctness)) ``

#### `PopularitySignalValidator.SignalKeyCollision`

Members:

- `PopularitySignalValidator.ValidateAtStartup`: found, Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs:70-164. Code changed: no (the only plan-vs-built difference drops a duplicate argument from the not-eligible `LogWarning` at :146-154; the logger is a no-op in the model).

Correspondence:

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| PopularitySignalOptions.cs:10 PopularitySignalEntry (pre-existing) | 62-63 `Signal` | non-nullable strings become CsString |
| PopularitySignalOptions.cs:14 Section; EngagementStoreOptions.Section | 121-122 | constants |
| PopularitySignalOptions.cs:70-74 signature, HashSet, foreach (pre-existing; T4) | 164-174 `ValidateAtStartup`, `Loop` | `options` reduced to Signals plus the bool `RecencyBoost > 0`, since the double is only compared to 0 and only selects a log line; the logger is a no-op; the foreach becomes recursion on j |
| :76-81 duplicate ParentType (pre-existing) | 125-128 `SeenContains`, 177-181 | `HashSet.Add`==false becomes an EqIC element in `seen`; throw becomes `InvalidOperation` |
| :83-87 engagement disabled (pre-existing) | 184-187 | throw becomes `InvalidOperation` |
| :89-99 unregistered parent, warn + continue (pre-existing) | 45-54 `RegistryGet`, 189-191 | the dictionary becomes a first-EqIC-match seq; continue becomes a recursive call |
| :101-104 no vector/chunk fields (pre-existing) | 194-196 | approved axiom `HasVectorOrChunkFields` |
| :110-119 collision check (**T4 S6**, the logic under test) | 56-60 `ToCamelCase`, 140-152 `Collides`/`FirstColliding`, 198-204 | `ScalarColumns.Append(KeyColumn)` becomes `ScalarColumns + [KeyColumn]`; FirstOrDefault becomes an in-order first match; OrdinalIgnoreCase becomes `EqIC` (approved `OrdinalUpper`) |
| :121-125 relation lookup (pre-existing) | 154-160 `FirstRelation`, 206-209 | `?? throw` becomes `InvalidOperation` |
| :127-131 non-OneToMany (pre-existing) | 130-138 `KindName`, 210-213 | enum ToString |
| :133-136 child unregistered (pre-existing) | 215-218 | |
| :138-142 child ineligible (pre-existing) | 219-222 | approved axiom `IsEngagementEligible` |
| :144-154 eligibility warning (T4; log args differ from plan) | 224, 72-115 `IsEligible` | logs only, so no effect on outcome; the dropped duplicate log argument is not modelled (the logger is a no-op) |
| :156-162 recency warning (pre-existing) | 225 | logs only |
| :163 end of iteration | 226 | recursive call with j+1 |

Obligations:

- `P1_ASignalKeyCollidingWithAParentColumnIsRefused`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ASignalKeyCollidingWithAParentColumnIsRefused.r1 (line 143: EqIC(c.Name, countKey) || EqIC(c.Name, countKey + "Buckets") -> EqIC(c.Name, countKey); failed: P1_ASignalKeyCollidingWithAParentColumnIsRefused) ``

#### `EnrichmentTargetRules.Violation`

Members:

- `Violation`: found, Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs:25-153. Code changed: no (a comment above the column lookup grew from 3 to 5 lines; later lines moved down by 2).

Correspondence:

| Worktree file:line (EnrichmentTargetRules.cs) | Introduced by | Dafny lines | Note |
|---|---|---|---|
| 19 `SyntheticDocumentField = "Document"` | T5 S3 | 69 | const |
| 25-28 signature, `Count == 0` -> null | T5 S3 | 208-210 | `string?` -> `Option<seq<char>>` |
| 30 `Authorization?.OwnerField` | T5 S3 | 115-119, 212 | `OwnerFieldOf` |
| 35-40 sourceNames `Select.Concat(Where).ToHashSet(OrdinalIgnoreCase)` | T5 S3 | 71-97, 213 | HashSet kept as its source sequence; `Contains` -> `ContainsIC` (exists an EqIC element); `Count == 0` -> `\|seq\| == 0`. Both are independent of deduplication. `DocumentTemplate is null` is modelled as `!HasDocumentTemplate` |
| 42 `foreach target` with early return | T5 S3 | 149-155, 214-215 | `TargetLoop` returns at the first Some |
| 49-53 column lookup `FirstOrDefault ?? (key ? KeyColumn : null)` | T5 S3 | 99-113 | `FirstMatchingColumn`, `ColumnOf` |
| 54-58 not a column | T5 S3 | 125-127 | |
| 63-68 not TEXT / TEXT[] (OrdinalIgnoreCase) | T5 S3 | 128-130 | `EqIC` over the approved `OrdinalUpper` |
| 74-81 key / tenant / owner (`!IsNullOrEmpty(ownerField)`) | T5 S3 | 131-135 | `NullOrEmpty` |
| 83-89 target is a source | T5 S3 | 136-140 | |
| 91-95 Extracted with `IsNullOrWhiteSpace(Hint)` | T5 S3 | 141-143 | `NullOrWhiteSpace` over the approved `CharIsWhiteSpace` |
| 100-104 `sourceNames.Count == 0` | T5 S3 | 216-218 | |
| 106-108 targetNames `ToHashSet(OrdinalIgnoreCase)` | T5 S3 | 157-161, 220 | kept as a sequence; only `Contains` is used |
| 114 `Authorization?.FieldPermissions ?? []` | T5 S3 | 221 | |
| 118-125 writeRestricted `FirstOrDefault`, with its return | T5 S3 | 163-170, 222-226 | |
| 131-132 `foreach source in Where(ReadableRoles.Count>0 && sourceNames.Contains)` | T5 S3 | 194-205, 228 | the Where filter becomes a skip branch; order is kept |
| 134-149 inner `foreach target`, `!covered` -> return | T5 S3 | 182-193 | |
| 136-139 `covered = Any(... All(role => Contains(role, Ordinal)))` | T5 S3 | 172-180 | ordinal role comparison is `seq` membership |
| 152 `return null` | T5 S3 | 199 / 185 / 151 (None leaves) | |

Modelling caveats:

- The verifier prints two "unusual indentation" warnings in the frozen P6 and P7 text (cosmetic, present at plan stage).

Obligations:

- `P1_AdmittedTargetsAreTextOrTextArrayColumns`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AdmittedTargetsAreTextOrTextArrayColumns.r1 (line 128: else if !EqIC(column.value.SqlType, "TEXT") && !EqIC(column.value.SqlType, "TEXT -> else if !(!EqIC(column.value.SqlType, "TEXT") && !EqIC(column.value.SqlType, "TE; failed: P1_AdmittedTargetsAreTextOrTextArrayColumns) ``
- `P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner.r1 (line 131: else if EqIC(t.ColumnName, d.KeyColumn.Name) || -> else if; failed: P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner) ``
- `P3_AdmittedTargetsHaveASourceAndAreNotSources`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_AdmittedTargetsHaveASourceAndAreNotSources.r1 (line 136: else if ContainsIC(sourceNames, t.ColumnName) then -> else if !ContainsIC(sourceNames, t.ColumnName) then; failed: P3_AdmittedTargetsHaveASourceAndAreNotSources) ``
- `P4_AdmittedExtractedTargetsHaveAHint`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P4_AdmittedExtractedTargetsHaveAHint.r1 (line 141: else if t.Kind == Extracted && NullOrWhiteSpace(t.Hint) then -> else if t.Kind == Extracted && !NullOrWhiteSpace(t.Hint) then; failed: P4_AdmittedExtractedTargetsHaveAHint) ``
- `P5_NoAdmittedTargetIsWriteRestricted`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P5_NoAdmittedTargetIsWriteRestricted.r1 (line 223: if writeRestricted.Some? then -> if writeRestricted.None? then; failed: NoneFacts) ``
- `P6_EveryTargetReaderCanReadEverySource`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P6_EveryTargetReaderCanReadEverySource.r1 (failed: InnerLoopNone — helper(s) in the proof closure of P6_EveryTargetReaderCanReadEverySource) ``
- `P7_ADescriptorKeepingEveryRuleIsAdmitted`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P7_ADescriptorKeepingEveryRuleIsAdmitted.r1 (line 141: else if t.Kind == Extracted && NullOrWhiteSpace(t.Hint) then -> else if t.Kind != Extracted && NullOrWhiteSpace(t.Hint) then; failed: P7_ADescriptorKeepingEveryRuleIsAdmitted) ``

#### `SchemaRegistryRepository.OwnerQueries`

Members:

- `GetOwnersAsync`: found, Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs:40-43. Code changed: no.
- `UpsertIfOwnerAsync`: found, SchemaRegistryRepository.cs:47-57. Code changed: no.
- `ExistsAsync`: found, SchemaRegistryRepository.cs:63-70. Code changed: no.

Correspondence:

| Worktree range | Introduced | Dafny lines | Note |
|---|---|---|---|
| SchemaRegistryRepository.cs:40-43 GetOwnersAsync | T7 S3 | 43-55 (GetOwners), 40-41 (LowerMatches) | none; rows modelled as multiset; JsonOwner = ->> axiom |
| SchemaRegistryRepository.cs:47-57 UpsertIfOwnerAsync | T7 S3 | 57-72 | none; ExecuteAsync(...) > 0 modelled as 1>0 / 0>0 |
| SchemaRegistryRepository.cs:63-70 ExistsAsync | T7 S3 | 74-83 (Substr2, Exists) | none |
| NamingExtensions.cs:5-14 ToSnakeCase, SchemaBuilder.cs:31 ToTableName (existing, unchanged) | - | 87-101 | none |

Obligations:

- `P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds.1 (line 54 `if LowerMatches(k, t)` -> `if !LowerMatches(k, t)` in GetOwners; failed row: GetOwnersHasMatch (correctness), a helper in P1's call closure) ``
- `P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner.1 (line 66 `JsonOwner(tbl[t]) == owner` -> `!=` in UpsertIfOwner; failed row: P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner (correctness)) ``
- `P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable.1 (line 82 drops the conjunct `Substr2(k) == Substr2(t)` from Exists; failed row: P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable (correctness)) ``
- `P4_AStoredFirstLetterVariantCountsAsTheSameType`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P4_AStoredFirstLetterVariantCountsAsTheSameType.1 (line 82 `Substr2(k) == Substr2(t)` -> `!=` in Exists; failed row: P4_AStoredFirstLetterVariantCountsAsTheSameType (correctness)) ``

#### `PrivilegedRequestRepository.Transitions`

Members:

- `InsertAsync` (:16-30), `ExpireIfPendingAsync` (:43-49), `ListClaimableAsync` (:51-61), `TryClaimAsync` (:64-71), `FailAbandonedAsync` (:86-94), `DeleteFinishedOlderThanAsync` (:98-106): all found in Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs. Code changed: no, for every member.

Correspondence:

| Worktree lines | Member | Dafny | Note |
|---|---|---|---|
| 16-30 | InsertAsync | Insert | found; unchanged. now() as one instant; null expiresAfter -> NULL. Deviation (as in start model): TotalSeconds double modelled as int |
| 43-49 | ExpireIfPendingAsync | ExpireIfPending | found; unchanged |
| 51-61 | ListClaimableAsync | ListClaimable / FilterClaimable / axiom OrderByRequestedAtLimit | found; unchanged (WHERE 56-57, ORDER/LIMIT 58-59) |
| 64-71 | TryClaimAsync | TryClaim / ClaimableWhere | found; unchanged |
| 86-94 | FailAbandonedAsync | FailAbandoned | found; unchanged; per-row model, NULL ClaimedAt selects nothing; double AgeSeconds as int |
| 98-106 | DeleteFinishedOlderThanAsync | DeletesRow | found; unchanged; NULL ExpiresAt selects nothing |

Modelling caveats:

- `TotalSeconds`/`AgeSeconds` doubles are modelled as ints, and `now()` as one instant, as at plan stage.

Obligations:

- `P1_AClaimTakesOnlyAPendingUnexpiredRequest`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AClaimTakesOnlyAPendingUnexpiredRequest.r1 (line 83: if r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value > now) -> if r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value >= now); failed: P1_AClaimTakesOnlyAPendingUnexpiredRequest) ``
- `P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires.r1 (line 54: if r.Status == Pending then (true, r.(Status := Expired)) -> if r.Status != Pending then (true, r.(Status := Expired)); failed: P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires) ``
- `P3_OnlyClaimableRequestsAreListed`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_OnlyClaimableRequestsAreListed.r1 (line 62: r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value > now) -> (r.ExpiresAt.None? || r.ExpiresAt.value > now); failed: P3_OnlyClaimableRequestsAreListed) ``
- `P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed.r1 (line 93: if r.Status == Running && r.ClaimedAt.Some? && r.ClaimedAt.value < now - age -> if r.Status == Running && r.ClaimedAt.Some? && r.ClaimedAt.value <= now - age; failed: P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed) ``
- `P5_CleanupDeletesOnlyFinishedRequests`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P5_CleanupDeletesOnlyFinishedRequests.r1 (line 107: (r.Status == Pending && r.ExpiresAt.Some? && r.ExpiresAt.value < now)) -> (r.Status == Pending && r.ExpiresAt.Some? && r.ExpiresAt.value >= now)); failed: P5_CleanupDeletesOnlyFinishedRequests) ``
- `P6_ARequestExpiresAfterItsWaitOrNever`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P6_ARequestExpiresAfterItsWaitOrNever.r1 (line 46: if expiresAfter.Some? then Some(now + expiresAfter.value) else None, -> if expiresAfter.Some? then None else None,; failed: P6_ARequestExpiresAfterItsWaitOrNever) ``

#### `PostgresPrivilegedRequestLocks.Keys`

Members:

- `RegistrationKey`: found, Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs:33. Code changed: no.
- `TypeKeyBase`: found, PostgresPrivilegedRequestLocks.cs:34. Code changed: no.
- key expression of `AcquireRegistrationAsync`: found, PostgresPrivilegedRequestLocks.cs:40-41. Code changed: no (only the wrapping SQL function changed, `pg_advisory_lock` → `pg_try_advisory_lock` in a poll loop, which does not change the key).
- key expression of `AcquireTypeAsync`: found, PostgresPrivilegedRequestLocks.cs:45-46. Code changed: no (same note).

Correspondence:

| Worktree file:line | Task/step | Dafny lines | Note |
|---|---|---|---|
| Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs:33 `internal const long RegistrationKey = 1L << 41;` | T7 S3 | 22-23 | `1L << 41` = 0x200_0000_0000, fits in long; modelled as an exact `int` constant (no wrap possible). Unchanged from the plan. |
| PostgresPrivilegedRequestLocks.cs:34 `internal const long TypeKeyBase = 1L << 40;` | T7 S3 | 25-26 | `1L << 40` = 0x100_0000_0000; exact `int`. Unchanged. |
| PostgresPrivilegedRequestLocks.cs:40-41 `AcquireRegistrationAsync` -> `AcquireAsync("@Key", new { Key = RegistrationKey }, ct)` | T7 S3 | 28-31 | Key expression unchanged. As built, AcquireAsync (51-80) sends it as `SELECT pg_try_advisory_lock(@Key)` (line 62) in a poll loop instead of the plan's blocking `pg_advisory_lock`, and the Lease unlocks with the same keySql/param (line 103). The key value is identical either way; only the comment changed. Dapper binds the `long` as bigint. |
| PostgresPrivilegedRequestLocks.cs:45-46 `AcquireTypeAsync` -> `AcquireAsync("@Base + hashtext(lower(@TypeName))", new { Base = TypeKeyBase, TypeName = typeName }, ct)` | T7 S3 | 33-41 | Key expression unchanged. `@Base` bigint + `hashtext(...)` int4 is evaluated by Postgres in bigint, exactly (Postgres raises on overflow, which P1's third clause excludes); modelled as exact `int` addition. `hashtext` and `lower` are the approved axioms `HashText` and `PgLower` (lines 15, 18). `typeName` is a non-nullable `string` -> `CsString`. Same try-lock / unlock change as above, comment only. |
| (CsString / Int32 vocabulary) | — | 8, 10 | `string` length is an `int`; `hashtext` returns int4. Unchanged. |

Modelling caveats:

- The rest of the built locks file (poll loop, own `NpgsqlDataSource`, discard on failure, disposal) is outside this candidate; it is on the uncovered list.

Obligations:

- `P1_EveryLockKeyLiesOutsideTheInt4Range`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_EveryLockKeyLiesOutsideTheInt4Range.r1 (line 26: const TypeKeyBase: int := 0x100_0000_0000 -> const TypeKeyBase: int := 0x100_0000; failed: P1_EveryLockKeyLiesOutsideTheInt4Range) ``
- `P2_NoTypeLockIsTheRegistrationLock`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_NoTypeLockIsTheRegistrationLock.r1 (line 26: const TypeKeyBase: int := 0x100_0000_0000 -> const TypeKeyBase: int := 0x200_0000_0000; failed: P2_NoTypeLockIsTheRegistrationLock) ``
- `P3_ATypeLockFoldsTheNamesCase`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_ATypeLockFoldsTheNamesCase.r1 (line 40: TypeKeyBase + HashText(PgLower(typeName)) as int -> TypeKeyBase + HashText(typeName) as int; failed: P3_ATypeLockFoldsTheNamesCase) ``

#### `SchemaRegistrationApplier.ExecuteAsync`

Members:

- `ExecuteAsync`: found, Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs:20-61. Code changed: no (only the call sites at :31 and :53 now name `SchemaRegistrationRefusals.OwnedByAnotherTenant`, same callee semantics).
- `RequireAdoptableAsync`: found, SchemaRegistrationApplier.cs:66-79. Code changed: no.
- `NotAdoptable`: found, SchemaRegistrationApplier.cs:81-84. Code changed: no.
- `OwnedByAnotherTenant`: found (moved), now `SchemaRegistrationRefusals.OwnedByAnotherTenant` in Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs:21-23. Code changed: no (body and signature identical; only its declaring class and file changed).

Correspondence:

| Worktree file:line | Task/step | Dafny lines | Note |
|---|---|---|---|
| Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs:19-24 (`SchemaRegistrationRefusals.OwnedByAnotherTenant`, :21-23) | T7 S3 (plan put it in SchemaRegistrationApplier.cs; as built it moved here) | 49-54 | **Deviation, location only.** As built, the member moved from `SchemaRegistrationApplier` (`internal static`) to the new `internal static class SchemaRegistrationRefusals` in PrivilegedRequestContracts.cs. Its signature (`string -> RpcException`) and message are identical, so the Dafny text is unchanged. |
| SchemaRegistrationApplier.cs:20-61 `ExecuteAsync` | T7 S3 | 131-137 | `ct` is not modelled; nothing in the candidate reads it. |
| SchemaRegistrationApplier.cs:25-54 first `foreach` | T7 S3 | 110-121 (`ExecuteDescriptors`) | The loop is modelled as recursion that stops at the first exception, which keeps the effects of earlier iterations. |
| SchemaRegistrationApplier.cs:27-53 loop body | T7 S3 | 86-108 (`ApplyDescriptor`) | Done means the iteration completed. Any other outcome is the exception thrown. |
| :29-31 `GetOwnersAsync` + `Any(owner => owner != OwnerTenantId)` → throw `OwnedByAnotherTenant` | T7 S3 | 90-92, 64-71 (`AnyOwnerDiffers`) | The `string?` `!=` is ordinal and null-aware, modelled as `Option` inequality. `GetOwnersAsync` is the oracle `db.owners`. |
| :37-38 `!ExistsAsync` → `RequireAdoptableAsync` | T7 S3 | 94-96 | `ExistsAsync` is the oracle `db.exists_`. |
| :41-48 `ApplySchemaAsync(ToTableSchema(d), Throw)`; `SchemaDriftException` → `FailedPrecondition(ex.Message)` | T7 S3 | 98-101 | The oracle `db.ddl`, keyed by table name, returns Applied, Drift(m) or DdlFault. Any other exception, including one from `ToTableSchema`, propagates as `Fault`. These are the starting model's choices. |
| :51 `JsonSerializer.Serialize` | T7 S3 | 104 | Approved axiom `SerializeDescriptor`. Its value is unused by the model because the `upsert` oracle is keyed by (typeName, owner), as in the starting model. |
| :52-53 `UpsertIfOwnerAsync` false → throw `OwnedByAnotherTenant` | T7 S3 | 105-107 | The `Ddl` effect is kept, because the DDL already ran. |
| :59-60 second `foreach` `EnqueueTypeAsync` | T7 S3 | 123-129 (`EnqueueAll`), 136 | |
| :66-79 `RequireAdoptableAsync` | T7 S3 | 73-84 | None means it returned normally. Some(o) means it threw o. The early returns and branch order are kept: null first, then not an ordinary table, then no tenant column, then owned with foreign rows. `DescribeAsync` and `HasRowsOfAnotherTenantAsync` are oracles. |
| :81-84 `NotAdoptable` | T7 S3 | 56-62 | The message is identical, including the `$"has no {TenantColumn} column"` reason at :75 (Dafny line 79). |

Obligations:

- `P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything.r1 (line 92: if AnyOwnerDiffers(storedOwners, descriptor.OwnerTenantId) then (OwnedByAnotherT -> if !AnyOwnerDiffers(storedOwners, descriptor.OwnerTenantId) then (OwnedByAnother; failed: P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything) ``
- `P2_AnUnregisteredTypeNeverAdoptsAForeignTable`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_AnUnregisteredTypeNeverAdoptsAForeignTable.r1 (line 95: var adopt := if !db.exists_(descriptor.TypeName) then RequireAdoptableAsync(desc -> var adopt := if db.exists_(descriptor.TypeName) then RequireAdoptableAsync(descr; failed: P2_AnUnregisteredTypeNeverAdoptsAForeignTable) ``
- `P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable.r1 (line 78: else if !existing.value.IsOrdinaryTable then Some(NotAdoptable(descriptor, "is n -> else if existing.value.IsOrdinaryTable then Some(NotAdoptable(descriptor, "is no; failed: P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable) ``
- `P4_DriftIsFailedPreconditionAndNothingIsWritten`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P4_DriftIsFailedPreconditionAndNothingIsWritten.r1 (line 100: case Drift(m) => (Rpc(FailedPrecondition, m), []) -> case Drift(m) => (Fault, []); failed: P4_DriftIsFailedPreconditionAndNothingIsWritten) ``
- `P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied.r1 (line 105: if !db.upsert(descriptor.TypeName, descriptor.OwnerTenantId) -> if db.upsert(descriptor.TypeName, descriptor.OwnerTenantId); failed: P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied) ``
- `P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders.r1 (line 136: else (Done, r.1 + EnqueueAll(changed)) -> else (Done, EnqueueAll(changed) + r.1); failed: P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders) ``

#### `QueuedSchemaRegistrationExecutor.ExecuteAsync`

Members:

- `ExecuteAsync`: found, Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs:22-70 (T7 S3). Code changed: no.

Correspondence:

| Worktree file:line (QueuedSchemaRegistrationExecutor.cs, T7 S3) | Dafny | Note |
|---|---|---|
| :27-29 | `Execute` payload | As in the model comments. |
| :33-37 | `Submit` | |
| :39-40 | `WaitClaim` | |
| :42-49 | `Expire` / `Reread` | |
| :51-53 | `AfterClaim` `WaitRun` | |
| :55-69 | final match | No deviations. |

Obligations:

- `P1_SuccessIsAnsweredOnlyAfterTheApiReloads`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_SuccessIsAnsweredOnlyAfterTheApiReloads.r1 (line 113: (if a.reloadOk then Done else Fault, calls + [Reload]) -> (if !a.reloadOk then Done else Fault, calls + [Reload]); failed: P1_SuccessIsAnsweredOnlyAfterTheApiReloads) ``
- `P2_ARequestNeverClaimedIsExpiredAndUnavailable`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_ARequestNeverClaimedIsExpiredAndUnavailable.r1 (line 89: if a.expired then -> if !a.expired then; failed: P2_ARequestNeverClaimedIsExpiredAndUnavailable) ``
- `P3_AClaimedRequestStillRunningIsDeadlineExceeded`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_AClaimedRequestStillRunningIsDeadlineExceeded.r1 (line 116: (Rpc(DeadlineExceeded, -> (Rpc(Unavailable,; failed: P3_AClaimedRequestStillRunningIsDeadlineExceeded) ``
- `P4_AFailedRequestAnswersWithItsStoredStatusAndMessage`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P4_AFailedRequestAnswersWithItsStoredStatusAndMessage.r1 (line 127: (Rpc(result.Code, if result.Message.Some? then result.Message.value else ""), ca -> (Rpc(Unavailable, if result.Message.Some? then result.Message.value else ""), ca; failed: P4_AFailedRequestAnswersWithItsStoredStatusAndMessage) ``
- `P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded.r1 (line 81: var calls := [Submit(Some(waits.RegisterClaim))]; -> var calls := [Submit(None)];; failed: P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded) ``

#### `PrivilegedRequestExecutor.RunAsync`

Members:

- `RunAsync` (:31-60), `RegisterSchemaAsync` (:64-71), `ReconcileTypeAsync` (:77-91), `ReplayDlqAsync` (:95-106), `Deserialize` (:108-110): all found in Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs. Code changed: no, for every member.

Correspondence:

| worktree lines | member | Dafny | notes |
|---|---|---|---|
| 31-60 | RunAsync | RunAsync, TryBlock (Dafny lines ~75-120) | catch order and the cancellation filter kept; logging omitted (no effect on result) |
| 64-71 | RegisterSchemaAsync | RegisterSchemaAsync | as plan |
| 77-91 | ReconcileTypeAsync | ReconcileTypeAsync | as plan |
| 95-106 | ReplayDlqAsync | ReplayDlqAsync | guidText field is a starting-model deviation (Guid formatting is external) |
| 108-110 | Deserialize | folded into each arm's match on o.payload (null/throw/wrong kind -> OtherEx) | starting-model choice |

Modelling caveats:

- Logging is omitted (no effect on the result); `Deserialize` is folded into each arm's match on the payload, as at plan stage.

Obligations:

- `P1_ARefusalIsRecordedWithItsStatusAndMessage`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ARefusalIsRecordedWithItsStatusAndMessage.r1 (line 82: case Raised(RpcEx(code, detail)) => (PResult(code, Some(detail), None), effects) -> case Raised(RpcEx(code, detail)) => (PResult(code, None, None), effects); failed: P1_ARefusalIsRecordedWithItsStatusAndMessage) ``
- `P2_AnUnexpectedFailureIsInternalWithAGenericMessage`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_AnUnexpectedFailureIsInternalWithAGenericMessage.r1 (line 92: (PResult(Internal, Some("The request failed in the worker; the worker log has th -> (PResult(Unavailable, Some("The request failed in the worker; the worker log has; failed: P2_AnUnexpectedFailureIsInternalWithAGenericMessage) ``
- `P3_StoppingTheWorkerIsUnavailableNeverAPartialResult`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_StoppingTheWorkerIsUnavailableNeverAPartialResult.r1 (line 85: if o.cancelled then -> if !o.cancelled then; failed: P3_StoppingTheWorkerIsUnavailableNeverAPartialResult) ``
- `P4_EachKindTakesOnlyItsOwnLock`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P4_EachKindTakesOnlyItsOwnLock.r1 (line 126: case Did(_) => (Returned(Ok(None)), [RegistrationLocked, Applied])) -> case Did(_) => (Returned(Ok(None)), [Applied, RegistrationLocked])); failed: P4_EachKindTakesOnlyItsOwnLock) ``
- `P5_TheRegistryIsReloadedBeforeAnything`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P5_TheRegistryIsReloadedBeforeAnything.r1 (line 109: (arm.0, [Loaded] + arm.1) -> (arm.0, arm.1 + [Loaded]); failed: P5_TheRegistryIsReloadedBeforeAnything) ``
- `P6_AReplayRechecksTheRowThenProducesBeforeMarking`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P6_AReplayRechecksTheRowThenProducesBeforeMarking.r1 (line 179: case Did(_) => (Returned(Ok(None)), [Produced, Marked])) -> case Did(_) => (Returned(Ok(None)), [Marked, Produced])); failed: P6_AReplayRechecksTheRowThenProducesBeforeMarking) ``
- `P7_AReconcileRecordsItsCountOrNotFound`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P7_AReconcileRecordsItsCountOrNotFound.r1 (line 153: case Some(n) => (Returned(Ok(Some(n))), eff)) -> case Some(n) => (Returned(Ok(None)), eff)); failed: P7_AReconcileRecordsItsCountOrNotFound) ``

#### `PrivilegedRequestWorker.PollOnceAsync`

Members:

- `PollOnceAsync`: found, Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs:54-74. Code changed: no.
- `RunClaimedAsync`: found, PrivilegedRequestWorker.cs:78-94. Code changed: no.

Correspondence:

| Worktree file:line (PrivilegedRequestWorker.cs, T7 S3) | Dafny | Note |
|---|---|---|
| :22, :23, :29 `CleanupInterval` / `FinishedRetention` / `AbandonedAfter` | consts | Unchanged. |
| :30-31 `Abandoned` | const `Abandoned` (Unavailable, message, Count None) | |
| :78-94 `RunClaimedAsync` | function `RunClaimed` | Ran, then Completed(status, ToJson). Deviation: `executor.RunAsync` is a total oracle `results` (it catches all exceptions); a `CompleteAsync` exception is caught and logged, so its effect is recorded regardless. |
| :67-73 claim loop | `ClaimLoop` | Continues on a lost claim. Deviation (carried from the starting model): the `Task.Run` fire-and-forget is modelled as sequential effects; `ListClaimableAsync`/`TryClaimAsync` exceptions are not modelled. |
| :54-65 cleanup | `PollOnce` | The in-flight pruning at :56-58 has no effect on the properties and is omitted. |
| :60, :64 `DateTimeOffset.UtcNow` (two reads) | one instant `now` | Deviation (carried from the starting model): see the caveat below. |

Modelling caveats:

- The code reads `DateTimeOffset.UtcNow` twice (:60 and :64; `_lastCleanup` takes the later reading after two awaits), while the model uses one instant `now`. P3's `next == now` is proved for the model; in the real code `next` is the second reading, `>= now`, which only lengthens the interval P3 bounds. Carried from the plan-stage model; the frozen signature cannot take a second clock.

Obligations:

- `P1_OnlyClaimedRequestsRun`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_OnlyClaimedRequestsRun.r1 (line 80: if !claims(request.Id) then -> if claims(request.Id) then; failed: ClaimLoop_RanOnlyClaimed) ``
- `P2_EachClaimedRunIsRecordedSucceededOnlyWhenOk`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_EachClaimedRunIsRecordedSucceededOnlyWhenOk.r1 (line 70: [Completed(request.Id, if result.Code == OK then Succeeded else Failed, ResultTo -> [Completed(request.Id, if result.Code != OK then Succeeded else Failed, ResultTo; failed: ClaimLoop_CompletesClaimed) ``
- `P3_CleanupRunsAtMostHourly`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_CleanupRunsAtMostHourly.r1 (line 92: if now - lastCleanup >= CleanupInterval then -> if now - lastCleanup > CleanupInterval then; failed: P3_CleanupRunsAtMostHourly) ``

#### `Program.PrivilegedAdminRoutes`

Members:

- `/admin/reconcile/{typeName}` handler: found, Iverson.Server/Iverson.Api/Program.cs:664-672, plus the shared helper `RunPrivilegedAdminRequestAsync` at Program.cs:943-975. Code changed: **yes**, a structural refactor (commit a6f972cc, "give the two privileged admin routes one helper"); behaviour unchanged.
- `/admin/dlq/{id}/replay` handler: found, Program.cs:738-755, plus the same helper. Code changed: **yes**, the same refactor; behaviour unchanged.

Correspondence:

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Program.cs:943-951 `RunPrivilegedAdminRequestAsync` signature (T8 S3; extracted in a6f972cc) | 62-66 | New helper. `requestedBy`, `writeAuditEntry` and `ct` are not modelled, because none of them affects the answer or the recorded client calls; the starting model already dropped `requestedBy`, the audit call and RequestAborted. `wait` (a TimeSpan) is modelled as int ticks, as before. `requestId` and `waited` are inputs, as before. `ok` becomes `PResult -> Http`. |
| Program.cs:953 `SubmitAsync(kind, payloadJson, requestedBy, expiresAfter: null)` | 68 (`Submitted(kind, None)`) | |
| Program.cs:954-958 `WaitWhileAsync(requestId, pending-or-running, wait, ct)` | 68 (`Waited(wait)`) | The predicate and the cancellation token are not modelled (same as before). |
| Program.cs:959 `PrivilegedRequestResult.From(request)` | 69 | Supplied as the input `waited` (same as before). |
| Program.cs:961-967 `result is null` → audit, then 202 `{requestId}` | 70-71 | The audit call is not modelled. |
| Program.cs:968-969 NotFound → 404 `{error = Message}` | 72-73 | |
| Program.cs:970-971 `!= OK` → 500 `{error = Message}` | 74-75 | |
| Program.cs:972-973 audit, then `return ok(result)` | 76-77 | The audit call is not modelled. |
| Program.cs:664-672 reconcile handler: `return await RunPrivilegedAdminRequestAsync(privileged, ReconcileType, Serialize(ReconcileTypePayload(typeName)), tenant_id, Waits.Reconcile, audit, result => Ok(new {reconciledCount = result.Count, typeName}), RequestAborted)` (T8 S3; a6f972cc) | 80-91 | The payload is the approved axiom `SerializeReconcilePayload`. The lambda becomes `(result: PResult) => Ok200(ReconciledBody(result.Count, typeName))`; the parameter type is written out because Dafny needs it to resolve the lambda. |
| Program.cs:738 `dlq.GetUnreplayedSummaryAsync(id)` (T8 S3) | 98 | Supplied as the input `summary` (same as before). |
| Program.cs:739 `row is null` → 404 `"No unreplayed DLQ row with id '{id}'"` | 99-100 | `{id}` is the unspecified `GuidToString`, as before. |
| Program.cs:741-742 tenant mismatch, not (null-tenant and operator) → Forbid | 101-102 | |
| Program.cs:747-755 replay handler: `return await RunPrivilegedAdminRequestAsync(privileged, ReplayDlq, Serialize(ReplayDlqPayload(id)), actingTenantId, Waits.Replay, audit, _ => Ok(new {replayed = true, id, topic = row.SourceTopic}), RequestAborted)` (T8 S3; a6f972cc) | 103-110 | The payload is the approved axiom `SerializeReplayPayload`. The constant `replayed = true` field of the body is not modelled, as in the starting model. |

Modelling caveats:

- `requestedBy`, the audit writer and the cancellation token are not modelled (none affects the answer or the recorded client calls), as at plan stage. The checks at Program.cs:722-735 precede the candidate range and are not modelled.

Obligations:

- `P1_AnUnfinishedRequestIsAnswered202WithItsId`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AnUnfinishedRequestIsAnswered202WithItsId.r1 (line 71: (Accepted202(requestId), calls) -> (Error500(None), calls); failed: P1_AnUnfinishedRequestIsAnswered202WithItsId) ``
- `P2_AWorkerNotFoundIs404`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P2_AWorkerNotFoundIs404.1 (line 73: NotFound arm returns Error500(result.value.Message) instead of NotFound404; failed row: P2_AWorkerNotFoundIs404 (correctness)) ``
- `P3_AnyOtherWorkerFailureIs500WithItsMessage`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P3_AnyOtherWorkerFailureIs500WithItsMessage.1 (line 75: non-OK arm returns NotFound404(result.value.Message) instead of Error500; failed row: P3_AnyOtherWorkerFailureIs500WithItsMessage (correctness)) ``
- `P4_AFinishedRequestIs200`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P4_AFinishedRequestIs200.1 (line 77: success arm returns Accepted202(requestId) instead of ok(result.value); failed row: P4_AFinishedRequestIs200 (correctness)) ``
- `P5_TheReplayChecksRunBeforeAnythingIsQueued`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P5_TheReplayChecksRunBeforeAnythingIsQueued.r1 (line 101: else if row.value.TenantId != actingTenant && !(row.value.TenantId.None? && isOp -> else if row.value.TenantId == actingTenant && !(row.value.TenantId.None? && isOp; failed: P5_TheReplayChecksRunBeforeAnythingIsQueued) ``
- `P6_AdminRequestsNeverExpire`: **proved**. G5: `` fresh mutant (G5 agent; controller re-ran Canonicalize and OwnerQueries): killed by P6_AdminRequestsNeverExpire.1 (line 68: calls[0] = Submitted(kind, Some(0)) instead of Submitted(kind, None); failed row: P6_AdminRequestsNeverExpire (correctness)) ``

#### `Program.Health`

Members:

- `/health` handler body: found, Iverson.Server/Iverson.Api/Program.cs:581-639 (body 591-638). Code changed: no.

Correspondence:

| Worktree file:line | Introduced by | Dafny lines | Note |
|---|---|---|---|
| Iverson.StarRocks/EngagementHealthStatus.cs:3-14 | existing | 9 | enum -> datatype, same order |
| (Task.IsCompletedSuccessfully / Result) | -- | 12 | Probe<T>: Answered(value) / Faulted |
| Iverson.Api/ReadinessPolicy.cs:5 | existing | 17 | record -> datatype |
| Iverson.Api/ReadinessPolicy.cs:14-32 | existing | 20-30 | same branch order; the `engagementEnabled = true` default is not used (caller passes it) |
| Program.cs:581-589 (params; 587-588 registry/storeRoles) | T9 S5 (587-588) | 51 | DI services become model inputs: pg/grants/role probe outcomes, registry.HasLoaded, sr status, qdrant/kafka bools, Enabled, cached entry |
| Program.cs:591-592 | existing | 53-54 | cache hit returns the cached IResult -> `cached.value` |
| Program.cs:599 | existing | 33-36, 56 | PgCheck: IsCompletedSuccessfully && Result == 1 |
| Program.cs:607 | T9 S5 | 39-42, 57 | GrantsCheck: IsCompletedSuccessfully && Result |
| Program.cs:608 | T9 S5 | 45-48, 58 | RoleCheck: IsCompletedSuccessfully |
| Program.cs:609-611, 613, 615 | existing (613 edited T9 S5) | 59, params sr/qdrant/kafka | srTask/vectorTask/kafkaTask modelled as completed values (starting-model choice). Checked as built: Qdrant PingAsync (IntelligenceCollectionManager.cs:46-) and Kafka PingAsync (KafkaBrokerHealthCheck.cs:17-) catch all exceptions and return false; pg/grants/role are ContinueWith-wrapped so never fault; Task.WhenAll throws only if sr.CheckHealthAsync faults, which is not modelled (inherited deviation) |
| Program.cs:616 | existing | param engagementEnabled | |
| Program.cs:617 | T9 S5 | param registryLoaded | registry.HasLoaded (SchemaRegistry.cs:27) |
| Program.cs:618-627 | T9 S5 (620-626) | -- | response body `checks` not modelled; only status code and FullyHealthy matter to the properties |
| Program.cs:629-631 | T9 S5 | 60-62 | same conjunction order |
| Program.cs:633-635 | existing | 63-66 | 200 + FullyHealthy (body status healthy/degraded) vs 503 |
| Program.cs:637-638 | existing | 67 | cache.Set side effect not modelled; returns result |

Modelling caveats:

- The sr/qdrant/kafka probes are modelled as completed values. Qdrant and Kafka pings catch all exceptions and pg/grants/role are `ContinueWith`-wrapped, so they never fault; `Task.WhenAll` throws only if `sr.CheckHealthAsync` faults, and that fault path is not modelled (inherited from the plan-stage model).

Obligations:

- `P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry.r1 (line 61: pgResult && grantsResult && roleResult && registryLoaded, -> pgResult && grantsResult && roleResult,; failed: P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry) ``
- `P2_StarRocksAuthPendingKeepsTheApiReady`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_StarRocksAuthPendingKeepsTheApiReady.r1 (line 27: && starRocksStatus != Unhealthy; -> && starRocksStatus == Healthy;; failed: P2_StarRocksAuthPendingKeepsTheApiReady) ``

#### `ObjectRetrievalGrpcService.GetManyTenantGuard`

Members:

- `GetMany` guard through `FetchManyByKeysAsync`: found, Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:115-121 (T9 S5). Code changed: no.

Correspondence:

| worktree lines | Dafny | note |
|---|---|---|
| 115 `if (decision.TenantColumn is null)` | GetManyRead, `if dec.TenantColumn.None?` | none |
| 116 throw RpcException(Internal, "The type declares no tenant boundary.") | `(Rpc(Internal, "..."), [])` | exception as outcome |
| 118-121 FetchManyByKeysAsync(..., EntityAccess.ForTenant(decision.TenantValue)) | `(Proceeded, [ForTenant(dec.TenantValue)])` | none |

Obligations:

- `P1_ATenantlessDecisionIsRefusedBeforeTheRead`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ATenantlessDecisionIsRefusedBeforeTheRead.r1 (line 32: if dec.TenantColumn.None? then -> if dec.TenantColumn.Some? then; failed: P1_ATenantlessDecisionIsRefusedBeforeTheRead) ``
- `P2_TheReadNeverRunsAcrossTenants`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_TheReadNeverRunsAcrossTenants.r1 (line 37: (Proceeded, [ForTenant(dec.TenantValue)]) -> (Proceeded, [CrossTenantMaintenance]); failed: P2_TheReadNeverRunsAcrossTenants) ``

#### `ObjectMappingGrpcService.DeleteTenantGuard`

Members:

- `Delete` guard and `ExecuteInTransactionAsync` block: found, Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:348-365 (T9 S5). Code changed: no.

Correspondence:

| worktree lines | Dafny | note |
|---|---|---|
| 348-349 guard `if (decision.TenantColumn is null) throw RpcException(Internal, "The type declares no tenant boundary.")` | DeleteWrite first branch, `if dec.TenantColumn.None? then (Rpc(Internal, ...), [])` | none |
| 351-365 ExecuteInTransactionAsync { DeleteAsync(..., EntityAccess.ForTenant(decision.TenantValue)); EnqueueDeleteOutboxRowAsync(...) } | else branch `(Proceeded, [TxBegun, EntityDelete(ForTenant(dec.TenantValue)), OutboxDeleteRow])` | no deviation; the built code is identical to the plan's code for this range |

Obligations:

- `P1_ATenantlessDecisionIsRefusedBeforeTheTransaction`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ATenantlessDecisionIsRefusedBeforeTheTransaction.r1 (line 38: if dec.TenantColumn.None? then -> if dec.TenantColumn.Some? then; failed: P1_ATenantlessDecisionIsRefusedBeforeTheTransaction) ``
- `P2_TheDeleteNeverRunsAcrossTenants`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_TheDeleteNeverRunsAcrossTenants.r1 (line 45: (Proceeded, [TxBegun, EntityDelete(ForTenant(dec.TenantValue)), OutboxDeleteRow] -> (Proceeded, [TxBegun, EntityDelete(CrossTenantMaintenance), OutboxDeleteRow]); failed: P2_TheDeleteNeverRunsAcrossTenants) ``

#### `OutboxWriter.UpsertAndEnqueueOutboxAsync`

Members:

- `UpsertAndEnqueueOutboxAsync`: found, Iverson.Server/Iverson.Sql/OutboxWriter.cs:18-72. Code changed: no (the built diff touches only `WithTenantColumn`, which sits behind the approved axiom).

Correspondence:

| Worktree lines | Introduced by | Dafny lines | Note |
|---|---|---|---|
| 18-19 signature `(TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null)` | pre-existing (3159f0d6) | 42 `UpsertAndEnqueue(tenantId: Option<CsString>, payload: CsString, tenantColumn: Option<CsString>)` | `string? tenantId` -> Option. `schema` contributes only `schema.TenantColumn` (string?) -> `tenantColumn`; `typeName`, `key` feed only the outbox parameter object, not read by any property, so omitted. Default value not modelled (plain parameter). |
| 21-24 comment + `ArgumentNullException.ThrowIfNull(tenantId);` | T9 S5 (65fad16e) | 44-45 | Throw -> `ArgumentNull` outcome. |
| 26-49 table/keyColumn/allCols/updateSet/upsertSql/outboxSql/outboxRowId | pre-existing (3159f0d6, fa1e4c61, 8bf75209) | 48-49 (comment only) | Pure string/Guid construction, no throw path that a property reads; abstracted. Deviation: PostgresIdentifier.Quote could in principle throw on an invalid name; not modelled (no property concerns it, and it would be an outcome after the null check, not affecting P1; P2 assumes the transaction is reached). |
| 51-55 `payloadJson = WithTenantColumn(payloadJson, schema.TenantColumn, tenantId);` | pre-existing (e3478c60) | 50-51 | Approved axiom `WithTenantColumnJson` (uninterpreted total function); its body at 90-100 is not transliterated. |
| 57-69 `txRunner.ExecuteInTransactionAsync(async tx => { ... })` | e8cf3e52/e3478c60, role-scope order 36e02130 | 31-39 (TransactionBody), 52-53 | Four tx calls modelled in issue order: EnterTenantScopeAsync(tenantId) -> EnterTenantScope(t); ExecuteAsync(upsertSql, …) -> UpsertRow; ExitRoleScopeAsync() -> ExitRoleScope; ExecuteAsync(outboxSql, …) -> OutboxInsert. Deviation: tx-call failures/rollback not modelled (the properties concern the issued sequence on the success path). |
| 71 `return outboxRowId;` | pre-existing (3159f0d6) | 54-55 | Return -> `Written(steps)`; the Guid itself is not carried. |

Modelling caveats:

- `WithTenantColumn` is the approved axiom `WithTenantColumnJson`; its built case-variant removal (OutboxWriter.cs:96) is on the uncovered list. Transaction-call failures and rollback are not modelled, as at plan stage.

Obligations:

- `P1_ANullTenantIsRefusedBeforeTheTransaction`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ANullTenantIsRefusedBeforeTheTransaction.r1 (line 45: if tenantId.None? then ArgumentNull -> if tenantId.None? then Written([]); failed: P1_ANullTenantIsRefusedBeforeTheTransaction) ``
- `P2_TheUpsertAlwaysRunsInsideTheTenantScope`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_TheUpsertAlwaysRunsInsideTheTenantScope.r1 (line 35: [EnterTenantScope(tenantId)]   // await tx.EnterTenantScopeAsync(tenantId); -> []   // await tx.EnterTenantScopeAsync(tenantId);; failed: P2_TheUpsertAlwaysRunsInsideTheTenantScope) ``

#### `EngagementRepository.TenantScopedAccess`

Members:

- In Iverson.Server/Iverson.StarRocks/EngagementRepository.cs, all found, code changed: no for each: `TenantConnectionStringFor` (:85-87), `IsRecordedAsync` (:91-93), `RunTenantScopedAsync` (:302-348; comment change only), `StreamTenantScopedAsync` (:356-411), `SearchAsync` tenant-scoped path (:641-683), `AggregateAsync` (:685-755), `GroupByAsync` (:757-791), `PipelineAsync` (:793-827), `MatchRowsAsync` (:462-479), `UpsertAsync` (:481-509), `DeleteAsync` (:539-551).

Correspondence:

| Worktree file:line | Introduced by | Dafny lines | Note |
|---|---|---|---|
| :74-83 `TenantConnectionString` | T10 S3 | 20 (approved axiom) | Uninterpreted. The builder body is not modelled. |
| `TenantIdentifier.IsValid` (TenantIdentifier.cs) | pre-existing | 23 (approved axiom) | Uninterpreted. |
| :60-62 `NoTenantLoginsMessage` | T10 S3 | 38 (`InvalidOperation`) | The message text is not modelled. The exception type is. |
| :85-87 `TenantConnectionStringFor` | T10 S3 | 78-85 | `tenantLoginKey ?? throw` becomes `None? => Raise(InvalidOperation)`. |
| :91-93 `IsRecordedAsync` | T10 S3 | 87-95 | `provisionedTenants ?? throw` becomes `!HasDirectory => Raise(InvalidOperation)`. The directory's answer is the `recorded` input. |
| :191-192, :194-204 `OpenTenantConnectionAsync` / `OpenOrSurfaceRefusalAsync` | T10 S3 (shares the pre-existing body) | 97-104 | AccessDenied (1045) is mapped to `NotReady`. Anything else propagates unchanged. |
| :101-125 `RunAsync`, :141-171 `RewrapIfBackendUnavailableAsync` | pre-existing (comment-only change in T10 S3) | 52-64 (`DbRun`), 106-113 | Outside the candidate. The gate, breaker, retry and liveness probe are abstracted to the `DbRun` cases plus a `rewrap` flag, as in the starting model. |
| :288-291 `IsExpectedMissingResourceError` | pre-existing | 70-76 | The message's "Unknown table" check is carried as the `unknownTable` bool. |
| :302-348 `RunTenantScopedAsync` | T10 S3 | 133-147, 115-131 | :310-311 maps to 138-139. :313 maps to 141-143. :315-317 and :333-347 (telemetry) are abstracted: every exception is rethrown, and only the activity status changes. :321-329 maps to 145-146 and `RunTenantBody` 121-131 (:326 `new MySqlConnection(tenantConnectionString)`, then :327 open, then :328 operation). |
| :356-411 `StreamTenantScopedAsync` | T10 S3 | 149-172, 115-131 | :360-361 maps to 156-157. :363 maps to 159-161. :373-387 maps to 163 (`RunTenantBody`). :382-386 (ReleaseAsync on failure) is abstracted because it adds no data effect. :389-392 maps to 166. :393-397 maps to 168. :399 maps to the `Empty` result. :401-410 (row reads) maps to 171. A read failure after the reader starts is abstracted, as in the starting model. |
| :641-683 `SearchAsync` (tenant-scoped path :663-682) | T10 S3 | 174-198, 268 | :664-665 maps to 183. :667-669 (builder) maps to 184 (`sqlBuilt`). :671-672 maps to 186-188. :674-682 maps to 190-197. |
| :685-755 `AggregateAsync` (:694-696, else-branch :707-728) | T10 S3 | 200-207, 174-198, 269 | :694-696 maps to 205. :709-710 (`return null`) maps to `Empty`. :712-714 maps to `sqlBuilt`. :716-717 maps to 186-188. :719-727 maps to 190-197. The bucket and metric decoding at :730-754 is abstracted to `Rows` because it does not connect. |
| :757-791 `GroupByAsync` (tenant-scoped path :772-790) | T10 S3 | 174-198, 270 | Same shape as Search. |
| :793-827 `PipelineAsync` (tenant-scoped path :807-826) | T10 S3 | 174-198, 271 | Same shape. `MaskPipelineRows` at :821 is abstracted to `Rows`. |
| :462-479 `MatchRowsAsync` | T10 S3 | 209-225, 272 | :469-470 maps to 218. :472 maps to 219 (`sqlBuilt`). :474-475 maps to 221-223. :477-478 maps to 224. |
| :481-509 `UpsertAsync` | T10 S3 | 227-242, 273 | :487 maps to 236. :489 (`UpsertEntries`) and :506 (guard) map to 237 (`sqlBuilt`). :491 maps to 238. :507 maps to 240-241. Same abstraction as the starting model: both throw sites are folded into one `sqlBuilt` check placed before the `entries.Count == 0` check. It is unchanged and does not affect the frozen properties. Upsert does not consult `IsRecordedAsync` in the built code, which agrees with the model. |
| :539-551 `DeleteAsync` | T10 S3 | 244-261, 274 | :541 maps to 252. :545 maps to 254-256. :549 maps to 257 (`sqlBuilt`). :550 maps to 259-260. |

Modelling caveats:

- The gate, breaker, retry and liveness probe are abstracted to `DbRun` cases plus a rewrap flag; telemetry and post-reader read failures are abstracted; Upsert's two throw sites are folded into one `sqlBuilt` check. All carried from the plan-stage model.

Obligations:

- `P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting.r1 (line 188: else if !rec.value then (Empty, []) -> else if rec.value then (Empty, []); failed: P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting) ``
- `P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting.r1 (line 93: if !repo.HasDirectory then Raise(InvalidOperation, false) -> if repo.HasDirectory then Raise(InvalidOperation, false); failed: P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting) ``
- `P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting.r1 (line 83: if repo.TenantLoginKey.None? then Raise(InvalidOperation, false) -> if repo.TenantLoginKey.None? then Ret(repo.BaseCs); failed: P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting) ``
- `P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange.r1 (failed: RunTenantScopedOwnLogin — helper(s) in the proof closure of P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange) ``

#### `EngagementRepository.OpenOrSurfaceRefusalAsync`

Members:

- `OpenOrSurfaceRefusalAsync`: found, Iverson.Server/Iverson.StarRocks/EngagementRepository.cs:194-204. Code changed: no.
- `OpenTenantConnectionAsync`: found, EngagementRepository.cs:191-192. Code changed: no.
- `OpenProvisionerConnectionAsync`: found, EngagementRepository.cs:180-181. Code changed: no.

Correspondence:

| Worktree file:line | Code | Dafny lines | Note |
|---|---|---|---|
| EngagementRepository.cs:180-181 | `OpenProvisionerConnectionAsync(Func<Task> open) => OpenOrSurfaceRefusalAsync(open, "StarRocks refused the provisioner login; it may not have been created yet.")` | 50-57 (+ helper lemma 59-65) | Same literal. Helper lemma only proves the 74-unit literal fits CsString. No deviation. |
| EngagementRepository.cs:191-192 | `OpenTenantConnectionAsync(Func<Task> open) => OpenOrSurfaceRefusalAsync(open, "StarRocks refused the tenant's login.")` | 43-48 | Same literal. No deviation. |
| EngagementRepository.cs:194 | `private static async Task OpenOrSurfaceRefusalAsync(Func<Task> open, string refusal)` | 30-31 | `Func<Task> open` is modelled as its awaited outcome (`OpenStep`); a synchronous throw from `open()` and a faulted Task are both seen inside the try, so they are one outcome. |
| EngagementRepository.cs:196-199 | `try { await open().ConfigureAwait(false); }` | 33-34 | Success path -> `Opened`. |
| EngagementRepository.cs:200 | `catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)` | 35-36 | AccessDenied = 1045. |
| EngagementRepository.cs:201-203 | `throw new EngagementNotReadyException(refusal, ex);` | 37-38 | Throw modelled as an outcome variant (`OpenThrew(NotReady(refusal, ex))`). |
| (implicit, end of try/catch) | any other exception propagates unchanged | 39-40 | Uncaught exceptions return the same outcome. |

Obligations:

- `P1_ARefusedTenantLoginSurfacesAsNotReady`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ARefusedTenantLoginSurfacesAsNotReady.r1 (line 36: else if open.e.MySqlEx? && open.e.code == AccessDenied then -> else if open.e.MySqlEx? && open.e.code != AccessDenied then; failed: P1_ARefusedTenantLoginSurfacesAsNotReady) ``
- `P2_EveryOtherOpenOutcomeIsUnchanged`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_EveryOtherOpenOutcomeIsUnchanged.r1 (line 36: else if open.e.MySqlEx? && open.e.code == AccessDenied then -> else if open.e.MySqlEx? && open.e.code >= AccessDenied then; failed: P2_EveryOtherOpenOutcomeIsUnchanged) ``
- `P3_ARefusedProvisionerLoginStillSurfacesAsNotReady`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_ARefusedProvisionerLoginStillSurfacesAsNotReady.r1 (line 56: OpenOrSurfaceRefusal(open, refusal) -> OpenOrSurfaceRefusal(open, "StarRocks refused the tenant's login."); failed: P3_ARefusedProvisionerLoginStillSurfacesAsNotReady) ``

#### `EngagementRepository.IsExpectedMissingResourceError`

Members:

- `IsExpectedMissingResourceError`: found, Iverson.Server/Iverson.StarRocks/EngagementRepository.cs:288-291 (moved one line from 287-290). Code changed: no.

Correspondence:

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| EngagementRepository.cs:288 `private static bool IsExpectedMissingResourceError(Exception ex) =>` (T10 S3) | 16-21 (`predicate IsExpectedMissingResource(e: Ex)`) | `Exception` is modelled as `datatype Ex = MySqlEx(code: int, message: CsString) \| OtherEx` (line 14). It is non-nullable, as in the starting model. |
| EngagementRepository.cs:289 `ex is MySqlException mex &&` (T10 S3) | 23 `e.MySqlEx? &&` | none |
| EngagementRepository.cs:290 `mex.ErrorCode == (MySqlErrorCode)5502 &&` (T10 S3) | 24 `e.code == 5502 &&` | The enum is modelled as its int value. |
| EngagementRepository.cs:291 `mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase);` (T10 S3) | 25 `ContainsIgnoreCase(e.message, "Unknown table")` | Uses the approved axiom `ContainsIgnoreCase` (line 10). |

Obligations:

- `P1_AnUnknownTableIsStillAnExpectedEmptyTenant`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AnUnknownTableIsStillAnExpectedEmptyTenant.r1 (line 24: e.code == 5502 && -> e.code != 5502 &&; failed: P1_AnUnknownTableIsStillAnExpectedEmptyTenant) ``
- `P2_NothingElseIsAnExpectedEmptyTenant`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_NothingElseIsAnExpectedEmptyTenant.r1 (line 24: e.code == 5502 && -> true &&; failed: P2_NothingElseIsAnExpectedEmptyTenant) ``

#### `EngagementRepository.EnsureTenantProvisionedAsync`

Members:

- `EnsureTenantProvisionedAsync`: found, Iverson.Server/Iverson.StarRocks/EngagementRepository.cs:553-639. Code changed: no.

Correspondence:

| Worktree file:line (task/step) | Code | Dafny lines | Note |
|---|---|---|---|
| TenantIdentifier.cs:138-139 (pre-plan) | `Qualify` | 56-60 `Qualify` | Unchanged. |
| EngagementRepository.cs:57-58 (T10 S3) | `TenantSessionProperties` | 63-64 | Verbatim. |
| EngagementRepository.cs:553 (T10 S3) | signature `(string tenantId, EngagementTableSchema schema)` | 115 `Provision(repo, tenantId, table, failAt)` | `repo` carries the constructor fields the member reads (provisionerConnectionString, tenantLoginKey, provisionedTenants presence). `failAt` is the fault-injection index. `EngTable` keeps only TableName. All of this is carried from the starting model. |
| :555 | `if (!IsValid) return;` | 117 | Early return -> (Provisioned, []). |
| :560-562 | provisioner cs null -> throw IOE | 118 | ProvFault(InvalidOperation), no effects. |
| :566 | `provisionedTenants ?? throw IOE` | 119 | Same order: directory checked before key. |
| :567-568 | `tenantLoginKey ?? throw IOE`, then LoginPassword | 120, 122 | Key null-check happens before the LoginPassword call. |
| :569, 571-574 | loginName, dbName, roleName, qualifiedTable, BuildCreateTableDdl | 123-129 | The DDL goes through the approved axiom `CreateTableDdl`, guarded by a length check because concatenation is not length-bounded in the model. Carried from the starting model. A throw from BuildCreateTableDdl is not modelled, since the approved axiom is total. |
| :576-578 | Activity / tags | none | Telemetry only, no effect on outcome. |
| :580, 634 | `RunAsync(lambda)` | 101-111 `ProvisionBody`; 130-131 | Modelled as running the lambda once. The readiness gate, retry and circuit breaker are not modelled separately: a gate or breaker throw before any statement is the same as failAt = 0, which gives (Threw, []). Carried from the starting model. |
| :582-584 | open connection, `SET ROLE user_admin` | 75-79 `BeforeTry` (indices 0, 1) | Before the try, so a throw here skips the finally (model line 105). |
| :585-622 | try: 9 statements, then CreateTable (619) and Widen (620), `return true` | 82-95 `TryBlock` (indices 2..12); 107-110 | Each SQL string is verbatim, in the same order. CreatedTable and Widened are opaque effects. |
| :623-633 | finally `SET ROLE NONE`, catch-and-log | 98 `FinallyBlock`; 108 | Always appended once the try has started. A throw from it is swallowed, so it is not a fault point. |
| :637 | `directory.RecordProvisionedAsync(tenantId)` | 132-133 | Index 13: any failAt >= 13 means the record throws (OtherEx), otherwise Recorded is appended last. |
| :638 | SetStatus Ok | none | Telemetry only. |

Modelling caveats:

- `RunAsync`'s gate, retry and breaker are modelled as running the lambda once (a throw before any statement is `failAt = 0`); `BuildCreateTableDdl` is the total approved axiom `CreateTableDdl`. Carried from the plan-stage model.

Obligations:

- `P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds.r1 (line 132: else if failAt.Some? then (ProvFault(OtherEx), ran.1)   // RecordProvisionedAsyn -> else if failAt.None? then (ProvFault(OtherEx), ran.1)   // RecordProvisionedAsyn; failed: P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds) ``
- `P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting.r1 (line 119: else if !repo.HasDirectory then (ProvFault(InvalidOperation), []) -> else if repo.HasDirectory then (ProvFault(InvalidOperation), []); failed: P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting) ``
- `P3_TheTenantLoginReplacesTheAppGrant`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_TheTenantLoginReplacesTheAppGrant.r1 (line 90: Ran("GRANT `" + roleName + "` TO USER 'iverson_provisioner'@'%'"), -> Ran("GRANT `" + roleName + "` TO USER 'iverson_app'@'%'"),; failed: P3_TheTenantLoginReplacesTheAppGrant) ``

#### `PostgresProvisionedTenantDirectory.IsProvisionedAsync`

Members:

- `IsProvisionedAsync`: found, Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs:18-27. Code changed: no.
- `RecordProvisionedAsync`: found, PostgresProvisionedTenantDirectory.cs:29-39. Code changed: no.

Correspondence:

| cs lines | Dafny | note |
|---|---|---|
| 16 (_provisioned ordinal dict) | set<CsString> cache | keys only; byte value never read |
| 18-27 IsProvisionedAsync (found, 18-27) | IsProvisionedOutcome / IsProvisioned (lines 15-26) | ContainsKey early return, query (may throw -> Threw), TryAdd if found, return found |
| 29-39 RecordProvisionedAsync (found) | RecordOutcome / Record | ExecuteAsync may throw -> Threw, cache untouched; then TryAdd |

Modelling caveats:

- The ordinal `_provisioned` dictionary is modelled as a key set (its byte values are never read).

Obligations:

- `P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain.r1 (line 25: var cache' := if found then cache + {t} else cache;           // if (found) _pro -> var cache' := if !found then cache + {t} else cache;           // if (found) _pr; failed: P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain) ``
- `P2_RecordingCachesTheTenant`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_RecordingCachesTheTenant.r1 (line 41: case Ok(_) => (Ok(()), cache + {t})                               // _provisione -> case Ok(_) => (Ok(()), cache)                               // _provisioned.TryA; failed: P2_RecordingCachesTheTenant) ``

#### `StarRocksResiliencePipelineFactory.IsBackendTransient`

Members:

- `IsBackendTransient`: found, Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs:47. Code changed: no.
- `IsPoolExhausted`: found, StarRocksResiliencePipelineFactory.cs:56-58. Code changed: no (visibility private → public in 3c5ba85a; the model does not represent access modifiers).
- the two `ShouldHandle` predicates in `Build`: found, :19 (breaker) and :37 (retry). Code changed: no.

Correspondence:

| Worktree file:line | Introduced by | Dafny lines | Note |
|---|---|---|---|
| (MySqlException shape: ErrorCode, Message) | library | 6-10 | MySqlEx(code: int, message: CsString); non-nullable Message as CsString (Length is int). Unchanged from the starting model. |
| (MySqlErrorCode.UnableToConnectToHost) | library | 12-13 | const 1042. Unchanged. |
| (MySqlException.IsTransient) | library | 15-16 | Approved axiom, bodiless predicate. Unchanged. |
| (string.Contains(sub, StringComparison.Ordinal)) | library | 18-22 | Ordinal substring over UTF-16 code units. Unchanged. |
| :56-58 `IsPoolExhausted` | T11 S3 (c5f125cc); line 56 visibility changed private -> public by 3c5ba85a | 24-32 | Only the access modifier changed; the body (57-58) is the same. The model is the same apart from the cited line numbers/comment. |
| :47 `IsBackendTransient` | T11 S3 (c5f125cc) | 34-39 | Same text as the plan. No change. |
| :37 retry `ShouldHandle` (AddRetry) | T11 S3 (c5f125cc) | 41-47 | Handle<MySqlException>(IsBackendTransient): the outcome's MySqlException goes to the predicate. No change. |
| :19 breaker `ShouldHandle` (AddCircuitBreaker) | T11 S3 (c5f125cc) | 49-54 | Same as above. No change. (`Build`, line 11, became internal instead of public in 3c5ba85a. The class, line 9, became public instead of internal. Neither affects the predicates.) |
| — | frozen properties | 56-74 | Copied exactly; the bodies are empty. |

Modelling caveats:

- The new `TenantPoolFullMessage` const (:64-65) is called by no modelled member and is not modelled.

Obligations:

- `P1_AFullPoolIsNotTransient`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P1_AFullPoolIsNotTransient.r1 (line 38: IsTransient(e) && !IsPoolExhausted(e) -> IsTransient(e); failed: P1_AFullPoolIsNotTransient) ``
- `P2_EveryOtherFailureKeepsItsTransientClassification`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P2_EveryOtherFailureKeepsItsTransientClassification.r1 (line 30: e.code == UnableToConnectToHost && -> ; failed: P2_EveryOtherFailureKeepsItsTransientClassification) ``
- `P3_OnePredicateFeedsTheRetryAndTheBreaker`: **proved**. G5: `` replayed plan-stage mutant: KILLED by P3_OnePredicateFeedsTheRetryAndTheBreaker.r1 (line 46: IsBackendTransient(e) -> IsTransient(e); failed: P3_OnePredicateFeedsTheRetryAndTheBreaker) ``

### Obligation table

| Candidate | Obligation | Status |
|---|---|---|
| `SchemaBuilder.NormalizeDeclaredNames` | `P1_OwnerAndFieldPermissionNamesTakeTheColumnsSpelling` | proved |
|  | `P2_OnlyNonOneToManyForeignKeysAreRewritten` | proved |
|  | `P3_NormalizationNeverFails` | proved |
| `SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties` | `P1_PropertiesEqualIgnoringCaseAreRefused` | proved |
| `SchemaRegistrationOrchestrator.NavigationColumnCheck` | `P1_ANavigationNamedLikeAnyColumnIsRefused` | proved |
| `PayloadKeyCanonicalizer.Canonicalize` | `P1_AKeyNamingOneColumnTakesItsSpelling` | proved |
|  | `P2_TwoKeysNamingOneColumnAreRefused` | proved |
|  | `P3_AKeyMatchingSeveralColumnsIsRefused` | proved |
|  | `P4_KeysNamingNoColumnAndTheTenantKeyAreLeftAlone` | proved |
|  | `P5_NothingElseIsRefused` | proved |
| `StructSerializer.FoldKeys` | `P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted` | proved |
|  | `P2_NestedKeysAreAlwaysUpperFirsted` | proved |
| `EngagementRepository.UpsertEntries` | `P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected` | proved |
| `EngagementStoreConsumer.WithOwnerValue` | `P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey` | proved |
|  | `P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains` | proved |
| `PopularitySignalEligibility.IsEligible` | `P1_NotOneToManyOrMissingChild_IsNeverEligible` | proved |
|  | `P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow` | proved |
|  | `P3_AnUnregisteredChildIsNeverEligible` | proved |
| `ObjectSearchGrpcService.EligiblePopularitySignal` | `P1_OnlyAnEligibleSignalIsRankedOn` | proved |
| `ObjectSearchGrpcService.ReadableDecayField` | `P1_ADeniedDecayColumnGivesNoDecay` | proved |
|  | `P2_AReadableOrUnrestrictedDecayFieldIsKept` | proved |
| `ObjectSearchGrpcService.SimilarResponseAllowList` | `P1_ResponsesCarryOnlyDescriptorColumns` | proved |
|  | `P2_ThePayloadKeyIsAnsweredAsTheKeyColumn` | proved |
| `PopularitySignalValidator.SignalKeyCollision` | `P1_ASignalKeyCollidingWithAParentColumnIsRefused` | proved |
| `EnrichmentTargetRules.Violation` | `P1_AdmittedTargetsAreTextOrTextArrayColumns` | proved |
|  | `P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner` | proved |
|  | `P3_AdmittedTargetsHaveASourceAndAreNotSources` | proved |
|  | `P4_AdmittedExtractedTargetsHaveAHint` | proved |
|  | `P5_NoAdmittedTargetIsWriteRestricted` | proved |
|  | `P6_EveryTargetReaderCanReadEverySource` | proved |
|  | `P7_ADescriptorKeepingEveryRuleIsAdmitted` | proved |
| `SchemaRegistryRepository.OwnerQueries` | `P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds` | proved |
|  | `P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner` | proved |
|  | `P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable` | proved |
|  | `P4_AStoredFirstLetterVariantCountsAsTheSameType` | proved |
| `PrivilegedRequestRepository.Transitions` | `P1_AClaimTakesOnlyAPendingUnexpiredRequest` | proved |
|  | `P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires` | proved |
|  | `P3_OnlyClaimableRequestsAreListed` | proved |
|  | `P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed` | proved |
|  | `P5_CleanupDeletesOnlyFinishedRequests` | proved |
|  | `P6_ARequestExpiresAfterItsWaitOrNever` | proved |
| `PostgresPrivilegedRequestLocks.Keys` | `P1_EveryLockKeyLiesOutsideTheInt4Range` | proved |
|  | `P2_NoTypeLockIsTheRegistrationLock` | proved |
|  | `P3_ATypeLockFoldsTheNamesCase` | proved |
| `SchemaRegistrationApplier.ExecuteAsync` | `P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything` | proved |
|  | `P2_AnUnregisteredTypeNeverAdoptsAForeignTable` | proved |
|  | `P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable` | proved |
|  | `P4_DriftIsFailedPreconditionAndNothingIsWritten` | proved |
|  | `P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied` | proved |
|  | `P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders` | proved |
| `QueuedSchemaRegistrationExecutor.ExecuteAsync` | `P1_SuccessIsAnsweredOnlyAfterTheApiReloads` | proved |
|  | `P2_ARequestNeverClaimedIsExpiredAndUnavailable` | proved |
|  | `P3_AClaimedRequestStillRunningIsDeadlineExceeded` | proved |
|  | `P4_AFailedRequestAnswersWithItsStoredStatusAndMessage` | proved |
|  | `P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded` | proved |
| `PrivilegedRequestExecutor.RunAsync` | `P1_ARefusalIsRecordedWithItsStatusAndMessage` | proved |
|  | `P2_AnUnexpectedFailureIsInternalWithAGenericMessage` | proved |
|  | `P3_StoppingTheWorkerIsUnavailableNeverAPartialResult` | proved |
|  | `P4_EachKindTakesOnlyItsOwnLock` | proved |
|  | `P5_TheRegistryIsReloadedBeforeAnything` | proved |
|  | `P6_AReplayRechecksTheRowThenProducesBeforeMarking` | proved |
|  | `P7_AReconcileRecordsItsCountOrNotFound` | proved |
| `PrivilegedRequestWorker.PollOnceAsync` | `P1_OnlyClaimedRequestsRun` | proved |
|  | `P2_EachClaimedRunIsRecordedSucceededOnlyWhenOk` | proved |
|  | `P3_CleanupRunsAtMostHourly` | proved |
| `Program.PrivilegedAdminRoutes` | `P1_AnUnfinishedRequestIsAnswered202WithItsId` | proved |
|  | `P2_AWorkerNotFoundIs404` | proved |
|  | `P3_AnyOtherWorkerFailureIs500WithItsMessage` | proved |
|  | `P4_AFinishedRequestIs200` | proved |
|  | `P5_TheReplayChecksRunBeforeAnythingIsQueued` | proved |
|  | `P6_AdminRequestsNeverExpire` | proved |
| `Program.Health` | `P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry` | proved |
|  | `P2_StarRocksAuthPendingKeepsTheApiReady` | proved |
| `ObjectRetrievalGrpcService.GetManyTenantGuard` | `P1_ATenantlessDecisionIsRefusedBeforeTheRead` | proved |
|  | `P2_TheReadNeverRunsAcrossTenants` | proved |
| `ObjectMappingGrpcService.DeleteTenantGuard` | `P1_ATenantlessDecisionIsRefusedBeforeTheTransaction` | proved |
|  | `P2_TheDeleteNeverRunsAcrossTenants` | proved |
| `OutboxWriter.UpsertAndEnqueueOutboxAsync` | `P1_ANullTenantIsRefusedBeforeTheTransaction` | proved |
|  | `P2_TheUpsertAlwaysRunsInsideTheTenantScope` | proved |
| `EngagementRepository.TenantScopedAccess` | `P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting` | proved |
|  | `P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting` | proved |
|  | `P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting` | proved |
|  | `P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange` | proved |
| `EngagementRepository.OpenOrSurfaceRefusalAsync` | `P1_ARefusedTenantLoginSurfacesAsNotReady` | proved |
|  | `P2_EveryOtherOpenOutcomeIsUnchanged` | proved |
|  | `P3_ARefusedProvisionerLoginStillSurfacesAsNotReady` | proved |
| `EngagementRepository.IsExpectedMissingResourceError` | `P1_AnUnknownTableIsStillAnExpectedEmptyTenant` | proved |
|  | `P2_NothingElseIsAnExpectedEmptyTenant` | proved |
| `EngagementRepository.EnsureTenantProvisionedAsync` | `P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds` | proved |
|  | `P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting` | proved |
|  | `P3_TheTenantLoginReplacesTheAppGrant` | proved |
| `PostgresProvisionedTenantDirectory.IsProvisionedAsync` | `P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain` | proved |
|  | `P2_RecordingCachesTheTenant` | proved |
| `StarRocksResiliencePipelineFactory.IsBackendTransient` | `P1_AFullPoolIsNotTransient` | proved |
|  | `P2_EveryOtherFailureKeepsItsTransientClassification` | proved |
|  | `P3_OnePredicateFeedsTheRetryAndTheBreaker` | proved |

All 96 obligations are proved, and each passed G5.

### Ignored candidates

None: no obligation was `not verified` at plan stage.

### Uncovered

These are logic-bearing changes that come from neither a plan code block nor a re-proved correspondence range. They are not proved, and they do not change the recommendation.

- Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs:248-257 — logic-bearing — MySqlException pool-exhausted to 503 "busy" error mapping
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:62-63 — logic-bearing — TenantPoolFull maps to Unavailable status
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:119-146 — logic-bearing — Search row enumeration moved inside try; tenant-column strip and AllowedFields filtering
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:160-162 — logic-bearing — Search pool-exhausted catch mapping
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:829-832 — logic-bearing — pool-exhausted catch mapping (read RPC before GroupBy)
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:867-887 — logic-bearing — GroupBy row enumeration inside try; RemoveTenantColumn strip
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:901-903 — logic-bearing — GroupBy pool-exhausted catch mapping
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:952-972 — logic-bearing — Pipeline row enumeration inside try; RemoveTenantColumn strip
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:986-988 — logic-bearing — Pipeline pool-exhausted catch mapping
- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.MatchPattern.cs:180-183 — logic-bearing — MatchPattern pool-exhausted catch mapping
- Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:92 — logic-bearing — prior-owner refusal via SchemaRegistrationRefusals
- Iverson.Server/Iverson.Api/ApiLoginGrants.cs:49-51 — logic-bearing — privileged-request table grants (INSERT, SELECT, UPDATE Status column) for api login
- Iverson.Server/Iverson.Api/ApiLoginGrants.cs:62-71 — logic-bearing — granted-tables list that drives schema-apply lock acquisition (must match Statements)
- Iverson.Server/Iverson.Sql/ExistingTableInspector.cs:30-43 — logic-bearing — other-tenant rows predicate (< / > / IS NULL) and null-owner guard
- Iverson.Server/Iverson.Sql/OutboxWriter.cs:96 — logic-bearing — case-variant tenant key removal before setting tenant value
- Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs:1-111 (except the key lines :33-34, :40-41, :45-46, re-proved by PostgresPrivilegedRequestLocks.Keys) — logic-bearing — try-lock polling, lease unlock and pool discard on failure
- Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:20-23 — logic-bearing — shared table-lock key SQL (hashtext of table name)
- Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:70 — logic-bearing — session advisory lock on table-lock key
- Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:231 — logic-bearing — advisory unlock on table-lock key
- Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:305-316 — logic-bearing — grant transaction lock ordering (login lock, then each distinct table lock sorted)

Re-proved, dropped from the list (a returned correspondence range covers them):

- Iverson.Api/Consumers/EngagementStoreConsumer.cs:133 — re-proved by EngagementStoreConsumer.WithOwnerValue (:130-139)
- Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs:34-38 — re-proved by PayloadKeyCanonicalizer.Canonicalize (:34, :36-38)
- Iverson.Api/Grpc/ProtoPayloadHelper.cs:30-31 — re-proved by StructSerializer.FoldKeys (:27-31)
- Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:455-464 — re-proved by SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties (:453-467)
- Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs:21-24 — re-proved by SchemaRegistrationApplier.ExecuteAsync (OwnedByAnotherTenant :21-23)
- Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs:31 — re-proved by SchemaRegistrationApplier.ExecuteAsync (:25-54)
- Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs:53 — re-proved by SchemaRegistrationApplier.ExecuteAsync (:25-54)
- Iverson.Api/Program.cs:936-976 — re-proved by Program.PrivilegedAdminRoutes (helper :943-975; the rest is its doc comment and closing brace)
- Iverson.Api/Schema/SchemaBuilder.cs:281-284 — re-proved by SchemaBuilder.NormalizeDeclaredNames (:279-302)
- Iverson.Api/Schema/SchemaBuilder.cs:304-324 — re-proved by SchemaBuilder.NormalizeDeclaredNames and PayloadKeyCanonicalizer.Canonicalize (ColumnSpellingsByName :311-323; the rest is its doc comment)
- Iverson.Sql/JsonObjectKeys.cs:1-22 — re-proved by EngagementStoreConsumer.WithOwnerValue (RemoveIgnoringCase :14-21; the rest is usings, namespace and doc comment)

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

✅ Approve as-is. Every obligation is proved and passed G5, none is `removed or renamed`, and §2 and §3 are empty. The uncovered list does not change the label.
