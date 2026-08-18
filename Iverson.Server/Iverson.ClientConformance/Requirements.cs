namespace Iverson.ClientConformance;

/// <summary>
/// The registry of requirement IDs from <c>docs/standards/iverson-client-standard.md</c>.
///
/// A const exists here only for a requirement whose Status is <c>Active</c> in the standard.
/// <c>Retired</c> rows are not represented here at all. The coverage gate
/// (<see cref="RequirementsCoverageGateTests"/> in the test project) enforces, at build time,
/// that this class's set of <c>public const string</c> fields exactly matches the standard's set
/// of <c>Active</c> IDs, and that every const here is cited by at least one
/// <see cref="Assertion"/> constructed somewhere under <c>Iverson.ClientConformance/</c> (outside
/// this file and outside the test project).
/// </summary>
public static class Requirements
{
    // ── DECL — Declaration ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A client declares exactly one key property. Discharged by
    /// <c>Verifier.VerifyRegistration</c>'s "declares exactly one key property" assertion. This
    /// is a distinct assertion from the one REL's authoring notes describe as the loop backstop
    /// — that is the uncited "declares exactly the expected relation kinds" assertion
    /// (`Verifier.cs`, checking relation shape), not this one.
    /// </summary>
    public const string DeclExactlyOneKeyProperty = "IVC-DECL-001";

    /// <summary>
    /// A client declares a tenant field that is itself a declared property. Discharged by
    /// <c>Verifier.VerifyRegistration</c>'s "declares a tenant field" assertion, which requires
    /// the descriptor's <c>TenantField</c> to be non-empty AND to resolve against the
    /// descriptor's own <c>Properties</c> — a tenant field named but never declared is caught by
    /// the same assertion, not waved through.
    /// </summary>
    public const string DeclTenantFieldDeclared = "IVC-DECL-002";

    /// <summary>
    /// The key property is typed <c>UUID</c>. Discharged by
    /// <c>Verifier.VerifyRegistration</c>'s "key property is typed UUID" assertion, asserted
    /// directly from the descriptor's own <c>ClrType</c> — <c>ClrGuid</c> is exactly the CLR
    /// type the server maps to a <c>UUID</c> column.
    /// </summary>
    public const string DeclKeyTypedUuid = "IVC-DECL-003";

    /// <summary>
    /// A key value is a well-formed UUID on every leg — driver, the orchestrator's own gRPC
    /// read, and Postgres. All three legs are judged: <c>Verifier.VerifyThreeWay</c>'s "server
    /// returned a value" assertion judges the gRPC leg directly (the mirror of how
    /// <c>IVC-REL-010</c> cites the same assertion when <c>isKey</c> is false, so the two
    /// requirements partition that assertion's firings rather than double-covering either), and
    /// when <c>isKey</c> is true this const is also cited on the two agreement assertions —
    /// driver-vs-gRPC and gRPC-vs-Postgres — which judge the driver and Postgres legs because
    /// <c>ObservedValue.Matches</c> fails whenever either side did not parse as a UUID.
    /// </summary>
    public const string DeclKeyWellFormedUuid = "IVC-DECL-004";

    /// <summary>
    /// A client's declared tenant field is typed as a scalar string — never <c>UUID</c> and
    /// never array-typed. Discharged by <c>Verifier.VerifyRegistration</c>'s "tenant field is
    /// typed as a scalar string" assertion, which requires both <c>ClrType == ClrString</c> and
    /// <c>!IsArray</c> together — a client that types its tenant field as a GUID or as an array
    /// fails this, not merely one that omits it (that failure is <c>IVC-DECL-002</c>'s).
    /// </summary>
    public const string DeclTenantFieldTypedString = "IVC-DECL-005";

    /// <summary>
    /// A property declared array-typed never declares its CLR type as a delimited string.
    /// Discharged by <c>Verifier.VerifyRegistration</c>'s "array-typed property does not declare
    /// CLR_STRING" assertion, fired over every array-typed property on the descriptor — this is
    /// a declaration-level check independent of <c>IVC-REL-007</c>, which checks the wire value
    /// actually sent rather than the declared type.
    /// </summary>
    public const string DeclArrayNotDelimitedString = "IVC-DECL-006";

    // ── LIFE — Lifecycle ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mapped create, read, update and delete are each reachable through the client's public
    /// API. Discharged by <c>CrudRoundtripScenario.RequireStepOk</c>'s "step succeeded" assertion
    /// for the <c>write_author</c>/<c>write_tag</c>/<c>write_article</c>, <c>get</c>/
    /// <c>get_author</c>, <c>update</c> and <c>delete</c> steps — each fails outright (a
    /// Capability failure is a failure, per the design's ruling) if that operation could not be
    /// performed through the driver's own client library.
    /// </summary>
    public const string LifeMappedCrudReachable = "IVC-LIFE-001";

    /// <summary>
    /// A mapped create returns a key assigned by the server — encoded as a UUIDv7 — never a
    /// client-supplied one. The first clause is discharged by <c>CrudRoundtripScenario</c>'s
    /// "create returned a server-assigned UUIDv7 key" assertion, which inspects the version
    /// nibble of the key the write phase reported for <c>article</c>
    /// (`ObjectPersistenceGrpcService.Post` mints a UUIDv7 unconditionally and discards whatever
    /// key the client sent, per <c>2026-08-10-server-generated-ids-and-mapped-crud-parity-design.md</c>).
    ///
    /// The second clause — "never a client-supplied one" — has no arbitrary driver-supplied
    /// candidate available orchestrator-side to diff against: verified against all five drivers'
    /// write phases, none transmits a client-chosen key on a mapped create. Python's,
    /// TypeScript's, Go's and Java's write steps each state explicitly that a create request
    /// "must omit id entirely" and that "there is no client-derived key to fall back to any
    /// more" (<c>driver.py</c>, <c>driver.ts</c>, <c>main.go</c>, <c>Driver.java</c>); .NET's
    /// write step (<c>Program.cs</c>, <c>write_article</c>) never sets <c>DotNetArticle.Id</c>
    /// either, so <c>StructConverter.ToStruct</c> serializes it at its CLR default,
    /// <c>Guid.Empty</c>. That default IS a real, non-fabricated candidate, though — it is the
    /// literal wire value .NET's create payload carries, and the sentinel every other language's
    /// typed model would carry for an unset identifier — so <c>CrudRoundtripScenario</c>'s
    /// "create returned key is not the empty-key placeholder" assertion also cites this const,
    /// ruling out the specific regression where a server (or driver) echoes back whatever
    /// identity value it received instead of minting a fresh one. The deterministic
    /// <c>Keys.Derive</c>/<c>deriveKey</c> helper each driver also carries is NOT used for this:
    /// it exists only to re-resolve an already-known key for phases after <c>write</c> when
    /// <c>--keys</c> is absent, and is never sent on create, so diffing against it would compare
    /// against a value the server was never offered.
    /// </summary>
    public const string LifeCreateReturnsServerAssignedKey = "IVC-LIFE-002";

    /// <summary>
    /// An update changes the server's stored value, observable in a subsequent read. Discharged
    /// by <c>CrudRoundtripScenario</c>'s "article.Title: the update changed the server's stored
    /// value" assertion, which compares the server's own before/after title — never the value
    /// the driver merely claimed to have sent.
    /// </summary>
    public const string LifeUpdateReflectedInRead = "IVC-LIFE-003";

    /// <summary>
    /// A delete removes the row such that neither the orchestrator's own gRPC read nor the
    /// Postgres row finds it afterward. Both clauses are discharged: the gRPC clause by
    /// <c>CrudRoundtripScenario</c>'s "delete: the orchestrator's gRPC read no longer finds the
    /// row" assertion, the Postgres clause by its "delete: the Postgres row is gone" assertion —
    /// neither alone would prove the row is actually gone rather than merely unreadable through
    /// one path.
    /// </summary>
    public const string LifeDeleteRemovesRow = "IVC-LIFE-004";

    // IVC-LIFE-005 is Retired — it conflated reachability and hydration into one statement. Split
    // into IVC-LIFE-006 (reachability) and IVC-LIFE-007 (hydration, itself retired and
    // re-authored as IVC-LIFE-008). It takes no const.

    /// <summary>
    /// A depth-resolved read is reachable through the client's public API. Supersedes the retired
    /// <c>IVC-REL-009</c>, and is the reachability half of the retired <c>IVC-LIFE-005</c>.
    /// Discharged by <c>CrudRoundtripScenario.RequireStepOk</c>'s "step succeeded" assertion for
    /// each driver's OWN depth-1 read (<c>get_depth1</c> step) — deliberately not the
    /// orchestrator's own <c>MappingGet</c>, which only proves the SERVER can serve a
    /// depth-resolved read. Having each driver perform its own depth-1 read through its own client
    /// library is what proves the CLIENT can express the request and get back a result. This
    /// requirement is satisfied by the call completing (<c>step.Ok</c>); the assertion does not
    /// inspect <c>step.Entity</c>, matching the standard's statement, which says only
    /// "reachable" — it says nothing about whether that entity is hydrated — that is
    /// <c>IVC-LIFE-008</c>, a distinct assertion, so a client that reaches without hydrating goes
    /// green here and red there.
    /// </summary>
    public const string LifeDepthResolvedReadReachable = "IVC-LIFE-006";

    /// <summary>
    /// The entity returned by a depth-resolved read carries the related object's data, including
    /// that object's own key and not only the foreign key — the successor of the retired
    /// <c>IVC-LIFE-007</c> (itself the hydration half of the retired <c>IVC-LIFE-005</c>).
    /// <c>IVC-LIFE-007</c>'s statement was framed as a navigation member being "hydrated", graded
    /// by finding a property carrying an object with its own key — a framing that encodes .NET's
    /// object shape. This statement is framed instead as an observable property of what the
    /// operation returns: it names no member, type or signature detail, so each client can satisfy
    /// it in whatever shape its language allows. It stays a <c>Behaviour</c> — <c>Capability</c> is
    /// reachability, and <c>IVC-LIFE-006</c> already holds that Kind for the depth-resolved read
    /// itself. Discharged by <c>Verifier.VerifyDepthCapability</c>, called from
    /// <c>CrudRoundtripScenario</c>'s read phase against each driver's OWN depth-1 read
    /// (<c>get_depth1</c> step) after <c>IVC-LIFE-006</c>'s reachability assertion has already
    /// fired separately on the same step. This requires at least one of the descriptor's own
    /// relations to have actually carried its related object's data (a nav property, or — when
    /// that yields nothing — the hydration-carrier property — carrying an object with its own key)
    /// in the JSON the DRIVER itself reported back — a driver that reaches the depth-1 read but
    /// discards the hydrated payload (no field on its typed model to receive it) fails only this
    /// requirement, not <c>IVC-LIFE-006</c>. Live run 2026-08-18: passes for all five clients
    /// (.NET, Python, TypeScript, Go, Java) — the standard's LIFE section no longer carries a
    /// "Known non-conformance" entry for this requirement.
    /// </summary>
    public const string LifeDepthResolvedReadHydrated = "IVC-LIFE-008";

    // ── REG — Registration ──────────────────────────────────────────────────────────────────

    // IVC-REG-001 is Retired — read literally (unqualified over every relation kind), its
    // statement was factually wrong: SchemaRegistrationOrchestrator.cs's naming loop deliberately
    // excludes one_to_many, whose foreign key is named after THIS type, not the related type, and
    // lives on the related type's own row. A conforming server that correctly accepts a
    // one_to_many descriptor would have read as non-conformant against IVC-REG-001's literal text.
    // Superseded by IVC-REG-003, which states the same rule scoped to the three kinds it actually
    // governs. It takes no const.

    /// <summary>
    /// The server rejects registration of a relation whose navigation-property name equals its
    /// foreign key, for every relation kind. Distinct from <c>IVC-REL-003</c>, which is the
    /// CLIENT-side derivation obligation — this is the server enforcement boundary
    /// (<c>RelationCollisionCheck.IsCollision</c>, consulted by
    /// <c>SchemaRegistrationOrchestrator.cs</c> ~line 130-139) that closes the hole for any
    /// client, including a sixth one that forgets to derive a distinct name. Discharged by
    /// <c>NavPropertyRejectedScenario</c>'s registration-time check: before posting the illegal
    /// write payload that discharges <c>IVC-REL-005</c>, the scenario also attempts to REGISTER a
    /// second, self-contained fixture whose <c>PropertyName</c> equals its <c>ForeignKey</c>, and
    /// asserts that <c>RegisterSchema</c> itself rejects it with <c>InvalidArgument</c> — a
    /// distinct observation from the write-payload check, which exercises a descriptor that was
    /// never colliding in the first place. "For every relation kind" is exercised across all four
    /// kinds (<c>ManyToOne</c>, <c>OneToOne</c>, <c>ManyToMany</c>, <c>OneToMany</c>) by
    /// <c>NavPropertyRejectedScenario.CollisionFixtures</c> — <c>OneToMany</c> is the kind the
    /// <c>IVC-REG-003</c> ruling reversed (exempted from the naming check), so it is the kind most
    /// likely to regress silently if the collision check were ever re-narrowed to match, and a
    /// missing fixture for it would leave this cell green through such a regression.
    /// </summary>
    public const string RegNavPropertyCollisionEnforced = "IVC-REG-002";

    /// <summary>
    /// The server rejects registration of a <c>many_to_one</c>, <c>one_to_one</c> or
    /// <c>many_to_many</c> relation whose foreign key is not named <c>{RelatedTypeName}Id</c> (or
    /// <c>{RelatedTypeName}Ids</c> for <c>many_to_many</c>). Supersedes the retired
    /// <c>IVC-REG-001</c>, scoped — like its sibling <c>IVC-REL-001</c> — to the three kinds the
    /// naming rule actually governs; <c>one_to_many</c> is excluded because its foreign key is
    /// named after THIS type and lives on the related type's own row, and
    /// <c>SchemaRegistrationOrchestrator.cs</c>'s naming loop (~line 83) deliberately excludes it.
    /// Distinct from <c>IVC-REL-002</c>, which is the CLIENT-side derivation obligation — this is
    /// the server enforcement boundary that makes the naming rule hold even for a client (or a
    /// hand-built payload) that never derives the name correctly in the first place, per ruling 5
    /// ("the server is the enforcement boundary"). Discharged by
    /// <c>NamingRejectedScenario.JudgeServerSide</c>, which hand-builds descriptors carrying a
    /// misnamed foreign key across the <c>many_to_one</c> and <c>many_to_many</c> kinds and posts
    /// each directly to <c>RegisterSchema</c> over gRPC — the ONE orchestrator-side observation
    /// this scenario now carries (in the <c>dotnet</c> column, since .NET's
    /// <c>[ManyToOne(typeof(Author), "WriterId")]</c> is the one client-declaration style that can
    /// express a misnamed foreign key at all; Go/Python/TypeScript catch it client-side per
    /// <c>IVC-REL-002</c>'s recommended diagnostic, and Java's registrar has no override at all,
    /// so neither reaches this check) — asserting <c>SchemaRegistrationOrchestrator.cs</c>'s
    /// naming check (~line 110-122) rejects each with <c>InvalidArgument</c>, naming both the
    /// actual and required foreign-key names. The <c>many_to_many</c> fixture is what gives the
    /// statement's parenthetical <c>Ids</c> clause its own citation — previously only the
    /// <c>many_to_one</c> half of the statement had a fixture behind it.
    /// </summary>
    public const string RegForeignKeyNamingEnforced = "IVC-REG-003";

    // ── REL — Relations ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A client synthesizes a foreign-key property for <c>many_to_one</c>, <c>one_to_one</c> and
    /// <c>many_to_many</c>, and none for <c>one_to_many</c> (whose key lives on the related
    /// type's own row). Both halves are discharged: the positive half by
    /// <c>Verifier.VerifyRegistration</c>'s "foreign key ... is a declared property" assertion,
    /// fired for the three owning kinds; the negative half by that same method's "no foreign key
    /// was synthesized on the declaring type for a one-to-many relation" assertion, which fails
    /// if the declaring type carries a property spuriously shaped like
    /// <c>{RelatedTypeName}Id</c> for a <c>one_to_many</c> relation's related type. Absence of an
    /// assertion is not an assertion of absence — this const previously rationalized the negative
    /// half as discharged by the loop's <c>continue</c>, which asserted nothing.
    /// </summary>
    public const string RelForeignKeySynthesizedForOwningKinds = "IVC-REL-001";

    /// <summary>
    /// A synthesized foreign-key property is named <c>{RelatedTypeName}Id</c>, or
    /// <c>{RelatedTypeName}Ids</c> for the array-typed <c>many_to_many</c> form. Discharged by
    /// <c>Verifier.VerifyRegistration</c>'s dedicated naming assertion, which now grades every
    /// owning relation kind — <c>ManyToOne</c>, <c>OneToOne</c> and <c>ManyToMany</c> — comparing
    /// the declared foreign key's normalized name against the relation's <c>RelatedType</c> plus
    /// the kind-appropriate suffix. The standard's statement is unqualified over every
    /// synthesized foreign key; a prior version of this check excluded <c>many_to_many</c>,
    /// which weakened the statement rather than the other way around.
    /// </summary>
    public const string RelForeignKeyNamedRelatedTypeId = "IVC-REL-002";

    /// <summary>
    /// A client derives a navigation-property name distinct from the relation's foreign-key
    /// name, for every relation kind including <c>many_to_many</c> — the ruling in
    /// "Why <c>IVC-REL-003</c> covers <c>many_to_many</c>" (design doc). A collision lets
    /// hydration overwrite the foreign key (<c>EntityRelationResolver</c> writes the hydrated
    /// value to <c>entityStruct.Fields[relation.PropertyName]</c>), which is what makes
    /// <c>IVC-REL-006</c> unconditional. Discharged by <c>Verifier.VerifyRegistration</c>'s
    /// distinctness assertion, extended in this change to <c>ManyToMany</c> as well as
    /// <c>ManyToOne</c>/<c>OneToOne</c>, and enforced server-side at registration
    /// (<c>RelationValidator.cs</c>, tightened alongside T3/T4).
    /// </summary>
    public const string RelNavPropertyDistinctFromForeignKey = "IVC-REL-003";

    /// <summary>
    /// <c>isArray</c> is set on the foreign-key property for <c>many_to_many</c> and for no
    /// other kind. Discharged by <c>Verifier.VerifyRegistration</c>'s "foreign key ... is
    /// declared isArray" assertion (positive case, scoped to <c>ManyToMany</c>) and its
    /// "isArray is set only for a many-to-many foreign key" assertion (negative case, over every
    /// array-typed property on the descriptor).
    /// </summary>
    public const string RelIsArraySetForManyToManyOnly = "IVC-REL-004";

    /// <summary>
    /// Write payloads carry foreign-key values only; navigation properties are never sent.
    /// Discharged by <c>NavPropertyRejectedScenario.Judge</c>, which posts a payload keyed by a
    /// navigation property and asserts the server rejects it with <c>InvalidArgument</c>, naming
    /// both the navigation property and the required foreign key
    /// (<c>RelationValidator.ValidateAndNormalizeRelations</c>).
    /// </summary>
    public const string RelWritePayloadForeignKeyOnly = "IVC-REL-005";

    /// <summary>
    /// A foreign-key value is readable at every depth, including after hydration. Discharged by
    /// <c>Verifier.VerifyRelationHydrated</c>'s "foreign key ... survives hydration" assertion,
    /// which requires the foreign key to still be present — not overwritten by the nav property
    /// — in a depth-1 <c>MappingGet</c> response.
    /// </summary>
    public const string RelForeignKeyReadableAtDepth = "IVC-REL-006";

    /// <summary>
    /// Multi-valued foreign keys are sent as a list, never a delimited string. Discharged by
    /// <c>CrudRoundtripScenario</c>'s dedicated array-shape assertion over the driver's own
    /// depth-0 read of a <c>many_to_many</c> foreign key, which requires the raw JSON value to be
    /// a <see cref="System.Text.Json.JsonValueKind.Array"/> rather than a single string.
    /// </summary>
    public const string RelMultiValuedForeignKeyAsList = "IVC-REL-007";

    /// <summary>
    /// <c>one_to_many</c> resolves by reverse foreign-key lookup on the related type. Discharged
    /// by <c>Verifier.VerifyRelationHydrated</c>'s "one-to-many nav hydrates at depth 1"
    /// assertion, which requires the reverse-navigation collection to be non-empty in a depth-1
    /// <c>MappingGet</c> response (<c>EntityRelationResolver.ResolveOneToManyAsync</c> →
    /// <c>FetchByColumnAsync</c>).
    /// </summary>
    public const string RelOneToManyReverseLookup = "IVC-REL-008";

    // IVC-REL-009 is Retired — superseded by the LIFE depth capability. It takes no const.

    /// <summary>
    /// Foreign-key values are well-formed UUIDs, and foreign-key columns are typed <c>UUID</c>
    /// or <c>UUID[]</c>. Both clauses are discharged, and both are scoped to foreign-key columns
    /// only — never the primary key, which is also present in
    /// <c>Verifier.ComparedValueNames</c> and would otherwise let a type with zero owning
    /// relations discharge this requirement having observed no foreign key at all.
    /// <list type="bullet">
    /// <item><description>Well-formedness: <c>Verifier.VerifyThreeWay</c>'s "server returned a
    /// value" assertion requires the gRPC leg's raw value to have parsed as one or more
    /// <see cref="System.Guid"/>s (<c>ObservedValue.Uuids</c> non-null with at least one
    /// element), and cites this const only when <c>isKey</c> is false.</description></item>
    /// <item><description>Typing: <c>Verifier.VerifyRegistration</c>'s "foreign key ... is typed
    /// UUID" assertion, asserted directly from the descriptor the driver reported —
    /// <c>fkProperty.ClrType == ClrType.ClrGuid</c> — rather than deferred to the server-side
    /// enforcement in <c>SchemaRegistrationOrchestrator.cs</c> that the harness never
    /// observes.</description></item>
    /// </list>
    /// </summary>
    public const string RelForeignKeyWellFormedUuid = "IVC-REL-010";
}
