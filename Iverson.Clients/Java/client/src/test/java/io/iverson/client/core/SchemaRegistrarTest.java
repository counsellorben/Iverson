package io.iverson.client.core;

import io.grpc.CallOptions;
import io.grpc.Channel;
import io.grpc.ClientCall;
import io.grpc.Metadata;
import io.grpc.MethodDescriptor;
import io.grpc.Status;
import io.iverson.client.annotations.*;
import iverson.ObjectMapping;
import iverson.ObjectMapping.ClrType;
import iverson.ObjectMapping.PropertyDescriptor;
import iverson.ObjectMapping.RelationDescriptor;
import iverson.ObjectMapping.RelationKind;
import iverson.ObjectMapping.GetSchemaRequest;
import iverson.ObjectMapping.GetSchemaResponse;
import iverson.ObjectMapping.SchemaRequest;
import iverson.ObjectMapping.SchemaResponse;
import iverson.ObjectMapping.SchemaType;
import iverson.ObjectMapping.TypeDescriptor;
import iverson.ObjectMappingServiceGrpc;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.ArgumentCaptor;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;

import java.time.OffsetDateTime;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.*;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.*;
import static org.mockito.Mockito.lenient;

/**
 * Unit tests for {@link SchemaRegistrar}. All tests mock the gRPC stub —
 * no live server is required.
 */
@ExtendWith(MockitoExtension.class)
class SchemaRegistrarTest {

    // ── Fixture entities ───────────────────────────────────────────────────────

    @IversonEntity
    static class SearchAnnotationTestEntity {
        @IversonKey
        private UUID id;

        @IversonSearchKey(order = 0)
        private String category;

        @IversonSearchKey(order = 1)
        private OffsetDateTime publishedAt;

        @IversonLargeField
        private String body;

        @IversonEmbedding
        private String title;

        @IversonChunk(maxTokens = 256, overlap = 32)
        private String summary;
    }

    @IversonEntity
    @IversonEmbeddingModel("snowflake-arctic-embed:s")
    static class ModelAnnotationTestEntity {
        @IversonKey
        private UUID id;

        @IversonEmbedding
        @IversonChunk
        private String body;
    }

    // ModelAnnotationTestEntity's both-flags property cannot catch a swapped per-field guard:
    // `if (p.getIsChunk()) p.setModelId(model); if (p.getIsEmbedding()) p.setChunkModelId(model);`
    // stamps body's ModelId and ChunkModelId identically to the correct guards, because body is
    // both flags at once either way. An embedding-ONLY property and a separate chunk-ONLY
    // property are required to make a swap observable — mirrors Python's RegModelAsymmetricArticle
    // and Go's regModelAsymmetricArticle.
    @IversonEntity
    @IversonEmbeddingModel("snowflake-arctic-embed:s")
    static class ModelAsymmetricAnnotationTestEntity {
        @IversonKey
        private UUID id;

        @IversonEmbedding
        private String title;

        @IversonChunk
        private String body;
    }

    @IversonEntity
    static class EnrichmentAnnotationTestEntity {
        @IversonKey
        private UUID id;

        @IversonSummary
        private String summaryField;

        @IversonKeywords
        private String keywordsField;

        @IversonExtracted("Extract the publisher name")
        private String extractedField;

        @IversonChunk(contextual = true)
        private String contextualChunkField;

        private String plainField;
    }

    @IversonEntity
    @IversonDescription("An entity exercising metadata annotations")
    static class MetadataAnnotationTestEntity {
        @IversonKey
        @IversonDescription("The primary key")
        private UUID id;

        @IversonMetadata
        private String source;

        @IversonDescription("Where the article was published")
        private String outlet;

        @IversonMetadata
        @IversonDescription("Language code")
        private String language;

        private String plain;
    }

    @IversonEntity
    static class SchemaTestAuthor {
        @IversonKey
        private UUID id;
        private String name;
        private String bio;   // nullable (String is a reference type)
    }

    @IversonEntity
    static class SchemaTestArticle {
        @IversonKey
        private UUID id;
        private String title;
        private UUID authorId;

        @ManyToOne(type = SchemaTestAuthor.class)
        private SchemaTestAuthor author;

        @OneToMany(type = SchemaTestTag.class)
        private List<SchemaTestTag> tags;
    }

    @IversonEntity
    static class SchemaTestTag {
        @IversonKey
        private UUID id;
        private String label;
        private UUID articleId;
    }

    // Relation declared directly on the foreign-key member: today this makes
    // getPropertyName() == getForeignKey(), which destroys the FK value on depth-resolved reads.
    @IversonEntity
    static class SchemaTestFkNavCollisionEntity {
        @IversonKey
        private UUID id;

        @ManyToOne(type = SchemaTestAuthor.class)
        private UUID schemaTestAuthorId;

        @ManyToMany(type = SchemaTestTag.class)
        private List<UUID> schemaTestTagIds;
    }

    @IversonEntity
    static class ArrayTestEntity {
        @IversonKey
        private UUID id;
        private List<String> tags;
        private String[] labels;
        private byte[] payload;
    }

    // ── Test setup ─────────────────────────────────────────────────────────────

    @Mock
    private ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mockStub;

    private SchemaRegistrar sut;

    @BeforeEach
    void setUp() {
        SchemaResponse successResponse = SchemaResponse.newBuilder()
            .setSuccess(true)
            .build();
        // lenient: some tests throw before reaching registerSchema (e.g. missing @IversonEntity)
        lenient().when(mockStub.registerSchema(any())).thenReturn(successResponse);
        sut = new SchemaRegistrar(mockStub);
    }

    // ── registerAll: basic invocation ─────────────────────────────────────────

    @Test
    void registerAll_callsRegisterSchema_oncePerClass() {
        sut.registerAll(SchemaTestAuthor.class, SchemaTestTag.class);
        verify(mockStub, times(2)).registerSchema(any());
    }

    @Test
    void registerAll_sendsCorrectTypeName_forEachEntity() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SchemaTestAuthor.class, SchemaTestArticle.class, SchemaTestTag.class);

        verify(mockStub, times(3)).registerSchema(captor.capture());
        List<String> typeNames = captor.getAllValues().stream()
            .map(r -> r.getRootType().getTypeName())
            .toList();
        assertTrue(typeNames.contains("SchemaTestAuthor"));
        assertTrue(typeNames.contains("SchemaTestArticle"));
        assertTrue(typeNames.contains("SchemaTestTag"));
    }

    // ── registerAll: key property ─────────────────────────────────────────────

    @Test
    void registerAll_marksKeyProperty_withIsKeyTrue() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SchemaTestAuthor.class);

        verify(mockStub).registerSchema(captor.capture());
        SchemaRequest req = captor.getValue();
        PropertyDescriptor keyProp = req.getRootType().getPropertiesList()
            .stream().filter(PropertyDescriptor::getIsKey).findFirst()
            .orElseThrow(() -> new AssertionError("No key property found"));

        assertEquals("Id", keyProp.getName());
        assertEquals(ClrType.CLR_GUID, keyProp.getClrType());
        assertTrue(keyProp.getIsKey());
    }

    @Test
    void registerAll_keyField_appearsExactlyOnce_inPropertiesList() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SchemaTestAuthor.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        List<PropertyDescriptor> idProps = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Id"))
            .toList();

        assertEquals(1, idProps.size(),
            "key field must appear exactly once in the properties list, not once as key and "
                + "once again as a non-key duplicate");
        assertTrue(idProps.get(0).getIsKey());
    }

    // ── registerAll: navigation properties skipped ────────────────────────────

    @Test
    void registerAll_skipsNavigationProperties_fromScalarList() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SchemaTestArticle.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();
        List<String> propNames = typeDesc.getPropertiesList().stream()
            .map(PropertyDescriptor::getName)
            .toList();

        // Nav properties must NOT appear as scalar columns
        assertFalse(propNames.contains("Author"), "Author nav field should be excluded");
        assertFalse(propNames.contains("Tags"),   "Tags nav field should be excluded");

        // FK scalar must be present
        assertTrue(propNames.contains("AuthorId"), "AuthorId scalar FK must be included");
    }

    // ── registerAll: nullable detection ───────────────────────────────────────

    @Test
    void registerAll_nullableReferenceType_isMarkedNullable() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SchemaTestAuthor.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        // 'bio' is String (reference type) → nullable
        PropertyDescriptor bioProp = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Bio"))
            .findFirst()
            .orElseThrow(() -> new AssertionError("Bio property not found"));

        assertTrue(bioProp.getIsNullable(), "String field should be marked nullable");
    }

    // ── registerAll: array detection ──────────────────────────────────────────

    @Test
    void registerAll_listField_registersAsArrayOfElementType() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(ArrayTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor tagsProp = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Tags"))
            .findFirst()
            .orElseThrow(() -> new AssertionError("Tags property not found"));

        assertTrue(tagsProp.getIsArray(), "List<String> field should be marked is_array");
        assertEquals(ClrType.CLR_STRING, tagsProp.getClrType());
    }

    @Test
    void registerAll_arrayField_registersAsArrayOfElementType() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(ArrayTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor labelsProp = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Labels"))
            .findFirst()
            .orElseThrow(() -> new AssertionError("Labels property not found"));

        assertTrue(labelsProp.getIsArray(), "String[] field should be marked is_array");
        assertEquals(ClrType.CLR_STRING, labelsProp.getClrType());
    }

    @Test
    void registerAll_byteArrayField_stillRegistersAsClrBytesScalar() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(ArrayTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor payloadProp = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Payload"))
            .findFirst()
            .orElseThrow(() -> new AssertionError("Payload property not found"));

        assertEquals(ClrType.CLR_BYTES, payloadProp.getClrType());
        assertFalse(payloadProp.getIsArray(), "byte[] must remain the ClrBytes scalar, not an array");
    }

    // ── registerAll: @IversonSearchKey ────────────────────────────────────────

    @Test
    void registerAll_setsIsSearchKey_andSearchKeyOrder_onAnnotatedProperties() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SearchAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor category = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Category"))
            .findFirst().orElseThrow();
        assertTrue(category.getIsSearchKey());
        assertEquals(0, category.getSearchKeyOrder());

        PropertyDescriptor publishedAt = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("PublishedAt"))
            .findFirst().orElseThrow();
        assertTrue(publishedAt.getIsSearchKey());
        assertEquals(1, publishedAt.getSearchKeyOrder());
    }

    // ── registerAll: @IversonLargeField ───────────────────────────────────────

    @Test
    void registerAll_setsIsLargeField_onAnnotatedProperty() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SearchAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor body = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Body"))
            .findFirst().orElseThrow();
        assertTrue(body.getIsLargeField());
    }

    // ── registerAll: @IversonEmbedding / @IversonChunk ────────────────────────

    @Test
    void registerAll_setsIsEmbedding_onAnnotatedProperty() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SearchAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor title = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Title"))
            .findFirst().orElseThrow();
        assertTrue(title.getIsEmbedding());
    }

    @Test
    void registerAll_setsIsChunk_andChunkParams_onAnnotatedProperty() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SearchAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor summary = typeDesc.getPropertiesList().stream()
            .filter(p -> p.getName().equals("Summary"))
            .findFirst().orElseThrow();
        assertTrue(summary.getIsChunk());
        assertEquals(256, summary.getChunkMaxTokens());
        assertEquals(32, summary.getChunkOverlap());
    }

    // ── registerAll: @IversonEmbeddingModel ───────────────────────────────────

    // This is where stamping is falsifiable — the conformance harness's server-side parity check
    // cannot distinguish "the client stamped the declared model" from "the client sent \"\" and
    // the server fell back to the same value", because its fixture declares the deployment default
    // on purpose (single-model conformance environment).
    @Test
    void registerAll_stampsDeclaredEmbeddingModel_onEmbeddingAndChunkProperties() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(ModelAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor body = prop(typeDesc, "Body");
        PropertyDescriptor key = prop(typeDesc, "Id");
        assertTrue(body.getIsEmbedding());
        assertTrue(body.getIsChunk());

        // Neither field of the key property — which is never embedding nor chunk — may pick up
        // the declared model. This is what the per-property getIsEmbedding()/getIsChunk() guards
        // inside the post-pass exist to prevent; without them every property, key included, would
        // be stamped. assertAll so a dropped guard reddens its own assertion here without a prior
        // one hiding it behind a fail-fast stop.
        assertAll(
            () -> assertEquals("snowflake-arctic-embed:s", body.getModelId()),
            () -> assertEquals("snowflake-arctic-embed:s", body.getChunkModelId()),
            () -> assertEquals("", key.getModelId()),
            () -> assertEquals("", key.getChunkModelId()));
    }

    // THE DISCRIMINATING CASE for a swapped per-field guard. The both-flags/neither-flags shape
    // above passes even if `getIsChunk()`/`getIsEmbedding()` are swapped between setModelId and
    // setChunkModelId, because body picks up both stamps under either ordering. Title
    // (embedding-only) and body (chunk-only) on ModelAsymmetricAnnotationTestEntity are what makes
    // a swap observable: under the correct guards title gets ModelId only and body gets
    // ChunkModelId only; under the swap those flip.
    @Test
    void registerAll_stampsDeclaredEmbeddingModel_onlyOnTheMatchingFieldOfAnAsymmetricType() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(ModelAsymmetricAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor title = prop(typeDesc, "Title");
        PropertyDescriptor body = prop(typeDesc, "Body");
        assertTrue(title.getIsEmbedding());
        assertTrue(body.getIsChunk());

        assertAll(
            () -> assertEquals("snowflake-arctic-embed:s", title.getModelId()),
            () -> assertEquals("", title.getChunkModelId()),
            () -> assertEquals("", body.getModelId()),
            () -> assertEquals("snowflake-arctic-embed:s", body.getChunkModelId()));
    }

    // Undeclared types must keep sending "" on BOTH fields. SearchAnnotationTestEntity carries no
    // [IversonEmbeddingModel] and has an [IversonEmbedding] property (title) AND a separate
    // [IversonChunk] property (summary), so this pins ModelId's undeclared arm and ChunkModelId's
    // undeclared arm together — a fixture with only an embedding property would leave
    // ChunkModelId's undeclared default unpinned.
    @Test
    void registerAll_undeclaredType_sendsEmptyModelId_onEmbeddingAndChunkProperties() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SearchAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor title = prop(typeDesc, "Title");
        PropertyDescriptor summary = prop(typeDesc, "Summary");
        assertTrue(title.getIsEmbedding());
        assertTrue(summary.getIsChunk());

        // assertAll: both must be evaluated and reported even when one already failed, so a guard
        // that skips undeclared types entirely reddens both together rather than hiding the second
        // behind the first's fail-fast.
        assertAll(
            () -> assertEquals("", title.getModelId()),
            () -> assertEquals("", summary.getChunkModelId()));
    }

    // ── registerAll: @IversonSummary / @IversonKeywords / @IversonExtracted / contextual chunk ──

    @Test
    void registerAll_setsIsSummaryTarget_onAnnotatedProperty() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(EnrichmentAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        assertTrue(prop(typeDesc, "SummaryField").getIsSummaryTarget());
    }

    @Test
    void registerAll_setsIsKeywordsTarget_onAnnotatedProperty() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(EnrichmentAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        assertTrue(prop(typeDesc, "KeywordsField").getIsKeywordsTarget());
    }

    @Test
    void registerAll_setsExtractHint_onAnnotatedProperty() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(EnrichmentAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        assertEquals("Extract the publisher name", prop(typeDesc, "ExtractedField").getExtractHint());
    }

    @Test
    void registerAll_setsChunkContextual_onAnnotatedProperty() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(EnrichmentAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor contextualChunk = prop(typeDesc, "ContextualChunkField");
        assertTrue(contextualChunk.getIsChunk());
        assertTrue(contextualChunk.getChunkContextual());
    }

    @Test
    void registerAll_defaultsChunkContextual_toFalse_whenNotSpecified() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SearchAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor summary = prop(typeDesc, "Summary");
        assertTrue(summary.getIsChunk());
        assertFalse(summary.getChunkContextual());
    }

    @Test
    void registerAll_unannotatedProperty_hasNoEnrichmentTargets() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(EnrichmentAnnotationTestEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();

        PropertyDescriptor plain = prop(typeDesc, "PlainField");
        assertFalse(plain.getIsSummaryTarget());
        assertFalse(plain.getIsKeywordsTarget());
        assertEquals("", plain.getExtractHint());
        assertFalse(plain.getIsChunk());
        assertFalse(plain.getChunkContextual());
    }

    @Test
    void registerAll_throwsForBlankExtractHint() {
        @IversonEntity
        class BlankHintEntity {
            @IversonKey private UUID id;
            @IversonExtracted("   ")
            private String extractedField;
        }

        IllegalArgumentException ex = assertThrows(IllegalArgumentException.class,
            () -> sut.registerAll(BlankHintEntity.class));
        assertTrue(ex.getMessage().contains("extractedField"));
    }

    @Test
    void registerAll_throwsForEmptyExtractHint() {
        @IversonEntity
        class EmptyHintEntity {
            @IversonKey private UUID id;
            @IversonExtracted("")
            private String extractedField;
        }

        assertThrows(IllegalArgumentException.class,
            () -> sut.registerAll(EmptyHintEntity.class));
    }

    // ── registerAll: @IversonMetadata / @IversonDescription ───────────────────

    private static PropertyDescriptor prop(TypeDescriptor td, String name) {
        return td.getPropertiesList().stream()
            .filter(p -> p.getName().equals(name))
            .findFirst().orElseThrow(() -> new AssertionError(name + " property not found"));
    }

    private TypeDescriptor captureMetadataEntity() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);
        sut.registerAll(MetadataAnnotationTestEntity.class);
        verify(mockStub).registerSchema(captor.capture());
        return captor.getValue().getRootType();
    }

    @Test
    void registerAll_setsIsMetadata_onAnnotatedProperties() {
        TypeDescriptor td = captureMetadataEntity();

        assertTrue(prop(td, "Source").getIsMetadata());
        assertTrue(prop(td, "Language").getIsMetadata());
        assertFalse(prop(td, "Outlet").getIsMetadata());
        assertFalse(prop(td, "Plain").getIsMetadata());
        assertFalse(prop(td, "Id").getIsMetadata());
    }

    @Test
    void registerAll_setsTypeDescription_fromClassLevelAnnotation() {
        TypeDescriptor td = captureMetadataEntity();
        assertEquals("An entity exercising metadata annotations", td.getDescription());
    }

    @Test
    void registerAll_setsPropertyDescription_onAnnotatedProperties() {
        TypeDescriptor td = captureMetadataEntity();

        assertEquals("Where the article was published", prop(td, "Outlet").getDescription());
        assertEquals("Language code", prop(td, "Language").getDescription());
        assertEquals("", prop(td, "Plain").getDescription());
    }

    @Test
    void registerAll_setsPropertyDescription_onKeyProperty() {
        TypeDescriptor td = captureMetadataEntity();

        PropertyDescriptor key = prop(td, "Id");
        assertTrue(key.getIsKey());
        assertEquals("The primary key", key.getDescription());
    }

    // ── registerAll: declarations the server discards on the key field ────────

    @Test
    void registerAll_throwsWhenKeyDeclaresMetadata() {
        @IversonEntity
        class MetadataOnKeyEntity {
            @IversonKey @IversonMetadata private UUID id;
        }

        IllegalArgumentException ex = assertThrows(IllegalArgumentException.class,
            () -> sut.registerAll(MetadataOnKeyEntity.class));
        assertTrue(ex.getMessage().contains(
            "MetadataOnKeyEntity.id is the primary key and also declares"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonMetadata"), ex.getMessage());
        assertTrue(ex.getMessage().contains("silently discarded"), ex.getMessage());
    }

    @Test
    void registerAll_throwsWhenKeyDeclaresSummary() {
        @IversonEntity
        class SummaryOnKeyEntity {
            @IversonKey @IversonSummary private UUID id;
        }

        IllegalArgumentException ex = assertThrows(IllegalArgumentException.class,
            () -> sut.registerAll(SummaryOnKeyEntity.class));
        assertTrue(ex.getMessage().contains(
            "SummaryOnKeyEntity.id is the primary key and also declares"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonSummary"), ex.getMessage());
        assertTrue(ex.getMessage().contains("silently discarded"), ex.getMessage());
    }

    @Test
    void registerAll_namesEveryRejectedKeyDeclarationInOneError() {
        @IversonEntity
        class MultiDeclarationKeyEntity {
            @IversonKey @IversonSearchKey(order = 0) @IversonLargeField @IversonEmbedding
            @IversonChunk @IversonMetadata @IversonSummary @IversonKeywords
            @IversonExtracted("hint")
            private UUID id;
        }

        IllegalArgumentException ex = assertThrows(IllegalArgumentException.class,
            () -> sut.registerAll(MultiDeclarationKeyEntity.class));
        assertTrue(ex.getMessage().contains("@IversonSearchKey"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonLargeField"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonEmbedding"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonChunk"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonMetadata"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonSummary"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonKeywords"), ex.getMessage());
        assertTrue(ex.getMessage().contains("@IversonExtracted"), ex.getMessage());
    }

    @Test
    void registerAll_allowsDescriptionOnKeyField() {
        @IversonEntity
        class DescribedKeyEntity {
            @IversonKey @IversonDescription("Stable identifier.") private UUID id;
        }

        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(DescribedKeyEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        PropertyDescriptor key = prop(captor.getValue().getRootType(), "Id");
        assertTrue(key.getIsKey());
        assertEquals("Stable identifier.", key.getDescription());
    }

    // ── registerAll: relations ─────────────────────────────────────────────────

    @Test
    void registerAll_buildsRelations_withInferredForeignKeys() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SchemaTestArticle.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();
        List<RelationDescriptor> relations = typeDesc.getRelationsList();

        RelationDescriptor manyToOne = relations.stream()
            .filter(r -> r.getKind() == RelationKind.MANY_TO_ONE)
            .findFirst().orElseThrow(() -> new AssertionError("No MANY_TO_ONE relation"));
        assertEquals("Author", manyToOne.getPropertyName());
        assertEquals("SchemaTestAuthor", manyToOne.getRelatedType());
        assertEquals("SchemaTestAuthorId", manyToOne.getForeignKey());

        RelationDescriptor oneToMany = relations.stream()
            .filter(r -> r.getKind() == RelationKind.ONE_TO_MANY)
            .findFirst().orElseThrow(() -> new AssertionError("No ONE_TO_MANY relation"));
        assertEquals("Tags", oneToMany.getPropertyName());
        assertEquals("SchemaTestTag", oneToMany.getRelatedType());
        assertEquals("SchemaTestArticleId", oneToMany.getForeignKey());
    }

    @Test
    void registerAll_derivesDistinctPropertyName_whenRelationDeclaredOnForeignKeyMember() {
        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(SchemaTestFkNavCollisionEntity.class);

        verify(mockStub).registerSchema(captor.capture());
        TypeDescriptor typeDesc = captor.getValue().getRootType();
        List<RelationDescriptor> relations = typeDesc.getRelationsList();

        RelationDescriptor manyToOne = relations.stream()
            .filter(r -> r.getKind() == RelationKind.MANY_TO_ONE)
            .findFirst().orElseThrow(() -> new AssertionError("No MANY_TO_ONE relation"));
        assertEquals("SchemaTestAuthorId", manyToOne.getForeignKey());
        assertNotEquals(manyToOne.getForeignKey(), manyToOne.getPropertyName(),
            "the navigation property name must not collide with the foreign key, or a "
                + "depth-resolved read overwrites the FK value with the hydrated related entity");
        assertEquals("SchemaTestAuthor", manyToOne.getPropertyName());

        RelationDescriptor manyToMany = relations.stream()
            .filter(r -> r.getKind() == RelationKind.MANY_TO_MANY)
            .findFirst().orElseThrow(() -> new AssertionError("No MANY_TO_MANY relation"));
        assertEquals("SchemaTestTagIds", manyToMany.getForeignKey());
        assertNotEquals(manyToMany.getForeignKey(), manyToMany.getPropertyName(),
            "the navigation property name must not collide with the foreign key, or a "
                + "depth-resolved read overwrites the FK value with the hydrated related entity");
        assertEquals("SchemaTestTags", manyToMany.getPropertyName());
    }

    // ── registerAll: per-type authorization rules ──────────────────────────────

    @Test
    void registerAll_attachesPerTypeAuthorizationRules_andLeavesUnlistedTypesUnset() {
        ObjectMapping.AuthorizationRules authorRules = ObjectMapping.AuthorizationRules.newBuilder()
            .setOwnerField("OwnerId")
            .build();
        ObjectMapping.AuthorizationRules tagRules = ObjectMapping.AuthorizationRules.newBuilder()
            .setOwnerField("CreatedBy")
            .build();

        ArgumentCaptor<SchemaRequest> captor = ArgumentCaptor.forClass(SchemaRequest.class);

        sut.registerAll(
            Map.of("SchemaTestAuthor", authorRules, "SchemaTestTag", tagRules),
            SchemaTestAuthor.class, SchemaTestTag.class, SchemaTestArticle.class);

        verify(mockStub, times(3)).registerSchema(captor.capture());

        TypeDescriptor authorDesc = captor.getAllValues().stream()
            .map(SchemaRequest::getRootType)
            .filter(td -> td.getTypeName().equals("SchemaTestAuthor"))
            .findFirst().orElseThrow();
        assertTrue(authorDesc.hasAuthorization());
        assertEquals("OwnerId", authorDesc.getAuthorization().getOwnerField());

        TypeDescriptor tagDesc = captor.getAllValues().stream()
            .map(SchemaRequest::getRootType)
            .filter(td -> td.getTypeName().equals("SchemaTestTag"))
            .findFirst().orElseThrow();
        assertTrue(tagDesc.hasAuthorization());
        assertEquals("CreatedBy", tagDesc.getAuthorization().getOwnerField());

        TypeDescriptor articleDesc = captor.getAllValues().stream()
            .map(SchemaRequest::getRootType)
            .filter(td -> td.getTypeName().equals("SchemaTestArticle"))
            .findFirst().orElseThrow();
        assertFalse(articleDesc.hasAuthorization(),
            "a type absent from the map must register with no authorization rules");
    }

    // ── registerAll: error handling ────────────────────────────────────────────

    @Test
    void registerAll_throwsForNonAnnotatedClass() {
        class NotAnEntity {}
        assertThrows(IllegalArgumentException.class,
            () -> sut.registerAll(NotAnEntity.class));
    }

    @Test
    void registerAll_throwsForClassWithNoKeyField() {
        @IversonEntity
        class NoKey { private String name; }
        assertThrows(Exception.class, () -> sut.registerAll(NoKey.class));
    }

    // ── buildTypeDescriptor: PascalCase field names ────────────────────────────

    // ── IversonClient.getSchema ────────────────────────────────────────────────

    @Test
    void getSchema_issuesGetSchemaCall_andSurfacesReturnedTypes() {
        SchemaType type = SchemaType.newBuilder().setName("Article").build();
        GetSchemaResponse response = GetSchemaResponse.newBuilder().addTypes(type).build();
        when(mockStub.getSchema(any())).thenReturn(response);

        IversonClient client = new IversonClient(mockStub);
        List<SchemaType> types = client.getSchema("trace-123", null);

        assertEquals(1, types.size());
        assertEquals("Article", types.get(0).getName());

        ArgumentCaptor<GetSchemaRequest> captor = ArgumentCaptor.forClass(GetSchemaRequest.class);
        verify(mockStub).getSchema(captor.capture());
        assertEquals("trace-123", captor.getValue().getTraceId());
    }

    /**
     * Channel that answers a canned GetSchemaResponse and records the {@link CallOptions} the
     * stub actually invoked with. A Mockito stub cannot serve here: {@code withOption} is final on
     * {@code AbstractStub} and a mock answers regardless of call options, so it would pass even
     * when the acting-user option is never attached — which is exactly the defect being pinned.
     */
    private static final class CapturingChannel extends Channel {
        CallOptions capturedOptions;
        private final GetSchemaResponse response;

        CapturingChannel(GetSchemaResponse response) { this.response = response; }

        @Override
        public String authority() { return "capturing-channel"; }

        @Override
        @SuppressWarnings("unchecked")
        public <ReqT, RespT> ClientCall<ReqT, RespT> newCall(
                MethodDescriptor<ReqT, RespT> method, CallOptions callOptions) {
            capturedOptions = callOptions;
            return new ClientCall<ReqT, RespT>() {
                private Listener<RespT> listener;
                @Override public void start(Listener<RespT> responseListener, Metadata headers) {
                    this.listener = responseListener;
                }
                @Override public void request(int numMessages) { }
                @Override public void cancel(String message, Throwable cause) { }
                @Override public void sendMessage(ReqT message) { }
                @Override public void halfClose() {
                    listener.onMessage((RespT) response);
                    listener.onClose(Status.OK, new Metadata());
                }
            };
        }
    }

    @Test
    void getSchema_withActingUserToken_attachesTheActingUserCallOption() {
        CapturingChannel channel = new CapturingChannel(GetSchemaResponse.newBuilder()
            .addTypes(SchemaType.newBuilder().setName("Article").build())
            .build());
        IversonClient client = new IversonClient(
            ObjectMappingServiceGrpc.newBlockingStub(channel));

        List<SchemaType> types = client.getSchema("trace-123", "acting-user-jwt");

        assertEquals(1, types.size());
        assertNotNull(channel.capturedOptions, "the stub never issued a call");
        assertEquals(
            "acting-user-jwt",
            channel.capturedOptions.getOption(OAuth2ClientCredentials.ACTING_USER_TOKEN),
            "getSchema must attach the acting-user token as a call option, or "
                + "OAuth2ClientCredentials never emits the x-acting-user-authorization header");
    }

    @Test
    void getSchema_withoutActingUserToken_leavesTheCallOptionUnset() {
        CapturingChannel channel = new CapturingChannel(GetSchemaResponse.getDefaultInstance());
        IversonClient client = new IversonClient(
            ObjectMappingServiceGrpc.newBlockingStub(channel));

        client.getSchema("trace-123", null);

        assertNull(channel.capturedOptions.getOption(OAuth2ClientCredentials.ACTING_USER_TOKEN));
    }

    @Test
    void close_onTestSeamClient_withNoChannel_doesNotThrow() {
        // IversonClient is AutoCloseable and documented for try-with-resources, so the
        // channel-less constructor must not NPE on close.
        assertDoesNotThrow(() -> new IversonClient(mockStub).close());
    }

    @Test
    void buildTypeDescriptor_convertsCamelCase_toPascalCase() {
        @IversonEntity
        class Widget {
            @IversonKey     private String widgetId;
            private String  widgetName;
        }

        TypeDescriptor td = sut.buildTypeDescriptor(Widget.class);
        List<String> names = td.getPropertiesList().stream()
            .map(PropertyDescriptor::getName).toList();

        assertTrue(names.contains("WidgetId"),   "widgetId → WidgetId");
        assertTrue(names.contains("WidgetName"), "widgetName → WidgetName");
    }
}
