using System.Text.Json;
using Google.Protobuf;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance;

/// <summary>
/// One judgement the orchestrator made. Assertions live only here and in the scenarios that call
/// this class — never in a driver, which reports and never judges.
/// </summary>
public sealed record Assertion(string Name, bool Passed, string Detail = "", string? RequirementId = null)
{
    public static Assertion Pass(string name, string detail = "", string? requirementId = null) => new(name, true, detail, requirementId);
    public static Assertion Fail(string name, string detail, string? requirementId = null) => new(name, false, detail, requirementId);
    public static Assertion From(string name, bool passed, string detail, string? requirementId = null) => new(name, passed, detail, requirementId);
}

/// <summary>
/// The three independent observations of one named value. Each leg is resolved from a different
/// source so that agreement is evidence rather than tautology:
/// <list type="bullet">
/// <item><description><see cref="Driver"/> — what the client library handed back from its own
/// <c>read</c> phase. Deliberately NOT taken from the write/update steps: only the .NET driver
/// returns the server's entity there; Python, TypeScript and Go report the locally constructed
/// pre-call object and Java reports null, so a write-phase leg would compare the driver's own
/// input against the server's state and agree by construction.</description></item>
/// <item><description><see cref="Grpc"/> — the orchestrator's own <c>MappingGet</c>.</description></item>
/// <item><description><see cref="Postgres"/> — a direct query against the row.</description></item>
/// </list>
/// </summary>
public sealed record ThreeLegs(ObservedValue Driver, ObservedValue Grpc, ObservedValue Postgres);

/// <summary>
/// One leg's observation of one named value, canonicalized to a set of UUIDs so that the three
/// legs are compared parsed rather than as strings (they spell and format UUIDs differently).
/// <see cref="Uuids"/> is null when the raw value could not be read as UUIDs at all — which is a
/// failure, distinct from an empty set.
///
/// An ABSENT field and a NULL field produce the same value here, on purpose: Java's Gson omits
/// null fields where the other four emit them explicitly, so any check that told the two apart
/// would fail for Java only.
/// </summary>
public sealed record ObservedValue(string Raw, IReadOnlyList<Guid>? Uuids)
{
    public static readonly ObservedValue Missing = new("(absent/null)", []);

    public static ObservedValue Unreadable(string raw) => new(raw, null);

    public bool IsEmpty => Uuids is { Count: 0 };

    /// <summary>Order-insensitive: a multi-valued FK is a set, and no client promises an order.</summary>
    public bool Matches(ObservedValue other) =>
        Uuids is not null && other.Uuids is not null &&
        Uuids.Order().SequenceEqual(other.Uuids.Order());

    public override string ToString() => Uuids is null
        ? $"<unreadable: {Raw}>"
        : Uuids.Count switch
        {
            0 => "<none>",
            1 => Uuids[0].ToString(),
            _ => "[" + string.Join(", ", Uuids.Order()) + "]",
        };
}

/// <summary>
/// Every assertion S1 makes, as pure functions over reported data. Nothing here talks to a
/// process, a channel or a database — the scenario gathers the observations and this class judges
/// them, so each judgement is unit-testable without a live stack.
/// </summary>
public static class Verifier
{
    private static readonly JsonParser DescriptorParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>
    /// Parses a driver-reported descriptor into the strongly typed contract message. Going
    /// through protobuf's own JSON parser rather than reading the <see cref="JsonElement"/> by
    /// hand is what makes the five drivers comparable at all: they variously emit camelCase or
    /// snake_case names, enum values as names or as numbers, and (Go, TypeScript) omit
    /// proto3 default values entirely. The parser resolves all of that to one shape, and an
    /// omitted field lands on the same default as an explicitly-default one — the absent/null
    /// equivalence the harness requires.
    /// </summary>
    public static TypeDescriptor ParseDescriptor(JsonElement json) =>
        DescriptorParser.Parse<TypeDescriptor>(json.GetRawText());

    /// <summary>
    /// Case-insensitive, separator-insensitive identifier key: <c>py_author_id</c>,
    /// <c>pyAuthorId</c> and <c>PyAuthorId</c> all normalize alike. The three legs genuinely
    /// spell their names differently — Postgres and <c>MappingGet</c> use the descriptor's
    /// property names, the driver uses its own language's member naming.
    /// </summary>
    public static string Normalize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    // ── Registration ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The kind-scoped registration assertions, against the descriptor the driver actually sent.
    ///
    /// The propertyName != foreignKey distinctness check applies to <c>ManyToMany</c> as well as
    /// <c>ManyToOne</c>/<c>OneToOne</c> (<c>IVC-REL-003</c>). It used to be exempt for
    /// <c>ManyToMany</c> by design, on the theory that the FK-on-the-member clients (Python,
    /// TypeScript, Go, Java) legitimately produce a colliding name and the server treated that as
    /// correct rather than as a defect. That reasoning did not survive contact with
    /// <c>EntityRelationResolver</c>: a collision lets hydration overwrite the foreign key with
    /// the nav property's resolved value at the same struct key, which makes the collision
    /// unconditionally break <c>IVC-REL-006</c> (the foreign key must remain readable at every
    /// depth). The 2026-08-15 ruling extends the check to every relation kind and closes the hole
    /// server-side too — see <c>Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs</c>.
    /// </summary>
    public static IReadOnlyList<Assertion> VerifyRegistration(
        string label,
        TypeDescriptor descriptor,
        IReadOnlyCollection<RelationKind>? expectedRelationKinds = null)
    {
        var results = new List<Assertion>();

        var propertiesByName = new Dictionary<string, PropertyDescriptor>(StringComparer.Ordinal);
        foreach (var property in descriptor.Properties)
            propertiesByName.TryAdd(Normalize(property.Name), property);

        results.Add(Assertion.From(
            $"{label}: declares exactly one key property",
            descriptor.Properties.Count(p => p.IsKey) == 1,
            $"keys=[{string.Join(", ", descriptor.Properties.Where(p => p.IsKey).Select(p => p.Name))}]",
            Requirements.DeclExactlyOneKeyProperty));

        // IVC-DECL-003: the key property must be typed UUID. Asserted directly from the
        // descriptor the driver reported, the same way IVC-REL-010's foreign-key typing clause
        // is asserted below rather than deferred to server-side enforcement the harness never
        // observes.
        var keyProperty = descriptor.Properties.FirstOrDefault(p => p.IsKey);
        results.Add(Assertion.From(
            $"{label}: key property is typed UUID",
            keyProperty is not null && keyProperty.ClrType == ClrType.ClrGuid,
            keyProperty is not null ? $"clrType={keyProperty.ClrType}" : "no key property declared",
            Requirements.DeclKeyTypedUuid));

        // Asserted unconditionally — including (and especially) when Relations is empty. Every
        // other relation assertion below lives inside `foreach (var relation in
        // descriptor.Relations)`, so a client that silently drops all its relations previously
        // ran zero loop iterations and emitted zero relation assertions: a fully green result
        // that proved nothing about the relation shape the scenario exists to check. This is the
        // one relation assertion that fires regardless of how many relations were reported.
        //
        // `expectedRelationKinds` is null only from call sites that genuinely have no
        // expectation to check (kept solely so pre-existing unit tests compile); every real
        // scenario call site passes an explicit collection, including the empty one for
        // "author"/"tag".
        if (expectedRelationKinds is not null)
        {
            var actualKinds = descriptor.Relations.Select(r => r.Kind).OrderBy(k => k).ToList();
            var expectedKinds = expectedRelationKinds.OrderBy(k => k).ToList();
            results.Add(Assertion.From(
                $"{label}: declares exactly the expected relation kinds",
                actualKinds.SequenceEqual(expectedKinds),
                $"expected=[{string.Join(", ", expectedKinds)}] actual=[{string.Join(", ", actualKinds)}]"));
        }

        // Foreign keys of many-to-many relations — the only relations whose key is allowed (and
        // required) to be an array-typed property.
        var manyToManyForeignKeys = descriptor.Relations
            .Where(r => r.Kind == RelationKind.ManyToMany)
            .Select(r => Normalize(r.ForeignKey))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var relation in descriptor.Relations)
        {
            var name = $"{label}.{relation.PropertyName} ({relation.Kind})";

            // IVC-REL-003's statement is unqualified over every relation kind, one_to_many
            // included: the server enforces the nav/FK collision check "for every relation kind
            // including OneToMany" (SchemaRegistrationOrchestrator.cs), so a client that names its
            // one_to_many navigation property identically to its ForeignKey is rejected at
            // registration and must be caught here too, not just for the owning kinds.
            results.Add(Assertion.From(
                $"{name}: nav property is distinct from the foreign key",
                !string.Equals(relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase),
                $"propertyName='{relation.PropertyName}' foreignKey='{relation.ForeignKey}'",
                Requirements.RelNavPropertyDistinctFromForeignKey));

            if (relation.Kind == RelationKind.OneToMany)
            {
                // The foreign key lives on the related type's row; there is nothing on this type
                // to look for. But IVC-REL-001's statement has a negative half too — no foreign
                // key is synthesized for one_to_many at all — and that half needs a real,
                // failable assertion of its own: a client that spuriously synthesizes
                // "{RelatedTypeName}Id" on the declaring type for a one_to_many relation must be
                // caught, not waved through because the loop never looked.
                //
                // That "{RelatedTypeName}Id" shape only catches a client that spuriously names the
                // column after the RELATED type. But for a one_to_many, the foreign key that
                // legitimately belongs to this relation is named after THIS (declaring) type and
                // lives on the related descriptor as `relation.ForeignKey` — e.g. "authorId" is
                // carried by the one_to_many relation on the author descriptor even though the
                // column itself lives on the book row. A client that spuriously materializes THAT
                // column here (on the declaring type) is a distinct wrong shape and must be caught
                // too, not waved through because only the related-type shape was checked.
                var spuriousName = Normalize(relation.RelatedType) + "id";
                var spurious = propertiesByName.ContainsKey(spuriousName);
                var spuriousForeignKeyShape = relation.ForeignKey.Length > 0 &&
                    propertiesByName.ContainsKey(Normalize(relation.ForeignKey));
                results.Add(Assertion.From(
                    $"{name}: no foreign key was synthesized on the declaring type for a one-to-many relation",
                    !spurious && !spuriousForeignKeyShape,
                    spurious
                        ? $"found a spurious '{{RelatedTypeName}}Id'-shaped property for relatedType='{relation.RelatedType}'"
                        : spuriousForeignKeyShape
                            ? $"found a spurious property named after the relation's own foreignKey='{relation.ForeignKey}'"
                            : $"declared properties: [{string.Join(", ", descriptor.Properties.Select(p => p.Name))}]",
                    Requirements.RelForeignKeySynthesizedForOwningKinds));
                continue;
            }

            var declared = propertiesByName.TryGetValue(Normalize(relation.ForeignKey), out var fkProperty);

            results.Add(Assertion.From(
                $"{name}: foreign key '{relation.ForeignKey}' is a declared property",
                relation.ForeignKey.Length > 0 && declared,
                declared
                    ? $"declared as '{fkProperty!.Name}'"
                    : $"declared properties: [{string.Join(", ", descriptor.Properties.Select(p => p.Name))}]",
                Requirements.RelForeignKeySynthesizedForOwningKinds));

            if (relation.Kind is RelationKind.ManyToOne or RelationKind.OneToOne or RelationKind.ManyToMany)
            {
                // The standard's statement is unqualified over every synthesized foreign key,
                // many_to_many included — its array-typed foreign key legitimately pluralizes to
                // "{RelatedTypeName}Ids" (e.g. SchemaRegistrar.java:351 and the other four
                // registrars), so the expected suffix is kind-dependent but the check itself is
                // not scoped away from any relation kind.
                var expectedSuffix = relation.Kind == RelationKind.ManyToMany ? "Ids" : "Id";
                results.Add(Assertion.From(
                    $"{name}: foreign key '{relation.ForeignKey}' is named '{{RelatedTypeName}}{expectedSuffix}'",
                    relation.ForeignKey.Length > 0 &&
                    string.Equals(Normalize(relation.ForeignKey), Normalize(relation.RelatedType) + Normalize(expectedSuffix), StringComparison.Ordinal),
                    $"relatedType='{relation.RelatedType}' foreignKey='{relation.ForeignKey}'",
                    Requirements.RelForeignKeyNamedRelatedTypeId));
            }

            if (relation.Kind is RelationKind.ManyToOne or RelationKind.OneToOne or RelationKind.ManyToMany)
            {
                // IVC-REL-010's second clause — foreign-key columns typed UUID or UUID[] — is
                // asserted here, from the descriptor the driver itself reported, rather than
                // deferred to server-side enforcement the harness never observes. `ClrGuid` is
                // exactly the CLR type the server maps to a `UUID`/`UUID[]` SQL column
                // (`SchemaRegistrationOrchestrator.cs`); the array/scalar split itself is already
                // covered by IVC-REL-004's isArray checks above.
                results.Add(Assertion.From(
                    $"{name}: foreign key '{relation.ForeignKey}' is typed UUID",
                    declared && fkProperty!.ClrType == ClrType.ClrGuid,
                    declared ? $"clrType={fkProperty!.ClrType}" : "foreign key not declared",
                    Requirements.RelForeignKeyWellFormedUuid));
            }

            if (relation.Kind == RelationKind.ManyToMany)
            {
                results.Add(Assertion.From(
                    $"{name}: foreign key '{relation.ForeignKey}' is declared isArray",
                    declared && fkProperty!.IsArray,
                    declared ? $"isArray={fkProperty!.IsArray}" : "foreign key not declared",
                    Requirements.RelIsArraySetForManyToManyOnly));
            }
        }

        foreach (var property in descriptor.Properties.Where(p => p.IsArray))
        {
            results.Add(Assertion.From(
                $"{label}.{property.Name}: isArray is set only for a many-to-many foreign key",
                manyToManyForeignKeys.Contains(Normalize(property.Name)),
                $"many-to-many foreign keys: [{string.Join(", ", descriptor.Relations
                    .Where(r => r.Kind == RelationKind.ManyToMany).Select(r => r.ForeignKey))}]",
                Requirements.RelIsArraySetForManyToManyOnly));

            // IVC-DECL-006: a declaration-level check, independent of IVC-REL-007's wire-level
            // check — a property that is array-typed must never ALSO declare its CLR type as a
            // delimited string.
            results.Add(Assertion.From(
                $"{label}.{property.Name}: array-typed property does not declare CLR_STRING",
                property.ClrType != ClrType.ClrString,
                $"clrType={property.ClrType}",
                Requirements.DeclArrayNotDelimitedString));
        }

        return results;
    }

    /// <summary>
    /// The named set of values S1 compares three ways for one type: the key, plus the foreign key
    /// of every relation whose key lives on this type's own row. Whole-document comparison is
    /// deliberately avoided — the five languages legitimately differ on which fields they
    /// materialize and how they name them.
    /// </summary>
    public static IReadOnlyList<string> ComparedValueNames(TypeDescriptor descriptor)
    {
        var names = new List<string>();

        var key = descriptor.Properties.FirstOrDefault(p => p.IsKey);
        if (key is not null)
            names.Add(key.Name);

        foreach (var relation in descriptor.Relations)
        {
            if (relation.Kind == RelationKind.OneToMany || relation.ForeignKey.Length == 0)
                continue;
            if (!names.Contains(relation.ForeignKey, StringComparer.OrdinalIgnoreCase))
                names.Add(relation.ForeignKey);
        }

        return names;
    }

    // ── Depth-1 hydration ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Judges whether one relation actually hydrated in a depth-1 <c>MappingGet</c> response, as
    /// the class doc comment on <c>CrudRoundtripScenario</c> claims but nothing previously
    /// checked: raising the read depth from 0 to 1 changed no assertion outcome, because the only
    /// values compared were the scalar foreign keys already present at depth 0.
    ///
    /// For <see cref="RelationKind.ManyToOne"/> and <see cref="RelationKind.ManyToMany"/> — whose
    /// key lives on THIS type's own row — two things must both be true: the foreign key must
    /// SURVIVE hydration (still present, not replaced by the nav property), and the nav property
    /// itself must carry the related row(s), identified by each carrying its own key. Every
    /// reference model across all five languages names its key property "Id" (case/separator
    /// aside), so that is the property this checks for on the hydrated related object(s) — it is
    /// never assumed to be any particular per-language type name.
    ///
    /// For <see cref="RelationKind.OneToMany"/> — whose key lives on the RELATED type's row —
    /// there is no foreign key on this row to check; only the nav collection itself, which must
    /// be non-empty.
    /// </summary>
    public static IReadOnlyList<Assertion> VerifyRelationHydrated(
        string label, RelationDescriptor relation, JsonElement? entity)
    {
        var name = $"{label}.{relation.PropertyName} ({relation.Kind})";
        var nav = FindProperty(entity, relation.PropertyName);

        if (relation.Kind == RelationKind.OneToMany)
        {
            var count = CountHydratedObjects(nav);
            return
            [
                Assertion.From(
                    $"{name}: one-to-many nav hydrates at depth 1",
                    count > 0,
                    $"nav={(nav is null ? "(absent)" : nav.Value.GetRawText())}",
                    Requirements.RelOneToManyReverseLookup),
            ];
        }

        var fk = FromJson(entity, relation.ForeignKey);
        var hydratedCount = CountHydratedObjects(nav);

        return
        [
            Assertion.From(
                $"{name}: foreign key '{relation.ForeignKey}' survives hydration",
                fk.Uuids is { Count: > 0 },
                $"foreignKey={fk}",
                Requirements.RelForeignKeyReadableAtDepth),

            Assertion.From(
                $"{name}: nav property hydrates beside the foreign key, carrying the related key",
                hydratedCount > 0,
                $"nav={(nav is null ? "(absent)" : nav.Value.GetRawText())}"),
        ];
    }

    /// <summary>
    /// True when <paramref name="key"/> parses as a UUID whose version nibble reads '7' — the
    /// shape <c>ObjectPersistenceGrpcService.Post</c> mints unconditionally for a mapped create,
    /// discarding whatever key the client sent
    /// (<c>2026-08-10-server-generated-ids-and-mapped-crud-parity-design.md</c>). Used to
    /// discharge <c>IVC-LIFE-002</c> — extracted to a pure, unit-testable function rather than
    /// left inline in <c>CrudRoundtripScenario</c>.
    /// </summary>
    public static bool IsUuidV7(string? key) =>
        key is not null && Guid.TryParse(key, out var parsed) && parsed.ToString("N")[12] == '7';

    /// <summary>
    /// True when <paramref name="key"/> is the all-zeros UUID — the sentinel an unset identifier
    /// property serializes or deserializes to across every driver language, and the literal wire
    /// value .NET's mapped create sends for <c>DotNetArticle.Id</c> (never set on write, so
    /// <c>StructConverter.ToStruct</c> emits its CLR default). Used to discharge the second half
    /// of <c>IVC-LIFE-002</c> ("never a client-supplied one") — extracted to a pure,
    /// unit-testable function rather than left inline in <c>CrudRoundtripScenario</c>, matching
    /// <see cref="IsUuidV7"/>'s pattern.
    /// </summary>
    public static bool IsEmptyKeyPlaceholder(string? key) =>
        Guid.TryParse(key, out var parsed) && parsed == Guid.Empty;

    /// <summary>
    /// Judges IVC-LIFE-006 — the reachability half of the retired IVC-LIFE-005: whether a
    /// driver's own depth-1 read (the "get_depth1" step) is reachable through the client's
    /// public API at all, independent of whether the entity it returns is actually hydrated
    /// (that is IVC-LIFE-008, judged separately by <see cref="VerifyDepthCapability"/>).
    /// Extracted to a pure, unit-testable function — rather than left as an anonymous
    /// invocation of <c>CrudRoundtripScenario.RequireStepOk</c>'s generic step-success check —
    /// so reachability's falsifiability does not rest solely on the live matrix.
    /// </summary>
    public static Assertion VerifyDepthResolvedReadReachable(StepResult? step) =>
        Assertion.From(
            "step 'get_depth1' succeeded",
            step is { Ok: true },
            step is null ? "the driver reported no such step" : step.Error ?? "ok",
            Requirements.LifeDepthResolvedReadReachable);

    /// <summary>
    /// Judges IVC-LIFE-008 — successor of the retired IVC-LIFE-007 (itself the hydration half of
    /// the retired IVC-LIFE-005, itself the successor of IVC-REL-009) — from a DRIVER's own
    /// depth-1 read, never the orchestrator's <c>MappingGet</c>. The orchestrator's own depth-1
    /// read (used by <see cref="VerifyRelationHydrated"/> above) proves the SERVER hydrates; it
    /// says nothing about whether a given client's public API can express the request and
    /// materialize the result. Reachability of the depth-1 read itself is judged separately by
    /// <c>CrudRoundtripScenario.RequireStepOk</c> under IVC-LIFE-006; this method is what makes
    /// hydration a distinct, separately-gradable Behaviour: it requires at least one of the
    /// descriptor's own relations to have actually carried its related object's data (a nav
    /// property carrying an object with its own key) in the JSON the DRIVER itself reported back.
    /// The lookup first tries the registered <c>PropertyName</c> at the reported entity's top
    /// level; when that yields zero hydrated objects, it retries inside the hydration-carrier
    /// property — the well-known member <c>Hydrated</c>, matched through <see cref="Normalize"/>.
    /// The fallback is keyed on the hydrated-object COUNT rather than on the property's absence:
    /// Go's <c>one_to_many</c> declared member sits at top level under exactly the registered
    /// <c>PropertyName</c>, left empty, and would otherwise shadow the carrier entry if the
    /// fallback fired only on absence.
    /// </summary>
    public static Assertion VerifyDepthCapability(string label, TypeDescriptor descriptor, JsonElement? depth1Entity)
    {
        var hydratedRelations = descriptor.Relations
            .Where(r => CountHydratedObjectsForRelation(depth1Entity, r.PropertyName) > 0)
            .Select(r => r.PropertyName)
            .ToList();

        return Assertion.From(
            $"{label}: driver's own depth-1 read reports a hydrated entity",
            hydratedRelations.Count > 0,
            hydratedRelations.Count > 0
                ? $"hydrated: [{string.Join(", ", hydratedRelations)}]"
                : $"entity={(depth1Entity is null ? "(absent)" : depth1Entity.Value.GetRawText())}",
            Requirements.LifeDepthResolvedReadHydrated);
    }

    /// <summary>
    /// The well-known name of the hydration-carrier property Go's driver reports hydrated
    /// children under, since Go's fixed struct fields cannot materialize a navigation member the
    /// model never declared. Matched through <see cref="Normalize"/>, like every other property
    /// lookup in this file.
    /// </summary>
    private const string HydrationCarrierPropertyName = "Hydrated";

    /// <summary>
    /// Counts hydrated objects for one relation, trying the registered <c>PropertyName</c> at the
    /// reported entity's top level first and, only when that yields zero, retrying inside the
    /// hydration-carrier property under the same relation name. Keyed on the COUNT rather than on
    /// the top-level property's absence: Go's <c>one_to_many</c> declared member sits at top level
    /// under exactly <paramref name="propertyName"/>, left at its zero value, and an
    /// absence-keyed fallback would let that empty declared member shadow the carrier entry
    /// instead of falling through to it.
    /// </summary>
    private static int CountHydratedObjectsForRelation(JsonElement? depth1Entity, string propertyName)
    {
        var topLevelCount = CountHydratedObjects(FindProperty(depth1Entity, propertyName));
        if (topLevelCount > 0)
            return topLevelCount;

        var carrier = FindProperty(depth1Entity, HydrationCarrierPropertyName);
        return CountHydratedObjects(FindProperty(carrier, propertyName));
    }

    /// <summary>
    /// Finds a property in a JSON object by normalized name, returning the raw element rather
    /// than canonicalizing it to UUIDs — used where the caller needs to inspect a nested object or
    /// array of objects (a hydrated nav property) rather than a scalar/array-of-scalars FK.
    /// </summary>
    internal static JsonElement? FindProperty(JsonElement? document, string name)
    {
        if (document is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        var normalized = Normalize(name);
        foreach (var property in obj.EnumerateObject())
            if (Normalize(property.Name) == normalized)
                return property.Value;

        return null;
    }

    /// <summary>
    /// Counts objects, each carrying a non-empty "id"-normalized property, found either directly
    /// (a single hydrated many-to-one/one-to-one object) or inside an array (a hydrated
    /// many-to-many or one-to-many collection). An object present but missing its own key, or an
    /// empty array, counts as zero — a nav property that merely EXISTS is not evidence it
    /// hydrated.
    /// </summary>
    private static int CountHydratedObjects(JsonElement? value)
    {
        if (value is not { } element)
            return 0;

        return element.ValueKind switch
        {
            JsonValueKind.Object => HasOwnKey(element) ? 1 : 0,
            JsonValueKind.Array => element.EnumerateArray().Count(HasOwnKey),
            _ => 0,
        };

        static bool HasOwnKey(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in obj.EnumerateObject())
            {
                if (Normalize(property.Name) != "id")
                    continue;
                return property.Value.ValueKind == JsonValueKind.String &&
                       property.Value.GetString() is { Length: > 0 };
            }
            return false;
        }
    }

    // ── Value resolution ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <paramref name="name"/> out of a driver- or server-reported JSON object by
    /// normalized name and canonicalizes it. A missing property and an explicit null both yield
    /// <see cref="ObservedValue.Missing"/>.
    /// </summary>
    public static ObservedValue FromJson(JsonElement? document, string name)
    {
        if (document is not { ValueKind: JsonValueKind.Object } obj)
            return ObservedValue.Missing;

        var normalized = Normalize(name);
        foreach (var property in obj.EnumerateObject())
        {
            if (Normalize(property.Name) != normalized)
                continue;
            return FromJsonValue(property.Value);
        }

        return ObservedValue.Missing;
    }

    private static ObservedValue FromJsonValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return ObservedValue.Missing;

            case JsonValueKind.String:
            {
                var raw = value.GetString() ?? string.Empty;
                if (raw.Length == 0)
                    return ObservedValue.Missing;
                return Guid.TryParse(raw, out var parsed)
                    ? new ObservedValue(raw, [parsed])
                    : ObservedValue.Unreadable(raw);
            }

            case JsonValueKind.Array:
            {
                var uuids = new List<Guid>();
                foreach (var element in value.EnumerateArray())
                {
                    var single = FromJsonValue(element);
                    if (single.Uuids is null)
                        return ObservedValue.Unreadable(value.GetRawText());
                    uuids.AddRange(single.Uuids);
                }
                return new ObservedValue(value.GetRawText(), uuids);
            }

            default:
                return ObservedValue.Unreadable(value.GetRawText());
        }
    }

    /// <summary>
    /// Resolves <paramref name="name"/> out of a Postgres row by normalized column name. Npgsql
    /// hands back <see cref="Guid"/>/<c>Guid[]</c> for uuid columns and strings for text ones;
    /// both are canonicalized the same way. <see cref="DBNull"/> is treated as absent.
    /// </summary>
    public static ObservedValue FromRow(IReadOnlyDictionary<string, object?>? row, string name)
    {
        if (row is null)
            return ObservedValue.Missing;

        var normalized = Normalize(name);
        foreach (var (column, value) in row)
        {
            if (Normalize(column) != normalized)
                continue;
            return FromRowValue(value);
        }

        return ObservedValue.Missing;
    }

    private static ObservedValue FromRowValue(object? value) => value switch
    {
        null or DBNull => ObservedValue.Missing,
        Guid guid => new ObservedValue(guid.ToString(), [guid]),
        string { Length: 0 } => ObservedValue.Missing,
        string text => Guid.TryParse(text, out var parsed)
            ? new ObservedValue(text, [parsed])
            : ObservedValue.Unreadable(text),
        System.Collections.IEnumerable sequence => FromRowSequence(sequence),
        _ => ObservedValue.Unreadable(value.ToString() ?? "?"),
    };

    private static ObservedValue FromRowSequence(System.Collections.IEnumerable sequence)
    {
        var uuids = new List<Guid>();
        var raw = new List<string>();
        foreach (var item in sequence)
        {
            var single = FromRowValue(item);
            raw.Add(single.Raw);
            if (single.Uuids is null)
                return ObservedValue.Unreadable(string.Join(",", raw));
            uuids.AddRange(single.Uuids);
        }
        return new ObservedValue("[" + string.Join(",", raw) + "]", uuids);
    }

    // ── Three-way comparison ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Judges one named value across its three legs and names the disagreeing pair: driver vs
    /// gRPC isolates the client's read path; gRPC vs Postgres isolates the server's read path;
    /// the two agreeing but both empty isolates the write path.
    ///
    /// <paramref name="isKey"/> only changes assertion wording, never comparison behavior. A
    /// relation's foreign key is independently written and read on each leg, so "agrees with" is
    /// accurate for it. The primary key is not: the same identifier is threaded through the
    /// write call, the driver's own read, the orchestrator's gRPC read and the Postgres row — so
    /// what the three legs demonstrate is that the key is ECHOED back unchanged by all three, not
    /// that they independently arrived at the same value.
    /// </summary>
    public static IReadOnlyList<Assertion> VerifyThreeWay(
        string label, string valueName, ThreeLegs legs, bool isKey = false)
    {
        var observed =
            $"driver={legs.Driver} grpc={legs.Grpc} postgres={legs.Postgres}";

        return
        [
            // A value all three legs agree is empty would pass a pure agreement check while
            // certifying nothing — the gRPC leg is the server's own answer, so requiring it to
            // carry a value is what makes the agreement below meaningful.
            //
            // IVC-REL-010 is cited here only for a foreign key, never for the primary key: this
            // assertion fires once per name in ComparedValueNames, which always includes the
            // primary key, so citing REL-010 unconditionally let a type with zero owning
            // relations discharge "foreign-key values are well-formed UUIDs" having observed no
            // foreign key at all. The isKey branch is not left uncited, though — it discharges
            // IVC-DECL-004's "a key value is a well-formed UUID on every leg" instead, so the
            // two requirements partition this assertion's firings rather than one going unused.
            Assertion.From(
                $"{label}.{valueName}: server returned a value",
                legs.Grpc.Uuids is { Count: > 0 },
                observed,
                isKey ? Requirements.DeclKeyWellFormedUuid : Requirements.RelForeignKeyWellFormedUuid),

            // IVC-DECL-004 names all three legs — driver, gRPC, Postgres — so when isKey is true
            // the two agreement assertions below also cite it: Matches returns false whenever
            // either side is unreadable (Verifier.ObservedValue.Matches, above), so a driver leg
            // that failed to parse as a UUID fails this assertion rather than passing by omission,
            // and likewise for Postgres. Citing DECL-004 only on the "server returned a value"
            // assertion above would discharge the requirement having judged only the gRPC leg.
            Assertion.From(
                isKey
                    ? $"{label}.{valueName}: driver-supplied key is echoed unchanged by the orchestrator's gRPC read"
                    : $"{label}.{valueName}: driver read agrees with the orchestrator's gRPC read",
                legs.Driver.Matches(legs.Grpc),
                observed,
                isKey ? Requirements.DeclKeyWellFormedUuid : null),

            Assertion.From(
                isKey
                    ? $"{label}.{valueName}: the orchestrator's gRPC read echoes the same key as the Postgres row"
                    : $"{label}.{valueName}: the orchestrator's gRPC read agrees with the Postgres row",
                legs.Grpc.Matches(legs.Postgres),
                observed,
                isKey ? Requirements.DeclKeyWellFormedUuid : null),
        ];
    }
}
