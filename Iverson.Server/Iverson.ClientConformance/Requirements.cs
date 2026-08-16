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
