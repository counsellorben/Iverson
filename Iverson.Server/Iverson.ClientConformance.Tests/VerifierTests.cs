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
            { "propertyName": "{{prefix}}Author", "kind": "MANY_TO_ONE", "relatedType": "A", "foreignKey": "{{prefix}}_author_id" },
            { "propertyName": "{{prefix}}_tag_ids", "kind": "MANY_TO_MANY", "relatedType": "T", "foreignKey": "{{prefix}}_tag_ids" }
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

    [Fact]
    public void VerifyRegistration_exempts_many_to_many_from_the_distinct_nav_property_rule()
    {
        // The m2m relation above has propertyName == foreignKey by construction. Asserting
        // distinctness there would fail Python, TypeScript, Go and Java on a conforming stack —
        // the server treats the collision as correct (RelationValidator.cs:20-24).
        var results = Verifier.VerifyRegistration("article", FkOnMemberArticle("py"));

        results.Should().NotContain(r =>
            r.Name.Contains("MANY_TO_MANY") && r.Name.Contains("distinct from the foreign key"));
        results.Should().Contain(r => r.Name.Contains("ManyToMany") && r.Name.Contains("declared isArray"));
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
              "properties": [ { "name": "id", "isKey": true }, { "name": "tenantId" } ],
              "relations": [
                { "propertyName": "javaArticles", "kind": "ONE_TO_MANY", "relatedType": "JavaArticle", "foreignKey": "javaAuthorId" }
              ]
            }
            """));

        Verifier.VerifyRegistration("author", descriptor).Where(r => !r.Passed).Should().BeEmpty();
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
