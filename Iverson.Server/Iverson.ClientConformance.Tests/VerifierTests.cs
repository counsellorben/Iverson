using System.Text.Json;
using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.ClientConformance;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class VerifierTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ── descriptor parsing ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDescriptor_reads_the_dotnet_style_descriptor_with_defaults_written_out()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "DotNetArticle",
              "tenantField": "TenantId",
              "properties": [
                { "name": "Id", "clrType": "CLR_GUID", "isKey": true, "isArray": false },
                { "name": "TenantId", "clrType": "CLR_STRING", "isKey": false, "isArray": false },
                { "name": "DotNetAuthorId", "clrType": "CLR_GUID", "isKey": false, "isArray": false },
                { "name": "DotNetTagIds", "clrType": "CLR_GUID", "isKey": false, "isArray": true }
              ],
              "relations": [
                { "propertyName": "DotNetAuthor", "kind": "MANY_TO_ONE", "relatedType": "DotNetAuthor", "foreignKey": "DotNetAuthorId" },
                { "propertyName": "DotNetTags", "kind": "MANY_TO_MANY", "relatedType": "DotNetTag", "foreignKey": "DotNetTagIds" }
              ]
            }
            """));

        descriptor.TypeName.Should().Be("DotNetArticle");
        descriptor.Relations[0].Kind.Should().Be(RelationKind.ManyToOne);
        descriptor.Properties.Single(p => p.Name == "DotNetTagIds").IsArray.Should().BeTrue();
    }

    [Fact]
    public void ParseDescriptor_treats_omitted_proto3_defaults_the_same_as_written_ones()
    {
        // Go's protojson and TypeScript's ts-proto omit default values entirely; .NET, Python and
        // Java write them out. Both must land on the same parsed message or every assertion
        // below would be language-dependent.
        var omitted = Verifier.ParseDescriptor(Json("""
            { "typeName": "GoArticle", "properties": [ { "name": "Id", "isKey": true } ] }
            """));
        var written = Verifier.ParseDescriptor(Json("""
            { "typeName": "GoArticle", "tenantField": "", "relations": [],
              "properties": [ { "name": "Id", "clrType": "CLR_STRING", "isKey": true, "isArray": false } ] }
            """));

        omitted.Should().Be(written);
    }

    // ── registration assertions ──────────────────────────────────────────────────────────────

    private static TypeDescriptor FkOnMemberArticle(string prefix) => Verifier.ParseDescriptor(Json($$"""
        {
          "typeName": "{{prefix}}Article",
          "tenantField": "tenant_id",
          "properties": [
            { "name": "id", "clrType": "CLR_GUID", "isKey": true },
            { "name": "tenant_id", "clrType": "CLR_STRING" },
            { "name": "{{prefix}}_author_id", "clrType": "CLR_GUID" },
            { "name": "{{prefix}}_tag_ids", "clrType": "CLR_GUID", "isArray": true }
          ],
          "relations": [
            { "propertyName": "{{prefix}}Author", "kind": "MANY_TO_ONE", "relatedType": "{{prefix}}_author", "foreignKey": "{{prefix}}_author_id" },
            { "propertyName": "{{prefix}}Tags", "kind": "MANY_TO_MANY", "relatedType": "{{prefix}}_tag", "foreignKey": "{{prefix}}_tag_ids" }
          ]
        }
        """));

    [Fact]
    public void VerifyRegistration_passes_a_conforming_fk_on_the_member_descriptor()
    {
        var results = Verifier.VerifyRegistration("article", FkOnMemberArticle("py"));

        results.Where(r => !r.Passed).Should().BeEmpty();
        results.Should().NotBeEmpty();
    }

    // IVC-REL-001/002/003 all name one_to_one explicitly, but before this test the kind was
    // never exercised by a single unit fixture — requirement-level touch tracking cannot see a
    // gap like that, since a sibling relation kind touching the same const looks identical to a
    // green suite. A conforming one_to_one descriptor must pass all three.
    [Fact]
    public void VerifyRegistration_passes_a_conforming_one_to_one_relation()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Article", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "clrType": "CLR_GUID", "isKey": true }, { "name": "tenant_id" },
                { "name": "detail_id", "clrType": "CLR_GUID" }
              ],
              "relations": [
                { "propertyName": "detail", "kind": "ONE_TO_ONE", "relatedType": "detail", "foreignKey": "detail_id" }
              ]
            }
            """));

        var results = Verifier.VerifyRegistration("article", descriptor);

        results.Where(r => !r.Passed).Should().BeEmpty();
        results.Should().Contain(r => r.Name.Contains("OneToOne") && r.Name.Contains("distinct from the foreign key"));
        results.Should().Contain(r => r.Name.Contains("OneToOne") && r.Name.Contains("is named '{RelatedTypeName}Id'"));
        results.Should().Contain(r => r.Name.Contains("OneToOne") && r.Name.Contains("is a declared property"));
    }

    [Fact]
    public void VerifyRegistration_fails_a_one_to_one_relation_whose_nav_property_equals_its_foreign_key()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Article", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenant_id" },
                { "name": "detail_id", "clrType": "CLR_GUID" }
              ],
              "relations": [
                { "propertyName": "detail_id", "kind": "ONE_TO_ONE", "relatedType": "detail", "foreignKey": "detail_id" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed && r.Name.Contains("OneToOne") && r.Name.Contains("distinct from the foreign key"));
    }

    [Fact]
    public void VerifyRegistration_extends_the_distinct_nav_property_rule_to_many_to_many()
    {
        // 2026-08-15 ruling reversal: many_to_many is no longer exempt from the distinctness
        // check. A conforming descriptor (the fixture above) must carry a nav property distinct
        // from its foreign key for every relation kind, m2m included.
        var results = Verifier.VerifyRegistration("article", FkOnMemberArticle("py"));

        results.Should().Contain(r =>
            r.Name.Contains("ManyToMany") && r.Name.Contains("distinct from the foreign key") && r.Passed);
        results.Should().Contain(r => r.Name.Contains("ManyToMany") && r.Name.Contains("declared isArray"));
    }

    [Fact]
    public void VerifyRegistration_fails_a_many_to_many_whose_nav_property_equals_its_foreign_key()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenant_id" },
                { "name": "tag_ids", "isArray": true }
              ],
              "relations": [
                { "propertyName": "tag_ids", "kind": "MANY_TO_MANY", "relatedType": "T", "foreignKey": "tag_ids" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed && r.Name.Contains("distinct from the foreign key"));
    }

    [Fact]
    public void VerifyRegistration_fails_a_many_to_one_whose_nav_property_equals_its_foreign_key()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenant_id" }, { "name": "author_id" }
              ],
              "relations": [
                { "propertyName": "author_id", "kind": "MANY_TO_ONE", "relatedType": "A", "foreignKey": "author_id" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed && r.Name.Contains("distinct from the foreign key"));
    }

    [Fact]
    public void VerifyRegistration_fails_when_a_many_to_many_foreign_key_is_not_an_array()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenant_id" },
                { "name": "tag_ids", "isArray": false }
              ],
              "relations": [
                { "propertyName": "tags", "kind": "MANY_TO_MANY", "relatedType": "T", "foreignKey": "tag_ids" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed && r.Name.Contains("declared isArray"));
    }

    [Fact]
    public void VerifyRegistration_fails_when_a_non_one_to_many_foreign_key_is_undeclared()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [ { "name": "id", "isKey": true }, { "name": "tenant_id" } ],
              "relations": [
                { "propertyName": "author", "kind": "MANY_TO_ONE", "relatedType": "A", "foreignKey": "author_id" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed && r.Name.Contains("is a declared property"));
    }

    [Fact]
    public void VerifyRegistration_does_not_look_for_a_one_to_many_foreign_key_on_this_type()
    {
        // The author's reverse navigation has no key on its own row at all.
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "JavaAuthor", "tenantField": "tenantId",
              "properties": [ { "name": "id", "clrType": "CLR_GUID", "isKey": true }, { "name": "tenantId" } ],
              "relations": [
                { "propertyName": "javaArticles", "kind": "ONE_TO_MANY", "relatedType": "JavaArticle", "foreignKey": "javaAuthorId" }
              ]
            }
            """));

        Verifier.VerifyRegistration("author", descriptor).Where(r => !r.Passed).Should().BeEmpty();
    }

    // IVC-REL-001's negative clause: a client must synthesize NO foreign key for one_to_many.
    // Before this test (and the assertion it guards) existed, a client that spuriously
    // synthesized "{RelatedTypeName}Id" for a one_to_many relation passed registration green.
    [Fact]
    public void VerifyRegistration_fails_a_one_to_many_relation_with_a_spurious_foreign_key()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "JavaAuthor", "tenantField": "tenantId",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenantId" },
                { "name": "javaArticleId", "clrType": "CLR_GUID" }
              ],
              "relations": [
                { "propertyName": "javaArticles", "kind": "ONE_TO_MANY", "relatedType": "JavaArticle", "foreignKey": "javaAuthorId" }
              ]
            }
            """));

        Verifier.VerifyRegistration("author", descriptor)
            .Should().Contain(r => !r.Passed &&
                r.Name.Contains("no foreign key was synthesized") &&
                r.RequirementId == Requirements.RelForeignKeySynthesizedForOwningKinds);
    }

    // IVC-REL-001's negative clause also has a second wrong shape: a client that spuriously
    // materializes the property named after THIS relation's own ForeignKey (e.g. "javaAuthorId",
    // the name that legitimately belongs on the related JavaArticle row) on the declaring type
    // itself. The "{RelatedTypeName}Id" check above does not catch this — it only looks for a
    // property named after the RELATED type ("javaArticleId"), not this relation's ForeignKey.
    [Fact]
    public void VerifyRegistration_fails_a_one_to_many_relation_with_its_own_foreign_key_name_spuriously_declared()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "JavaAuthor", "tenantField": "tenantId",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenantId" },
                { "name": "javaAuthorId", "clrType": "CLR_GUID" }
              ],
              "relations": [
                { "propertyName": "javaArticles", "kind": "ONE_TO_MANY", "relatedType": "JavaArticle", "foreignKey": "javaAuthorId" }
              ]
            }
            """));

        Verifier.VerifyRegistration("author", descriptor)
            .Should().Contain(r => !r.Passed &&
                r.Name.Contains("no foreign key was synthesized") &&
                r.RequirementId == Requirements.RelForeignKeySynthesizedForOwningKinds);
    }

    // IVC-REL-003's statement is unqualified over every relation kind, one_to_many included: the
    // server enforces the nav/FK collision check for one_to_many too, so a client whose reverse
    // navigation property name collides with its ForeignKey must be caught here, not waved
    // through because round 1 only extended the distinctness check to the owning kinds.
    [Fact]
    public void VerifyRegistration_fails_a_one_to_many_whose_nav_property_equals_its_foreign_key()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "JavaAuthor", "tenantField": "tenantId",
              "properties": [ { "name": "id", "isKey": true }, { "name": "tenantId" } ],
              "relations": [
                { "propertyName": "javaAuthorId", "kind": "ONE_TO_MANY", "relatedType": "JavaArticle", "foreignKey": "javaAuthorId" }
              ]
            }
            """));

        Verifier.VerifyRegistration("author", descriptor)
            .Should().Contain(r => !r.Passed &&
                r.Name.Contains("OneToMany") && r.Name.Contains("distinct from the foreign key") &&
                r.RequirementId == Requirements.RelNavPropertyDistinctFromForeignKey);
    }

    [Fact]
    public void VerifyRegistration_fails_when_a_many_to_many_foreign_key_is_not_named_relatedTypeIds()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenant_id" },
                { "name": "tag_ids", "clrType": "CLR_GUID", "isArray": true }
              ],
              "relations": [
                { "propertyName": "tags", "kind": "MANY_TO_MANY", "relatedType": "wrong_type", "foreignKey": "tag_ids" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed &&
                r.Name.Contains("{RelatedTypeName}Ids") &&
                r.RequirementId == Requirements.RelForeignKeyNamedRelatedTypeId);
    }

    [Fact]
    public void VerifyRegistration_fails_when_an_owning_foreign_key_is_not_typed_uuid()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenant_id" },
                { "name": "author_id", "clrType": "CLR_STRING" }
              ],
              "relations": [
                { "propertyName": "author", "kind": "MANY_TO_ONE", "relatedType": "author", "foreignKey": "author_id" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed &&
                r.Name.Contains("is typed UUID") &&
                r.RequirementId == Requirements.RelForeignKeyWellFormedUuid);
    }

    [Fact]
    public void VerifyRegistration_fails_an_array_property_that_is_not_a_many_to_many_foreign_key()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true }, { "name": "tenant_id" },
                { "name": "author_id", "isArray": true }
              ],
              "relations": [
                { "propertyName": "author", "kind": "MANY_TO_ONE", "relatedType": "A", "foreignKey": "author_id" }
              ]
            }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed && r.Name.Contains("isArray is set only for"));
    }

    [Fact]
    public void VerifyRegistration_fails_when_a_descriptor_with_no_relations_is_expected_to_have_some()
    {
        // A client whose relations silently vanish (e.g. a serialization bug that drops the
        // "relations" array entirely) must not pass registration for a type expected to declare
        // any. Every OTHER relation assertion lives inside `foreach (var relation in
        // descriptor.Relations)`, so with Relations empty none of them fire — this is the one
        // assertion that still catches it.
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Article", "tenantField": "tenant_id",
              "properties": [ { "name": "id", "isKey": true }, { "name": "tenant_id" } ],
              "relations": []
            }
            """));

        var results = Verifier.VerifyRegistration(
            "article", descriptor, [RelationKind.ManyToOne, RelationKind.ManyToMany]);

        results.Should().Contain(r => !r.Passed && r.Name.Contains("expected relation kinds"));
    }

    [Fact]
    public void VerifyRegistration_passes_a_descriptor_with_no_relations_when_none_are_expected()
    {
        // "author" and "tag" genuinely declare zero relations of their own — that must be an
        // asserted, passing shape, not merely an unchecked absence of failures.
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Tag", "tenantField": "tenant_id",
              "properties": [ { "name": "id", "isKey": true }, { "name": "tenant_id" } ],
              "relations": []
            }
            """));

        var results = Verifier.VerifyRegistration("tag", descriptor, []);

        results.Should().Contain(r => r.Passed && r.Name.Contains("expected relation kinds"));
    }

    // Guards the "declares exactly one key property" assertion in isolation. Without this test,
    // hardcoding that assertion to true left the whole suite green — found during Task 11's
    // mutation pass.
    [Fact]
    public void VerifyRegistration_fails_a_descriptor_with_zero_key_properties()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Tag", "tenantField": "tenant_id",
              "properties": [ { "name": "id" }, { "name": "tenant_id" } ],
              "relations": []
            }
            """));

        var results = Verifier.VerifyRegistration("tag", descriptor, []);

        results.Should().Contain(r => !r.Passed && r.Name.Contains("exactly one key property"));
    }

    [Fact]
    public void VerifyRegistration_fails_a_descriptor_with_two_key_properties()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            {
              "typeName": "Tag", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "isKey": true },
                { "name": "id2", "isKey": true },
                { "name": "tenant_id" }
              ],
              "relations": []
            }
            """));

        var results = Verifier.VerifyRegistration("tag", descriptor, []);

        results.Should().Contain(r => !r.Passed && r.Name.Contains("exactly one key property"));
    }

    // ── DECL — declaration ───────────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyRegistration_cites_DECL001_for_the_key_property_count_assertion()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            { "typeName": "Tag", "tenantField": "tenant_id",
              "properties": [ { "name": "id", "clrType": "CLR_GUID", "isKey": true }, { "name": "tenant_id" } ],
              "relations": [] }
            """));

        Verifier.VerifyRegistration("tag", descriptor, [])
            .Should().Contain(r => r.Passed && r.Name.Contains("exactly one key property") &&
                r.RequirementId == Requirements.DeclExactlyOneKeyProperty);
    }

    [Fact]
    public void VerifyRegistration_fails_DECL003_when_the_key_property_is_not_typed_uuid()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            { "typeName": "Tag", "tenantField": "tenant_id",
              "properties": [ { "name": "id", "clrType": "CLR_STRING", "isKey": true }, { "name": "tenant_id" } ],
              "relations": [] }
            """));

        Verifier.VerifyRegistration("tag", descriptor, [])
            .Should().Contain(r => !r.Passed && r.Name.Contains("key property is typed UUID") &&
                r.RequirementId == Requirements.DeclKeyTypedUuid);
    }

    [Fact]
    public void VerifyRegistration_passes_DECL003_when_the_key_property_is_typed_uuid()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            { "typeName": "Tag", "tenantField": "tenant_id",
              "properties": [ { "name": "id", "clrType": "CLR_GUID", "isKey": true }, { "name": "tenant_id" } ],
              "relations": [] }
            """));

        Verifier.VerifyRegistration("tag", descriptor, [])
            .Should().Contain(r => r.Passed && r.Name.Contains("key property is typed UUID"));
    }

    [Fact]
    public void VerifyRegistration_fails_DECL006_when_an_array_typed_property_declares_CLR_STRING()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            { "typeName": "Bad", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "clrType": "CLR_GUID", "isKey": true }, { "name": "tenant_id" },
                { "name": "tag_ids", "clrType": "CLR_STRING", "isArray": true }
              ],
              "relations": [
                { "propertyName": "tags", "kind": "MANY_TO_MANY", "relatedType": "T", "foreignKey": "tag_ids" }
              ] }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => !r.Passed && r.Name.Contains("does not declare CLR_STRING") &&
                r.RequirementId == Requirements.DeclArrayNotDelimitedString);
    }

    [Fact]
    public void VerifyRegistration_passes_DECL006_when_an_array_typed_property_declares_CLR_GUID()
    {
        var descriptor = Verifier.ParseDescriptor(Json("""
            { "typeName": "Good", "tenantField": "tenant_id",
              "properties": [
                { "name": "id", "clrType": "CLR_GUID", "isKey": true }, { "name": "tenant_id" },
                { "name": "tag_ids", "clrType": "CLR_GUID", "isArray": true }
              ],
              "relations": [
                { "propertyName": "tags", "kind": "MANY_TO_MANY", "relatedType": "T", "foreignKey": "tag_ids" }
              ] }
            """));

        Verifier.VerifyRegistration("article", descriptor)
            .Should().Contain(r => r.Passed && r.Name.Contains("does not declare CLR_STRING"));
    }

    [Fact]
    public void VerifyThreeWay_cites_DECL004_for_the_primary_key()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "Id", Legs(id, id, id), isKey: true);

        results.Should().Contain(a => a.Name.Contains("server returned a value") &&
            a.RequirementId == Requirements.DeclKeyWellFormedUuid);
    }

    [Fact]
    public void VerifyThreeWay_does_not_cite_DECL004_for_a_foreign_key()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "AuthorId", Legs(id, id, id), isKey: false);

        results.Should().NotContain(a => a.RequirementId == Requirements.DeclKeyWellFormedUuid);
    }

    /// <summary>
    /// IVC-DECL-004 names all three legs — driver, gRPC, Postgres — so all three of
    /// VerifyThreeWay's assertions must cite it when isKey is true, not just the "server returned
    /// a value" one. Citing only that one would let the requirement report exercised-and-green
    /// having never judged the driver or Postgres legs — the same partial-discharge failure this
    /// fix closes.
    /// </summary>
    [Fact]
    public void VerifyThreeWay_cites_DECL004_on_all_three_assertions_for_the_primary_key()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "Id", Legs(id, id, id), isKey: true);

        results.Should().HaveCount(3);
        results.Should().OnlyContain(a => a.RequirementId == Requirements.DeclKeyWellFormedUuid);
    }

    /// <summary>
    /// The driver-vs-gRPC and gRPC-vs-Postgres assertions cited under IVC-DECL-004 must actually
    /// judge those legs, not merely carry the citation while always passing. An unreadable driver
    /// leg (fails to parse as a UUID) must fail the driver-vs-gRPC assertion specifically — proof
    /// that DECL-004's citation on it discharges a real judgement of the driver leg.
    /// </summary>
    [Fact]
    public void VerifyThreeWay_DECL004_driver_agreement_assertion_fails_when_driver_leg_is_unreadable()
    {
        var grpc = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay(
            "article", "Id", Legs("not-a-uuid", grpc, grpc), isKey: true);

        results.Should().Contain(a =>
            a.Name.Contains("echoed unchanged by the orchestrator's gRPC read") &&
            a.RequirementId == Requirements.DeclKeyWellFormedUuid &&
            !a.Passed);
    }

    /// <summary>
    /// Mirror of the above for the Postgres leg: an unreadable Postgres leg must fail the
    /// gRPC-vs-Postgres assertion cited under DECL-004, proving that citation discharges a real
    /// judgement of the Postgres leg too.
    /// </summary>
    [Fact]
    public void VerifyThreeWay_DECL004_postgres_agreement_assertion_fails_when_postgres_leg_is_unreadable()
    {
        var driverAndGrpc = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay(
            "article", "Id", Legs(driverAndGrpc, driverAndGrpc, "not-a-uuid"), isKey: true);

        results.Should().Contain(a =>
            a.Name.Contains("echoes the same key as the Postgres row") &&
            a.RequirementId == Requirements.DeclKeyWellFormedUuid &&
            !a.Passed);
    }

    // ── LIFE — server-assigned UUIDv7 key ────────────────────────────────────────────────────

    [Fact]
    public void IsUuidV7_true_for_a_version7_uuid() =>
        Verifier.IsUuidV7("0198f1a2-70c1-7abc-9def-0123456789ab").Should().BeTrue();

    [Fact]
    public void IsUuidV7_false_for_a_version4_uuid() =>
        Verifier.IsUuidV7(Guid.NewGuid().ToString()).Should().BeFalse();

    [Fact]
    public void IsUuidV7_false_for_null_or_unparseable() =>
        (Verifier.IsUuidV7(null) || Verifier.IsUuidV7("not-a-uuid")).Should().BeFalse();

    /// <summary>
    /// IVC-LIFE-002's second clause ("never a client-supplied one") is discharged by ruling out
    /// the all-zeros placeholder — the one candidate value actually observable orchestrator-side
    /// (see Requirements.LifeCreateReturnsServerAssignedKey's doc comment). This pins the
    /// predicate itself: a regression that stopped flagging the placeholder would silently widen
    /// what IVC-LIFE-002 accepts.
    /// </summary>
    [Fact]
    public void IsEmptyKeyPlaceholder_true_for_the_all_zeros_guid() =>
        Verifier.IsEmptyKeyPlaceholder("00000000-0000-0000-0000-000000000000").Should().BeTrue();

    [Fact]
    public void IsEmptyKeyPlaceholder_false_for_a_real_key() =>
        Verifier.IsEmptyKeyPlaceholder(Guid.NewGuid().ToString()).Should().BeFalse();

    [Fact]
    public void IsEmptyKeyPlaceholder_false_for_null_or_unparseable() =>
        (Verifier.IsEmptyKeyPlaceholder(null) || Verifier.IsEmptyKeyPlaceholder("not-a-uuid"))
            .Should().BeFalse();

    // ── LIFE — depth reachability ────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyDepthResolvedReadReachable_passes_when_the_drivers_depth1_step_succeeded()
    {
        var step = new StepResult("get_depth1", Ok: true);

        var assertion = Verifier.VerifyDepthResolvedReadReachable(step);

        assertion.Passed.Should().BeTrue();
        assertion.RequirementId.Should().Be(Requirements.LifeDepthResolvedReadReachable);
    }

    [Fact]
    public void VerifyDepthResolvedReadReachable_fails_when_the_drivers_depth1_step_did_not_succeed()
    {
        // The falsifiability case: a driver whose "get_depth1" step reports failure — the client
        // could not even reach a depth-resolved read through its public API — must not be
        // certified as having discharged reachability.
        var step = new StepResult("get_depth1", Ok: false, Error: "not implemented");

        var assertion = Verifier.VerifyDepthResolvedReadReachable(step);

        assertion.Passed.Should().BeFalse();
        assertion.RequirementId.Should().Be(Requirements.LifeDepthResolvedReadReachable);
    }

    [Fact]
    public void VerifyDepthResolvedReadReachable_fails_when_the_step_is_absent()
    {
        var assertion = Verifier.VerifyDepthResolvedReadReachable(null);

        assertion.Passed.Should().BeFalse();
    }

    // ── LIFE — depth capability ──────────────────────────────────────────────────────────────

    private static TypeDescriptor ArticleWithManyToOneAuthor => Verifier.ParseDescriptor(Json("""
        { "typeName": "Article", "tenantField": "tenant_id",
          "properties": [
            { "name": "id", "clrType": "CLR_GUID", "isKey": true }, { "name": "tenant_id" },
            { "name": "author_id", "clrType": "CLR_GUID" }
          ],
          "relations": [
            { "propertyName": "author", "kind": "MANY_TO_ONE", "relatedType": "author", "foreignKey": "author_id" }
          ] }
        """));

    [Fact]
    public void VerifyDepthCapability_passes_when_a_relation_hydrates_in_the_drivers_own_read()
    {
        var authorId = Guid.NewGuid();
        var entity = Json($$"""
            { "id": "a1", "author_id": "{{authorId}}", "author": { "id": "{{authorId}}", "name": "N" } }
            """);

        var assertion = Verifier.VerifyDepthCapability("article", ArticleWithManyToOneAuthor, entity);

        assertion.Passed.Should().BeTrue();
        assertion.RequirementId.Should().Be(Requirements.LifeDepthResolvedReadHydrated);
    }

    [Fact]
    public void VerifyDepthCapability_fails_when_the_drivers_own_read_reports_no_hydrated_relation()
    {
        // The falsifiability case: a driver whose "depth-1" read is stubbed to return the
        // depth-0 entity — the foreign key is present but no nav property carries a related
        // object — must not be certified as having exercised the depth capability.
        var authorId = Guid.NewGuid();
        var entity = Json($$"""{ "id": "a1", "author_id": "{{authorId}}" }""");

        var assertion = Verifier.VerifyDepthCapability("article", ArticleWithManyToOneAuthor, entity);

        assertion.Passed.Should().BeFalse();
        assertion.RequirementId.Should().Be(Requirements.LifeDepthResolvedReadHydrated);
    }

    [Fact]
    public void VerifyDepthCapability_fails_when_the_entity_is_absent()
    {
        var assertion = Verifier.VerifyDepthCapability("article", ArticleWithManyToOneAuthor, null);

        assertion.Passed.Should().BeFalse();
    }

    [Fact]
    public void VerifyDepthCapability_passes_when_a_relation_hydrates_only_inside_the_carrier()
    {
        // Go's fixed struct fields cannot materialize a navigation member the model never
        // declared, so its driver reports the hydrated child inside a well-known carrier
        // property instead of at the entity's top level.
        var authorId = Guid.NewGuid();
        var entity = Json($$"""
            { "id": "a1", "author_id": "{{authorId}}",
              "Hydrated": { "author": { "id": "{{authorId}}", "name": "N" } } }
            """);

        var assertion = Verifier.VerifyDepthCapability("article", ArticleWithManyToOneAuthor, entity);

        assertion.Passed.Should().BeTrue();
        assertion.RequirementId.Should().Be(Requirements.LifeDepthResolvedReadHydrated);
    }

    [Fact]
    public void VerifyDepthCapability_passes_when_the_top_level_property_is_empty_but_the_carrier_holds_the_child()
    {
        // The shadowing case: Go's declared one_to_many member sits at top level under exactly
        // the registered PropertyName, left at its zero value, while the hydrated children sit
        // in the carrier under the same name. A fallback keyed on the top-level property's
        // absence (rather than on its hydrated-object count) would stop here, since the
        // top-level property is PRESENT — just empty — and never reach the carrier. This case
        // must not regress.
        var authorId = Guid.NewGuid();
        var entity = Json($$"""
            { "id": "a1", "author_id": "{{authorId}}",
              "author": null,
              "Hydrated": { "author": { "id": "{{authorId}}", "name": "N" } } }
            """);

        var assertion = Verifier.VerifyDepthCapability("article", ArticleWithManyToOneAuthor, entity);

        assertion.Passed.Should().BeTrue();
        assertion.RequirementId.Should().Be(Requirements.LifeDepthResolvedReadHydrated);
    }

    [Fact]
    public void VerifyDepthCapability_fails_when_the_relation_is_absent_from_both_top_level_and_carrier()
    {
        var authorId = Guid.NewGuid();
        var entity = Json($$"""
            { "id": "a1", "author_id": "{{authorId}}", "Hydrated": { } }
            """);

        var assertion = Verifier.VerifyDepthCapability("article", ArticleWithManyToOneAuthor, entity);

        assertion.Passed.Should().BeFalse();
        assertion.RequirementId.Should().Be(Requirements.LifeDepthResolvedReadHydrated);
    }

    // ── compared value set ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ComparedValueNames_is_the_key_plus_every_foreign_key_on_this_row()
    {
        Verifier.ComparedValueNames(FkOnMemberArticle("ts"))
            .Should().Equal("id", "ts_author_id", "ts_tag_ids");
    }

    // ── field resolution ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("py_author_id")]
    [InlineData("pyAuthorId")]
    [InlineData("PyAuthorId")]
    [InlineData("PY-AUTHOR-ID")]
    public void FromJson_resolves_a_field_regardless_of_case_or_separators(string spelling)
    {
        var id = Guid.NewGuid();
        var document = Json($$"""{ "{{spelling}}": "{{id}}" }""");

        Verifier.FromJson(document, "PyAuthorId").Uuids.Should().Equal(id);
    }

    [Fact]
    public void FromJson_treats_an_absent_field_and_an_explicit_null_identically()
    {
        // Java's Gson omits null fields where the other four emit them explicitly; any check
        // that told these apart would fail for Java only.
        var absent = Verifier.FromJson(Json("""{ "other": "x" }"""), "authorId");
        var nulled = Verifier.FromJson(Json("""{ "authorId": null }"""), "authorId");

        absent.Should().Be(nulled);
        absent.Uuids.Should().BeEmpty();
    }

    [Fact]
    public void FromJson_compares_uuids_parsed_rather_than_as_strings()
    {
        var id = Guid.NewGuid();
        var lower = Verifier.FromJson(Json($$"""{ "id": "{{id.ToString().ToLowerInvariant()}}" }"""), "id");
        var upper = Verifier.FromJson(Json($$"""{ "id": "{{id.ToString().ToUpperInvariant()}}" }"""), "id");
        var braced = Verifier.FromJson(Json($$"""{ "id": "{{id:B}}" }"""), "id");

        lower.Matches(upper).Should().BeTrue();
        lower.Matches(braced).Should().BeTrue();
        lower.Raw.Should().NotBe(upper.Raw, "the three legs are compared parsed, not as strings");
    }

    [Fact]
    public void FromJson_reads_an_array_foreign_key_order_insensitively()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Verifier.FromJson(Json($$"""{ "tagIds": ["{{a}}","{{b}}"] }"""), "tagIds")
            .Matches(Verifier.FromJson(Json($$"""{ "tagIds": ["{{b}}","{{a}}"] }"""), "tagIds"))
            .Should().BeTrue();
    }

    [Fact]
    public void FromJson_marks_a_non_uuid_value_unreadable_rather_than_empty()
    {
        var value = Verifier.FromJson(Json("""{ "id": "not-a-uuid" }"""), "id");

        value.Uuids.Should().BeNull();
        value.Matches(ObservedValue.Missing).Should().BeFalse();
    }

    [Fact]
    public void FromRow_canonicalizes_npgsql_guids_and_guid_arrays()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var row = new Dictionary<string, object?> { ["Id"] = a, ["TagIds"] = new[] { a, b }, ["AuthorId"] = DBNull.Value };

        Verifier.FromRow(row, "id").Uuids.Should().Equal(a);
        Verifier.FromRow(row, "tag_ids").Uuids.Should().BeEquivalentTo(new[] { a, b });
        Verifier.FromRow(row, "authorId").Should().Be(ObservedValue.Missing);
        Verifier.FromRow(null, "id").Should().Be(ObservedValue.Missing);
    }

    // ── depth-1 hydration ────────────────────────────────────────────────────────────────────

    private static RelationDescriptor ManyToOneAuthor => new()
    {
        PropertyName = "PyAuthor", Kind = RelationKind.ManyToOne,
        RelatedType = "PyAuthor", ForeignKey = "py_author_id",
    };

    private static RelationDescriptor ManyToManyTags => new()
    {
        PropertyName = "py_tag_ids", Kind = RelationKind.ManyToMany,
        RelatedType = "PyTag", ForeignKey = "py_tag_ids",
    };

    private static RelationDescriptor OneToManyArticles => new()
    {
        PropertyName = "py_articles", Kind = RelationKind.OneToMany,
        RelatedType = "PyArticle", ForeignKey = "py_author_id",
    };

    [Fact]
    public void VerifyRelationHydrated_passes_a_many_to_one_whose_fk_and_nav_both_hydrate()
    {
        var authorId = Guid.NewGuid();
        var entity = Json($$"""
            { "py_author_id": "{{authorId}}", "PyAuthor": { "id": "{{authorId}}", "name": "A" } }
            """);

        Verifier.VerifyRelationHydrated("article", ManyToOneAuthor, entity)
            .Where(a => !a.Passed).Should().BeEmpty();
    }

    [Fact]
    public void VerifyRelationHydrated_fails_a_many_to_one_when_the_nav_property_is_absent()
    {
        var authorId = Guid.NewGuid();
        var entity = Json($$"""{ "py_author_id": "{{authorId}}" }""");

        Verifier.VerifyRelationHydrated("article", ManyToOneAuthor, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("nav property hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_fails_a_many_to_one_when_the_foreign_key_is_gone_but_the_nav_is_present()
    {
        var authorId = Guid.NewGuid();
        var entity = Json($$"""
            { "PyAuthor": { "id": "{{authorId}}", "name": "A" } }
            """);

        var results = Verifier.VerifyRelationHydrated("article", ManyToOneAuthor, entity);

        results.Should().Contain(a => !a.Passed && a.Name.Contains("survives hydration"));
        results.Should().Contain(a => a.Passed && a.Name.Contains("nav property hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_fails_a_many_to_one_when_the_nav_object_carries_no_key_of_its_own()
    {
        var authorId = Guid.NewGuid();
        var entity = Json($$"""
            { "py_author_id": "{{authorId}}", "PyAuthor": { "name": "A" } }
            """);

        Verifier.VerifyRelationHydrated("article", ManyToOneAuthor, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("nav property hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_passes_a_many_to_many_whose_fk_array_and_nav_array_both_hydrate()
    {
        var tagId = Guid.NewGuid();
        var entity = Json($$"""
            { "py_tag_ids": ["{{tagId}}"] }
            """);
        // The m2m nav property and its foreign key share a name by construction (see
        // VerifyRegistration's m2m exemption), so the server response carries hydrated child
        // objects under that single shared key, replacing the scalar array.
        var hydrated = Json($$"""
            { "py_tag_ids": [ { "id": "{{tagId}}", "name": "T" } ] }
            """);

        // A many-to-many nav hydrates by REPLACING the array-of-ids with an array-of-objects
        // under the same property name — there is no separate scalar FK left beside it once
        // hydrated, since propertyName == foreignKey for m2m. What must still be true is that the
        // hydrated collection carries the related rows' own keys.
        Verifier.VerifyRelationHydrated("article", ManyToManyTags, hydrated)
            .Should().Contain(a => a.Passed && a.Name.Contains("nav property hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_fails_a_many_to_many_whose_array_is_empty()
    {
        var entity = Json("""{ "py_tag_ids": [] }""");

        Verifier.VerifyRelationHydrated("article", ManyToManyTags, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("nav property hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_passes_a_one_to_many_whose_collection_hydrates()
    {
        var articleId = Guid.NewGuid();
        var entity = Json($$"""
            { "py_articles": [ { "id": "{{articleId}}", "title": "hi" } ] }
            """);

        Verifier.VerifyRelationHydrated("author", OneToManyArticles, entity)
            .Where(a => !a.Passed).Should().BeEmpty();
    }

    [Fact]
    public void VerifyRelationHydrated_fails_a_one_to_many_whose_collection_is_absent()
    {
        var entity = Json("""{ "name": "A" }""");

        Verifier.VerifyRelationHydrated("author", OneToManyArticles, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("hydrates at depth 1"));
    }

    [Fact]
    public void VerifyRelationHydrated_fails_a_one_to_many_whose_collection_is_empty()
    {
        var entity = Json("""{ "py_articles": [] }""");

        Verifier.VerifyRelationHydrated("author", OneToManyArticles, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("hydrates at depth 1"));
    }

    // ── three-way comparison ─────────────────────────────────────────────────────────────────

    private static ThreeLegs Legs(string? driver, string? grpc, string? postgres) => new(
        driver is null ? ObservedValue.Missing : Verifier.FromJson(Json($$"""{ "v": "{{driver}}" }"""), "v"),
        grpc is null ? ObservedValue.Missing : Verifier.FromJson(Json($$"""{ "v": "{{grpc}}" }"""), "v"),
        postgres is null ? ObservedValue.Missing : Verifier.FromJson(Json($$"""{ "v": "{{postgres}}" }"""), "v"));

    [Fact]
    public void VerifyThreeWay_passes_when_all_three_legs_agree_on_a_present_value()
    {
        var id = Guid.NewGuid().ToString();

        Verifier.VerifyThreeWay("article", "AuthorId", Legs(id, id, id))
            .Where(a => !a.Passed).Should().BeEmpty();
    }

    [Fact]
    public void VerifyThreeWay_rejects_three_way_agreement_on_nothing()
    {
        // Agreement alone is not evidence: three empty legs agree while certifying nothing.
        Verifier.VerifyThreeWay("article", "AuthorId", Legs(null, null, null))
            .Should().Contain(a => !a.Passed && a.Name.Contains("server returned a value"));
    }

    [Fact]
    public void VerifyThreeWay_names_the_client_read_path_when_only_the_driver_disagrees()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "AuthorId", Legs(Guid.NewGuid().ToString(), id, id));

        results.Should().Contain(a => !a.Passed && a.Name.Contains("driver read agrees"));
        results.Should().NotContain(a => !a.Passed && a.Name.Contains("Postgres row"));
    }

    [Fact]
    public void VerifyThreeWay_names_the_server_read_path_when_only_postgres_disagrees()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "AuthorId", Legs(id, id, Guid.NewGuid().ToString()));

        results.Should().Contain(a => !a.Passed && a.Name.Contains("Postgres row"));
        results.Should().NotContain(a => !a.Passed && a.Name.Contains("driver read agrees"));
    }

    // ── table naming ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DotNetArticle", "dot_net_articles")]
    [InlineData("PyAuthor", "py_authors")]
    [InlineData("GoTag", "go_tags")]
    public void TableName_mirrors_the_servers_own_derivation(string typeName, string expected) =>
        PostgresProbe.TableName(typeName).Should().Be(expected);

    // ── VerifyThreeWay: isKey wording ───────────────────────────────────────────────────────

    [Fact]
    public void VerifyThreeWay_isKey_true_uses_echo_wording_not_agreement_wording()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "Id", Legs(id, id, id), isKey: true);

        results.Should().Contain(a => a.Name.Contains("is echoed"));
        results.Should().NotContain(a => a.Name.Contains("agrees with"));
    }

    [Fact]
    public void VerifyThreeWay_isKey_false_keeps_agreement_wording()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "AuthorId", Legs(id, id, id), isKey: false);

        results.Should().Contain(a => a.Name.Contains("agrees with"));
        results.Should().NotContain(a => a.Name.Contains("is echoed"));
    }

    // IVC-REL-010 must never be discharged by a primary key: ComparedValueNames always includes
    // the key, so a type with zero owning relations would otherwise certify "foreign-key values
    // are well-formed UUIDs" having observed no foreign key at all.
    [Fact]
    public void VerifyThreeWay_does_not_cite_REL010_for_the_primary_key()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "Id", Legs(id, id, id), isKey: true);

        results.Should().NotContain(a => a.RequirementId == Requirements.RelForeignKeyWellFormedUuid);
    }

    [Fact]
    public void VerifyThreeWay_cites_REL010_for_a_foreign_key()
    {
        var id = Guid.NewGuid().ToString();
        var results = Verifier.VerifyThreeWay("article", "AuthorId", Legs(id, id, id), isKey: false);

        results.Should().Contain(a => a.RequirementId == Requirements.RelForeignKeyWellFormedUuid);
    }

    // ── VerifyRelationHydrated: depth-1 hydration ───────────────────────────────────────────

    private static RelationDescriptor Relation(RelationKind kind, string propertyName, string relatedType, string foreignKey) =>
        new() { Kind = kind, PropertyName = propertyName, RelatedType = relatedType, ForeignKey = foreignKey };

    [Fact]
    public void VerifyRelationHydrated_ManyToOne_passes_when_fk_survives_and_nav_carries_the_related_key()
    {
        var relation = Relation(RelationKind.ManyToOne, "author", "PyAuthor", "py_author_id");
        var entity = Json("""
            { "id": "a1", "py_author_id": "11111111-1111-1111-1111-111111111111",
              "author": { "id": "11111111-1111-1111-1111-111111111111", "name": "N" } }
            """);

        Verifier.VerifyRelationHydrated("article", relation, entity)
            .Where(a => !a.Passed).Should().BeEmpty();
    }

    [Fact]
    public void VerifyRelationHydrated_ManyToOne_fails_when_nav_property_is_missing()
    {
        // Mutation: drop the nav property entirely, leaving only the pre-hydration FK — this is
        // exactly the "depth 0/1 make no difference" bug the assertion exists to catch.
        var relation = Relation(RelationKind.ManyToOne, "author", "PyAuthor", "py_author_id");
        var entity = Json("""{ "id": "a1", "py_author_id": "11111111-1111-1111-1111-111111111111" }""");

        Verifier.VerifyRelationHydrated("article", relation, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("nav property hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_ManyToOne_fails_when_foreign_key_does_not_survive_hydration()
    {
        // Mutation: nav hydrates but the FK itself was dropped/blanked by hydration.
        var relation = Relation(RelationKind.ManyToOne, "author", "PyAuthor", "py_author_id");
        var entity = Json("""
            { "id": "a1", "py_author_id": "",
              "author": { "id": "11111111-1111-1111-1111-111111111111", "name": "N" } }
            """);

        Verifier.VerifyRelationHydrated("article", relation, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("survives hydration"));
    }

    [Fact]
    public void VerifyRelationHydrated_ManyToMany_fails_when_nav_array_elements_lack_their_own_key()
    {
        // Mutation: array present but elements are bare FK strings, not hydrated objects.
        var relation = Relation(RelationKind.ManyToMany, "tags", "PyTag", "py_tag_ids");
        var entity = Json("""
            { "id": "a1",
              "py_tag_ids": ["22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"],
              "tags": ["22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"] }
            """);

        Verifier.VerifyRelationHydrated("article", relation, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("nav property hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_ManyToMany_passes_when_nav_array_holds_hydrated_objects()
    {
        var relation = Relation(RelationKind.ManyToMany, "tags", "PyTag", "py_tag_ids");
        var entity = Json("""
            { "id": "a1", "py_tag_ids": ["22222222-2222-2222-2222-222222222222"],
              "tags": [ { "id": "22222222-2222-2222-2222-222222222222", "label": "L" } ] }
            """);

        Verifier.VerifyRelationHydrated("article", relation, entity)
            .Where(a => !a.Passed).Should().BeEmpty();
    }

    [Fact]
    public void VerifyRelationHydrated_OneToMany_fails_when_nav_collection_is_absent_or_empty()
    {
        // Mutation: author's reverse nav never hydrates — the exact production bug this
        // scenario-level assertion exists to catch (S1 previously asserted only pre-hydration
        // scalars, so raising depth 0 → 1 changed no outcome for this relation).
        var relation = Relation(RelationKind.OneToMany, "articles", "PyArticle", "py_author_id");
        var entity = Json("""{ "id": "au1", "name": "N" }""");

        Verifier.VerifyRelationHydrated("author", relation, entity)
            .Should().Contain(a => !a.Passed && a.Name.Contains("one-to-many nav hydrates"));
    }

    [Fact]
    public void VerifyRelationHydrated_OneToMany_passes_when_nav_collection_carries_hydrated_objects()
    {
        var relation = Relation(RelationKind.OneToMany, "articles", "PyArticle", "py_author_id");
        var entity = Json("""
            { "id": "au1", "name": "N",
              "articles": [ { "id": "a1", "title": "T" } ] }
            """);

        Verifier.VerifyRelationHydrated("author", relation, entity)
            .Where(a => !a.Passed).Should().BeEmpty();
    }
}

public class DriverRunnerCommandResolutionTests
{
    [Fact]
    public void ResolveCommand_leaves_a_bare_command_for_PATH_lookup() =>
        DriverRunner.ResolveCommand("go", "/repo/Iverson.Clients/Go").Should().Be("go");

    [Fact]
    public void ResolveCommand_anchors_a_relative_artifact_path_to_the_drivers_working_directory() =>
        // ProcessStartInfo resolves a relative FileName against the CALLING process's directory,
        // not WorkingDirectory, so Go's built binary was reported as a missing toolchain.
        DriverRunner.ResolveCommand("bin/conformance", "/repo/Iverson.Clients/Go")
            .Should().Be(Path.GetFullPath("/repo/Iverson.Clients/Go/bin/conformance"));

    [Fact]
    public void ResolveCommand_leaves_an_absolute_path_untouched() =>
        DriverRunner.ResolveCommand("/usr/bin/java", "/repo").Should().Be("/usr/bin/java");
}
