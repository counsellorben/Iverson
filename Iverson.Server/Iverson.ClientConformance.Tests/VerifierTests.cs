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
