# SDD ledger — plan: /home/ben/repositories/Iverson/.claude/worktrees/relation-properties-write-path/docs/plans/2026-08-06-strip-relation-nav-properties-implementation-plan.md

Worktree: /home/ben/repositories/Iverson/.claude/worktrees/relation-properties-write-path
Branch: worktree-relation-properties-write-path
MERGE_BASE for this plan's work: 54baf7d (docs-only commits since ae73e0a)

Baseline: 627 passed / 0 failed / 627 total, measured by the controller on this tree at ae73e0a.
All commits between ae73e0a and 54baf7d are docs-only (spec, plan, 4 critical reviews), so the
baseline still holds. Expected counts: 633 after Task 1, 641 after Task 2.

NOTE: this branch also carries the PRECEDING relation-properties work (0c30a4f, ef1da2a, e08b519,
ae73e0a), which is complete and reviewed but NOT merged — its finishing menu is still open. This
plan's tasks stack on top of it.

Pre-flight conflict scan: clean. No task contradicts another or the Global Constraints.

Controller resolutions carried into dispatches (from CIR round 2's implementer notes):
  - The new helper must NOT write the type name `RelationDescriptor` explicitly. Both
    `Iverson.Api.Schema` and `Iverson.Client.Contracts` declare it and both namespaces are imported
    by ObjectMappingGrpcService.cs (which already aliases RelationKind for this reason). Inferring
    the element type from `schema.Relations` is what keeps it compiling.
  - Task 1's test 6 must capture the serialized payload via the published `EntityEvent` or the
    transactional SQL. The outbox ROW carries no payload column — `payloadJson` goes to the entity
    upsert and the Kafka event.

Task 1: implemented, commit 14b6765 (BASE 54baf7d). Implementer stalled mid-wait exactly as the
  known pattern predicts — edits on disk, nothing committed, no report. Recovered by checking disk
  state and resuming the live agent via SendMessage, NOT re-dispatching.
  Controller re-ran the Api suite independently: 633 passed / 0 failed / 633 total. Matches the
  implementer's claim exactly.
Task 1: review found 1 Important — the restore canonicalises the nav key's case, so a caller sending
  `author` gets back `Author`. This was a CONTRADICTION INSIDE THE PLAN: Step 3's code block mandated
  `SetField(payload, r.PropertyName, ...)`, while the Global Constraint says stripping must not change
  what the caller gets back. Escalated to Ben per the plan-mandated rule.
  BEN'S RULING (2026-08-06): "Fix the code AND keep the wording" — the constraint governs, AND exact
  caller-spelling preservation is the intended contract GOING FORWARD. Task 2 and any later payload
  mutation must preserve caller spelling too. Carry this into Task 2's dispatch.
  Context worth keeping: CIR round 2 examined this exact behaviour and dropped it, having verified no
  client actually breaks (.NET FromStruct is case-insensitive; Python/TS/Java read PascalCase; Go
  sends no relation fields). The finding is real against the constraint's wording, not against any
  client. Ben chose the stricter contract anyway.
Task 1: minor (deferred): the capture filter duplicates the navIsDistinctKey expression from
  RelationValidator rather than sharing a helper; Task 2 will add a third copy.
Task 1: fix round 1/5 (1 addressed, 0 open — restore now writes the caller's actual key via
  StructFieldAccess.Candidates instead of the schema's PropertyName; commits 14b6765..1c401ed).
  Scoped re-review verdict ADDRESSED, no new breakage. Reviewer confirmed both spellings covered,
  the camelCase test carries a real negative assertion (author present AND Author absent), and the
  strip side is untouched so the persisted/published payload is unaffected.
Task 1: minor (deferred): CaptureNavProperties SelectMany over Candidates could capture BOTH case
  variants if a malformed payload set Author and author simultaneously. Harmless today — RemoveField
  strips both variants regardless, so strip/restore stay symmetric per key.
Task 1: complete (commits 54baf7d..1c401ed, review clean)
  Controller re-ran the Api suite independently on this tree: 634 passed / 0 failed / 634 total.
  Matches the implementer's claim exactly. One above the plan's predicted 633 because the fix added
  a regression test; Task 2's predicted 641 therefore becomes ~642.
Task 2: implemented (commit 715e7a8). Implementer stalled mid-wait exactly as this ledger predicted;
  recovered by resuming the live agent via SendMessage rather than re-dispatching. Same recovery as
  before — record it as the standing procedure for this failure mode.
  Controller re-ran the Api suite independently: 642 passed / 0 failed / 642 total. Matches the
  implementer's claim exactly. 642 not the brief's 641 because Task 1 landed at 634, not 633.
Task 2: deviation ACCEPTED — the implementer edited a pre-existing test in place
  (ManyToOne_ForeignKeyAlreadyPresent_NavPropertyIgnored -> ...MatchingNavPropertyNotOverridden).
  That test pinned silent FK-precedence over a DISAGREEING nav key, i.e. the exact behaviour Task 2
  exists to remove. Reviewer independently confirmed the edit was mandated, not convenience.
Task 2: review round 1/5 — 0 Critical, 1 Important, 2 Minor.
  IMPORTANT (fixed, commit 8ef9684): the rewritten test used ONE spelling for both the FK and the
  nav key, so its assertion could no longer fail — the file lost its only guard that a PRESENT FK is
  never renormalized from the nav object's spelling. Restored by adding the FK-value assertion to
  ManyToOne_CaseAndBraceVariantGuids_Accepted, the one test where the two spellings genuinely
  differ (upper-case FK vs "B"-braced nav). This is the same caller-spelling contract Ben set on
  Task 1 — a regression here would write a braced/upper GUID into the FK column and silently fail to
  match the related row in StarRocks and Qdrant. RelationValidatorTests 33/33 after the fix.
  Lesson: a test edited to make its premise consistent with a behaviour change can go vacuous
  without any assertion being deleted. Check that the edited fixture can still fail.
Task 2: minor (deferred): a ManyToMany nav list of scalars (ids as strings, not objects) yields an
  empty navKeys and so reports "'Tags' and 'TagIds' disagree" instead of the normalize path's much
  better "expected an object, got a scalar". Low reachability — such clients also collide
  PropertyName with ForeignKey and are guarded by navIsDistinctKey.
Task 2: minor (deferred): the ManyToMany disagreement message names neither the differing keys nor
  the direction, unlike its ManyToOne sibling which quotes both GUIDs. Brief-specified, but it makes
  the plan's Known-issue "partial nav subset on deleted/unreadable rows" hard to diagnose.
Task 2: complete (commits 1c401ed..8ef9684, review CHANGES REQUESTED then addressed).

FINAL WHOLE-BRANCH REVIEW (ae73e0a..99e3dca) — 0 Critical, 1 Important, 3 Minor.
  Verdict "Ready to merge: Yes". The earlier stacked plan's normalization substrate is not broken or
  contradicted by this one. Reviewer independently confirmed the end-to-end trace: payloadJson is
  computed BEFORE RestoreNavProperties and the outbox/Kafka publish consume the string, not the
  Struct, so no nav property reaches Postgres, StarRocks, Qdrant or the event body; no consumer reads
  a relation PropertyName from a payload; only the 2 call sites that echo request.Payload
  capture/restore, and the 2 in ObjectPersistenceGrpcService correctly do not.
  IMPORTANT (fixed, commit below): ValidateCollectionRelation treated a nav list with NO readable
  keys as a disagreement, while ValidateSingleRelation treats an unreadable nested key as "no second
  opinion". A write that succeeded before this branch would now return InvalidArgument naming neither
  the offending item nor the cause. Fixed by gating the SetEquals on navKeys.Count > 0, mirroring the
  ManyToOne rule, plus a regression test (ManyToMany_NavListWithNoReadableKeys_ForeignKeyListStands).
  This SUPERSEDES the Task 2 deferred minor about scalar nav lists — and note the ledger's recorded
  justification for deferring it was WRONG: Python/TS/Java only collide PropertyName with ForeignKey
  when the m2m field is NAMED tag_ids/tagIds; a field named `tags` is distinct and unguarded. Python
  and Java are saved instead by their converters stringifying collections. Lesson: a deferral is only
  as good as its stated reason; the reason, not just the verdict, needs checking.
  Controller re-ran the Api suite: 643 passed / 0 failed / 643 total.
  MINOR (deferred): KeyColumnNameFor's `?? "Id"` registry-miss fallback is silent, and Task 2 raised
  its consequence from "FK not normalized" to "write rejected". Worth logging the miss.
  MINOR (deferred): the navIsDistinctKey predicate is duplicated in RelationValidator and
  ObjectMappingGrpcService.CaptureNavProperties. Composed with Ben's caller-spelling ruling this is a
  contract risk — if the two diverge, strip-without-capture makes the nav property vanish from the
  echoed response, and no test couples them.
  MINOR (deferred) coverage gaps no single task owned: Mapping.Update's capture/restore pair is
  untested (only Mapping.Post is); echo tests cover ManyToOne only, not ManyToMany or OneToMany.
  OUT OF SCOPE, worth its own investigation: Java StructConverter.toValue falls through to
  val.toString() for collections, so a Java client sends TagIds as a STRING, not a ListValue — the
  Java sibling of the Go issue the spec already flags.
