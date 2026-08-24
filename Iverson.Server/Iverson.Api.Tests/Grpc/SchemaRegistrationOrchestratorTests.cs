using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class SchemaRegistrationOrchestratorTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IRecordStoreSchemaManager _schemaManager = Substitute.For<IRecordStoreSchemaManager>();
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly SchemaRegistry _registry;
    private readonly SchemaRegistrationOrchestrator _sut;

    public SchemaRegistrationOrchestratorTests()
    {
        _embedding.Dimension.Returns(768);
        _embedding.ModelId.Returns("nomic-embed-text");
        _registry = new SchemaRegistry(
            new SchemaRegistryRepository(_sql),
            NullLogger<SchemaRegistry>.Instance);
        _sut = new SchemaRegistrationOrchestrator(
            _schemaManager,
            _embedding,
            _registry,
            Substitute.For<IDocumentRerenderQueueRepository>(),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
    }

    private static TypeDescriptor SimpleType(string name, params string[] extraScalars)
    {
        var td = new TypeDescriptor { TypeName = name };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        foreach (var s in extraScalars)
            td.Properties.Add(new PropertyDescriptor { Name = s, ClrType = ClrType.ClrString });
        return td;
    }

    [Fact]
    public async Task RegisterAsync_WithInvalidOwnerField_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name");
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "DoesNotExist" };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // Task 1 of the remove-IversonTenant plan changed all three of the tests below; Task 4 moved
    // them again, as that comment predicted. Before Task 1 the server DERIVED the tenant column
    // from the client's tenant_field, so:
    //   * RegisterAsync_WithMissingTenantField_ThrowsInvalidArgument asserted that a type with no
    //     tenant_field was rejected with InvalidArgument;
    //   * RegisterAsync_WithInvalidTenantField_ThrowsInvalidArgument asserted that a tenant_field
    //     naming an undeclared property was rejected with InvalidArgument;
    //   * RegisterAsync_WithValidTenantField_Registers asserted TenantColumn == "TenantId".
    // Task 1 re-pointed all three at "the declaration is ignored". Task 4 stops ignoring it and
    // REJECTS it, so the two that asserted a successful registration of a tenant_field-carrying
    // descriptor now assert the rejection instead; the third keeps its surviving claim (a client
    // property that happens to be named TenantId is an ordinary scalar) with the declaration gone.

    [Fact]
    public async Task RegisterAsync_WithNoTenantFieldDeclared_Registers_WithTheServerOwnedTenantColumn()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Name", ClrType = ClrType.ClrString });

        var registered = await _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        registered.Should().Contain("Widget");
        _registry.Get("Widget")!.TenantColumn.Should().Be(SchemaDescriptor.TenantColumnName);
    }

    // Task 4 rejection 1 of 3: a non-empty tenant_field on the INBOUND TypeDescriptor.
    [Fact]
    public async Task RegisterAsync_WithADeclaredTenantField_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget", TenantField = "TenantId" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        // The MESSAGE, not just the status: every other guard in RegisterAsync also throws
        // InvalidArgument, so a status-only assertion would pass under any of them.
        ex.Which.Status.Detail.Should().Contain("tenant_field is no longer accepted");
        ex.Which.Status.Detail.Should().Contain("'TenantId'");
        // Nothing was registered — the rejection runs before BuildDescriptor.
        _registry.Get("Widget").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithATenantFieldNamingNothing_ThrowsInvalidArgument()
    {
        // "DoesNotExist" used to fail ValidateFieldReference, then (Task 1) registered silently.
        // It is now rejected for the same reason any other tenant_field is: the field is set at
        // all. Pinning the message proves it is the new rule doing the rejecting and not a
        // resurrected field-reference validation.
        var td = new TypeDescriptor { TypeName = "Widget", TenantField = "DoesNotExist" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Name", ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("tenant_field is no longer accepted");
        ex.Which.Status.Detail.Should().NotContain("does not match any declared scalar property");
    }

    [Fact]
    public async Task RegisterAsync_WithAPropertyNamedTenantId_Registers_AsAnOrdinaryScalar()
    {
        // A client property that merely happens to be called "TenantId" is legal and unremarkable:
        // the reserved name is "__TenantId", not this. It survives as an ordinary scalar and is
        // simply not the tenant boundary.
        var td = SimpleType("Widget", "Name");

        var registered = await _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        registered.Should().Contain("Widget");
        var descriptor = _registry.Get("Widget")!;
        descriptor.TenantColumn.Should().Be(SchemaDescriptor.TenantColumnName);
        descriptor.ScalarColumns.Select(c => c.Name).Should().Contain("TenantId");
    }

    // Task 4 rejection 2 of 3: a declared property/key/foreign key named __TenantId. Ruling 9 —
    // this is a DIFFERENT condition from a populated tenant_field and a guard for one does not
    // imply the other, so each has its own test. Every fixture here sends the name through the
    // proto TypeDescriptor, never by constructing a SchemaDescriptor directly.
    [Fact]
    public async Task RegisterAsync_WithAScalarPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = SchemaDescriptor.TenantColumnName, ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        ex.Which.Status.Detail.Should().StartWith("Property '__TenantId' on 'Widget'");
        // The REMEDY clause, pinned separately from the diagnosis: the actionable half of the
        // message is the only half a caller can do anything with, and every arm shares the same
        // "... reserved server-owned column name." prefix, so an arm wearing another arm's remedy
        // passes every other assertion here.
        ex.Which.Status.Detail.Should().Contain("Rename the property; the server maintains");
        // Without the guard this falls through to ValidateIdentifier, which rejects the leading
        // underscore with a generic message that never names the reservation.
        ex.Which.Status.Detail.Should().NotContain("must start with a letter");
    }

    [Fact]
    public async Task RegisterAsync_WithADifferentlyCasedTenantColumnProperty_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "__tenantid", ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
    }

    [Fact]
    public async Task RegisterAsync_WithAKeyPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor
            { Name = SchemaDescriptor.TenantColumnName, ClrType = ClrType.ClrGuid, IsKey = true });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Key property '__TenantId' on 'Widget'");
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        ex.Which.Status.Detail.Should().Contain("Rename the property; the server maintains");
    }

    [Fact]
    public async Task RegisterAsync_WithARelationForeignKeyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        // The foreign-key arm is the one ValidateIdentifier does NOT cover — relation names are
        // never identifier-checked — so without this guard it reaches the FK lookup and is
        // rejected as "not a declared property", which is a misleading answer.
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Author",
            Kind = Client.Contracts.RelationKind.ManyToOne,
            RelatedType = "Author",
            ForeignKey = SchemaDescriptor.TenantColumnName,
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Relation foreign key '__TenantId' on 'Widget'");
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        ex.Which.Status.Detail.Should().Contain("Rename the property; the server maintains");
        ex.Which.Status.Detail.Should().NotContain("which is not a declared property");
    }

    [Fact]
    public async Task RegisterAsync_WithAnOwnerFieldNamingTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        // Ruling 22. owner_field is the fourth way to address the reserved name and the ONLY one
        // ValidateFieldReference cannot catch: that check runs on the BUILT descriptor, where
        // SchemaBuilder has just injected __TenantId as a TEXT scalar, so the name RESOLVES and
        // passes the string-valued allow-list. Without the guard this type registers cleanly with
        // OwnerField == "__TenantId" — and then RowFieldAuthorizationEvaluator copies that name
        // into the decision, so EnforceWriteAuthorization's create branch force-sets the tenant
        // column and immediately overwrites it with the acting user's owner value.
        var td = SimpleType("Widget", "Name");
        td.Authorization = new Client.Contracts.AuthorizationRules
        {
            OwnerField = SchemaDescriptor.TenantColumnName
        };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Owner field '__TenantId' on 'Widget'");
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        // The REMEDY, pinned: "Rename the property" — the property arm's text — is actively
        // misleading for an owner_field collision, because there is no property of that name to
        // rename; the caller has to re-point owner_field instead. Swapping the two texts passed
        // the entire suite before this assertion existed.
        ex.Which.Status.Detail.Should().Contain("Point owner_field at a property you declared");
        // Pins that the REGISTRATION-TIME guard rejected it, not some downstream field-reference
        // check — ValidateFieldReference would have RESOLVED this name, not rejected it.
        ex.Which.Status.Detail.Should().NotContain("does not match any declared scalar property");
        _registry.Get("Widget").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithARelationNavigationPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        // Ruling 24. The navigation-property name is the FIFTH way to address the reserved name and
        // is covered by nothing else: ValidateIdentifier runs on TypeName and Properties[].Name
        // only, the FK-naming rule constrains ForeignKey only, and RelationCollisionCheck merely
        // compares PropertyName to ForeignKey — so this fixture, whose ForeignKey is the perfectly
        // legal "AuthorId", registered CLEANLY before this guard existed. The nav property is not a
        // column, which is the point: at depth > 0 MaskDisallowedFields strips the tenant column and
        // ResolveRelationsAsync then re-injects the related object under the key __TenantId, putting
        // the reserved name straight back on the wire.
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "AuthorId", ClrType = ClrType.ClrGuid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = SchemaDescriptor.TenantColumnName,
            Kind = Client.Contracts.RelationKind.ManyToOne,
            RelatedType = "Author",
            ForeignKey = "AuthorId",
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Relation navigation property '__TenantId' on 'Widget'");
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        ex.Which.Status.Detail.Should().Contain("Rename the navigation property");
        // Pins that it was the NAV-PROPERTY arm, not the foreign-key arm, that fired — the two now
        // sit in the same loop, and this fixture's foreign key is legal.
        ex.Which.Status.Detail.Should().NotStartWith("Relation foreign key");
        _registry.Get("Widget").Should().BeNull();
    }

    [Theory]
    [InlineData(Client.Contracts.RelationKind.OneToMany)]
    [InlineData(Client.Contracts.RelationKind.ManyToMany)]
    public async Task RegisterAsync_ARelationNavigationPropertyNamedLikeTheTenantColumn_IsRejectedForEveryKind(
        Client.Contracts.RelationKind kind)
    {
        // SURVIVOR X7. The fact above is the only nav-property fixture the guard had, and it is a
        // ManyToOne — so restricting the guard to `r.Kind == ManyToOne` passed the whole suite.
        // That is not hypothetical hygiene: the loop ONE LEVEL UP in the same method is genuinely
        // kind-filtered (`Where(r => r.Kind != OneToMany)`, SchemaRegistrationOrchestrator.cs:101),
        // so "this relation rule applies only to some kinds" is an established local idiom and a
        // plausible future edit.
        //
        // If the mutant became real, the COLLECTION kinds are the dangerous ones:
        // EntityRelationResolver writes `entityStruct.Fields[relation.PropertyName] =
        // Value.ForList(...)` for OneToMany and ManyToMany, so a nav property named __TenantId puts
        // the reserved name back on the wire by exactly the mechanism Ruling 24 exists to close —
        // and the ManyToOne fixture above would stay green throughout.
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "AuthorIds", ClrType = ClrType.ClrGuid, IsArray = true });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = SchemaDescriptor.TenantColumnName,
            Kind = kind,
            RelatedType = "Author",
            ForeignKey = "AuthorIds",
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Relation navigation property '__TenantId' on 'Widget'");
        // The foreign key is legal, so a message from the FK arm would mean the nav-property arm
        // never ran for this kind — which is the survivor's exact shape.
        ex.Which.Status.Detail.Should().NotStartWith("Relation foreign key");
        _registry.Get("Widget").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithAFieldPermissionNamingTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        // The sixth and last name-bearing field on TypeDescriptor, found by the closed enumeration
        // rather than by another review round. Exactly the class ValidateDocumentTemplate already
        // rejects for a FieldPermission naming 'Document': RowFieldAuthorizationEvaluator builds
        // allFields with the tenant column deliberately EXCLUDED, so this permission can never
        // exclude anything — yet it makes `excluded` non-empty, which flips the whole type into
        // field-masking mode. Accepted, it is a restriction the caller declared and the server
        // silently did not apply.
        var td = SimpleType("Widget", "Name");
        td.Authorization = new Client.Contracts.AuthorizationRules
        {
            FieldPermissions =
            {
                new Client.Contracts.FieldPermission
                {
                    FieldName = SchemaDescriptor.TenantColumnName,
                    ReadableRoles = { "admin" },
                },
            },
        };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Field permission '__TenantId' on 'Widget'");
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        ex.Which.Status.Detail.Should().Contain("Point field_name at a property you declared");
        _registry.Get("Widget").Should().BeNull();
    }

    // Both registration guards run over request.RootType AND request.Dependents — the loop at
    // RegisterAsync's head covers both. The two tests below are the DEPENDENT arm: restricting
    // either guard to request.RootType alone passes every other test in this suite.
    [Fact]
    public async Task RegisterAsync_WithADependentDeclaringATenantField_ThrowsInvalidArgument()
    {
        var dependent = SimpleType("Author", "Name");
        dependent.TenantField = "TenantId";

        var request = new SchemaRequest
        {
            RootType = SimpleType("Article", "Title"),
            Dependents = { dependent }
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("tenant_field is no longer accepted");
        ex.Which.Status.Detail.Should().Contain("'Author'");
        // Phase 1 validates every type before any registry write, so a bad dependent registers
        // nothing at all — not even the legal root that precedes it.
        _registry.Get("Article").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithADependentPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        var dependent = SimpleType("Author", "Name");
        dependent.Properties.Add(new PropertyDescriptor
            { Name = SchemaDescriptor.TenantColumnName, ClrType = ClrType.ClrString });

        var request = new SchemaRequest
        {
            RootType = SimpleType("Article", "Title"),
            Dependents = { dependent }
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Property '__TenantId' on 'Author'");
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        ex.Which.Status.Detail.Should().Contain("Rename the property; the server maintains");
        _registry.Get("Article").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithNonStringOwnerFieldSqlType_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Count", ClrType = ClrType.ClrInt32 });
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "Count" };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithOwnerFieldCollidingWithReservedChunkPayloadKey_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Text", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Body", ClrType = ClrType.ClrString, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "Text" }; // "Text".ToCamelCase() == "text"

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithGuidTypedOwnerField_DoesNotThrow()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ClrType = ClrType.ClrGuid });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "OwnerId" };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithSimpleEntity_ReturnsSuccessAndPersistsInRegistry()
    {
        var request = new SchemaRequest { RootType = SimpleType("Tag", "Label") };

        var registered = await _sut.RegisterAsync(request, CancellationToken.None);

        registered.Should().Contain("Tag");
        _registry.Get("Tag").Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithInjectionRelevantTypeName_ThrowsInvalidArgument()
    {
        var request = new SchemaRequest
        {
            RootType = SimpleType("Foo\"; DROP TABLE x; --", "Name")
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithInjectionRelevantPropertyName_ThrowsInvalidArgument()
    {
        var request = new SchemaRequest
        {
            RootType = SimpleType("Widget", "Name\"; DROP TABLE x; --")
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithUnderscoreInTypeName_ThrowsInvalidArgument()
    {
        // Underscores aren't SQL-injection-relevant, but they're excluded per the allow-list
        // design: ToSnakeCase already inserts its own underscores, so accepting caller-supplied
        // ones would let a caller collide with or otherwise manipulate the generated identifier.
        var request = new SchemaRequest
        {
            RootType = SimpleType("Foo_Bar", "Name")
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithNormalAlphanumericNames_DoesNotThrow()
    {
        var request = new SchemaRequest { RootType = SimpleType("Widget2", "Name2") };

        var registered = await _sut.RegisterAsync(request, CancellationToken.None);

        registered.Should().Contain("Widget2");
    }

    [Fact]
    public async Task RegisterAsync_WithManyToOneRelation_DoesNotThrow()
    {
        var td = SimpleType("Comment", "Body");
        td.Properties.Add(new PropertyDescriptor { Name = "ArticleId", ClrType = ClrType.ClrGuid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Article",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "Article",
            ForeignKey   = "ArticleId"
        });

        var registered = await _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        registered.Should().Contain("Comment");
    }

    [Fact]
    public async Task RegisterAsync_RelationForeignKeyNamingTheServerOwnedTenantColumn_IsRejectedAsReserved()
    {
        // RE-POINTED BY TASK 4. Before Task 4 this asserted the message "which is not a declared
        // property", produced by the __TenantId exclusion on the FK lookup (Ruling 8's tenth
        // ScalarColumns site) — whose whole purpose was to stop the caller getting the misleading
        // UUID-retyping message from the SqlType check. Task 4's reserved-name guard runs on the
        // inbound typeDesc, BEFORE BuildDescriptor, so it now answers first and names the
        // reservation outright. The original claim is preserved in the NotContain below: whatever
        // rejects this, it must not be the SqlType error.
        // NOTE: that FK-lookup exclusion is consequently no longer reachable from the registration
        // path — it stays as the twin of RelationValidator's, which IS still reachable via
        // SchemaRegistry.LoadAsync's rehydrated descriptors.
        var td = SimpleType("Comment", "Body");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tenant",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "Tenant",
            ForeignKey   = SchemaDescriptor.TenantColumnName
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("reserved server-owned column name");
        // Pins WHICH rejection fires: without a guard this is the SqlType error instead.
        ex.Which.Status.Detail.Should().NotContain("must be UUID");
    }

    [Fact]
    public async Task RegisterAsync_WithManyToManyRelation_DoesNotThrow()
    {
        var td = new TypeDescriptor { TypeName = "Post" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id",       ClrType = ClrType.ClrGuid,   IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "TagIds",   ClrType = ClrType.ClrGuid,   IsArray = true });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind         = Client.Contracts.RelationKind.ManyToMany,
            RelatedType  = "Tag",
            ForeignKey   = "TagIds"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithDependents_RegistersAllTypes()
    {
        var request = new SchemaRequest
        {
            RootType = SimpleType("Article", "Title"),
            Dependents = { SimpleType("Author", "Name") }
        };

        var registered = await _sut.RegisterAsync(request, CancellationToken.None);

        registered.Should().Contain("Article").And.Contain("Author");
    }

    [Fact]
    public async Task RegisterAsync_SetsVectorDimAndModelId_FromEmbeddingService()
    {
        var typeDesc = new TypeDescriptor { TypeName = "EmbeddableDoc" };
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true
        });
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name = "TenantId", ClrType = ClrType.ClrString
        });
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name    = "Content",
            ClrType = ClrType.ClrString,
            IsEmbedding = true,
            VectorDim   = 0,
            ModelId     = string.Empty
        });

        var request  = new SchemaRequest { RootType = typeDesc };
        await _sut.RegisterAsync(request, CancellationToken.None);

        var schema = _registry.Get("EmbeddableDoc")!;
        schema.VectorFields.Should().ContainSingle();
        schema.VectorFields[0].Dimension.Should().Be(768);
        schema.VectorFields[0].ModelId.Should().Be("nomic-embed-text");
    }

    [Fact]
    public async Task RegisterAsync_WithNonStringEnrichmentTarget_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Count", ClrType = ClrType.ClrInt32, IsSummaryTarget = true });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithKeyTenantOrOwnerAsEnrichmentTarget_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor
            { Name = "TenantId", ClrType = ClrType.ClrString, IsSummaryTarget = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithEnrichmentTargetThatIsAlsoEmbeddingOrChunk_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor
        {
            Name = "Summary", ClrType = ClrType.ClrString, IsSummaryTarget = true,
            IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithEnrichmentTargetsButNoSourceProperty_ThrowsInvalidArgument()
    {
        // Source text is the concatenation of [IversonEmbedding]/[IversonChunk] properties only —
        // an ordinary string property does NOT count as a source, even though it's plain text.
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Summary", ClrType = ClrType.ClrString, IsSummaryTarget = true });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithEnrichmentTargetAndChunkSourceProperty_DoesNotThrow()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Body", ClrType = ClrType.ClrString, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Summary", ClrType = ClrType.ClrString, IsSummaryTarget = true });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithEmptyExtractHint_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Extracted", ClrType = ClrType.ClrString, ExtractHint = "   " });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithManyToOneForeignKeyMatchingNoColumn_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner",
            Kind = Client.Contracts.RelationKind.ManyToOne,
            RelatedType = "User",
            ForeignKey = "OwnerId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Owner").And.Contain("OwnerId")
            .And.Contain("not a declared property");
    }

    [Fact]
    public async Task RegisterAsync_WithOneToManyForeignKeyMatchingNoColumn_DoesNotThrow()
    {
        var td = SimpleType("Widget", "Name");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Children",
            Kind = Client.Contracts.RelationKind.OneToMany,
            RelatedType = "Gadget",
            ForeignKey = "WidgetId" // lives on Gadget's row, not Widget's
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithManyToManyArrayForeignKeyColumn_DoesNotThrow()
    {
        var td = SimpleType("Widget", "Name");
        td.Properties.Add(new PropertyDescriptor
            { Name = "TagIds", ClrType = ClrType.ClrGuid, IsArray = true });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind = Client.Contracts.RelationKind.ManyToMany,
            RelatedType = "Tag",
            ForeignKey = "TagIds"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithWellFormedManyToOneForeignKey_Registers()
    {
        var td = SimpleType("Widget", "Name");
        td.Properties.Add(new PropertyDescriptor { Name = "UserId", ClrType = ClrType.ClrGuid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner",
            Kind = Client.Contracts.RelationKind.ManyToOne,
            RelatedType = "User",
            ForeignKey = "UserId"
        });

        var registered = await _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        registered.Should().Contain("Widget");
    }

    [Fact]
    public async Task RegisterAsync_WithNonUuidKeyColumn_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrString, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Id").And.Contain("UUID");
    }

    [Fact]
    public async Task RegisterAsync_WithNonUuidManyToOneForeignKeyColumn_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name", "UserId");   // UserId is ClrString → TEXT
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "User",
            ForeignKey   = "UserId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("UserId").And.Contain("UUID");
    }

    [Fact]
    public async Task RegisterAsync_WithScalarManyToManyForeignKeyColumn_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name");
        td.Properties.Add(new PropertyDescriptor
            { Name = "TagIds", ClrType = ClrType.ClrGuid });   // UUID, not UUID[]
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind         = Client.Contracts.RelationKind.ManyToMany,
            RelatedType  = "Tag",
            ForeignKey   = "TagIds"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("TagIds").And.Contain("UUID[]");
    }

    [Fact]
    public async Task RegisterAsync_WithOneToManyRelation_DoesNotCheckForeignKeyColumnType()
    {
        // The FK lives on the related type's row; nothing on this type is checked.
        var td = SimpleType("Widget", "Name", "WidgetId");   // WidgetId is ClrString → TEXT
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Children",
            Kind         = Client.Contracts.RelationKind.OneToMany,
            RelatedType  = "Gadget",
            ForeignKey   = "WidgetId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithManyToOneForeignKeyNotNamedAfterRelatedType_ThrowsInvalidArgument()
    {
        // Column exists and is UUID-typed, so it passes membership/type checks — but "OwnerId"
        // does not match the required "{RelatedTypeName}Id" == "AuthorId" for RelatedType "Author".
        var td = SimpleType("Widget", "Name");
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ClrType = ClrType.ClrGuid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "Author",
            ForeignKey   = "OwnerId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Owner").And.Contain("AuthorId");
    }

    [Fact]
    public async Task RegisterAsync_WithManyToManyForeignKeyNotNamedAfterRelatedType_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Post" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id",       ClrType = ClrType.ClrGuid,   IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "LabelIds", ClrType = ClrType.ClrGuid,   IsArray = true });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind         = Client.Contracts.RelationKind.ManyToMany,
            RelatedType  = "Tag",
            ForeignKey   = "LabelIds" // required: "TagIds"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Tags").And.Contain("TagIds");
    }

    [Fact]
    public async Task RegisterAsync_WithOneToManyForeignKeyNamedAfterThisType_DoesNotThrow()
    {
        // The naming rule ({RelatedTypeName}Id) does not apply to OneToMany: its foreign key is
        // "{ThisTypeName}Id" and lives on the related type's row. This is the case the scope
        // split in Step 2 protects.
        var td = SimpleType("Widget", "Name");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Children",
            Kind         = Client.Contracts.RelationKind.OneToMany,
            RelatedType  = "Gadget",
            ForeignKey   = "WidgetId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithManyToOnePropertyNameEqualsForeignKey_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name");
        td.Properties.Add(new PropertyDescriptor { Name = "AuthorId", ClrType = ClrType.ClrGuid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "AuthorId",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "Author",
            ForeignKey   = "AuthorId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("AuthorId").And.Contain("identical to its foreign key");
    }

    [Fact]
    public async Task RegisterAsync_WithOneToManyPropertyNameEqualsForeignKey_ThrowsInvalidArgument()
    {
        // The collision check applies to every relation kind, including OneToMany — the naming
        // check's OneToMany exemption does NOT carry over to the collision check.
        var td = SimpleType("Widget", "Name");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "WidgetId",
            Kind         = Client.Contracts.RelationKind.OneToMany,
            RelatedType  = "Gadget",
            ForeignKey   = "WidgetId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("WidgetId").And.Contain("identical to its foreign key");
    }
}
