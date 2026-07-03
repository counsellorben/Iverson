package io.iverson.client.core;

import io.iverson.client.annotations.*;
import iverson.ObjectMapping;
import iverson.ObjectMapping.ClrType;
import iverson.ObjectMapping.PropertyDescriptor;
import iverson.ObjectMapping.RelationDescriptor;
import iverson.ObjectMapping.RelationKind;
import iverson.ObjectMapping.SchemaRequest;
import iverson.ObjectMapping.SchemaResponse;
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
