package io.iverson.client.core;

import com.google.protobuf.Struct;
import com.google.protobuf.Value;
import io.iverson.client.annotations.*;
import org.junit.jupiter.api.Test;

import java.util.Arrays;
import java.util.List;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Unit tests for {@link StructConverter}'s fk-only write contract: a written
 * payload carries foreign key values only, never a relation navigation
 * property, and a collection-typed foreign key (ManyToMany ids) must
 * serialize as a real {@link com.google.protobuf.ListValue}, not a
 * stringified fallback.
 */
class StructConverterTest {

    @IversonEntity
    static class StructTestAuthor {
        @IversonKey
        private UUID id;
        private String name;
    }

    @IversonEntity
    static class StructTestTag {
        @IversonKey
        private UUID id;
        private String label;
    }

    @IversonEntity
    static class StructTestArticle {
        @IversonKey
        private UUID id;
        private String title;

        private UUID authorId;

        @ManyToOne(type = StructTestAuthor.class)
        private StructTestAuthor author;

        private List<UUID> tagIds;

        @ManyToMany(type = StructTestTag.class)
        private List<StructTestTag> tags;

        @ManyToMany(type = StructTestTag.class)
        private List<UUID> someTagIds;
    }

    @IversonEntity
    static class StructTestArrays {
        @IversonKey
        private UUID id;
        // SchemaRegistrar.detectClrType accepts array-typed properties, so the server registers
        // real array columns for these — the converter has to agree on both sides.
        private UUID[] tagIds;
        private int[] scores;
        // byte[] is the exception: detectClrType maps it to CLR_BYTES (a scalar), so it must
        // travel as a base64 string, not as a list.
        private byte[] thumbnail;
    }

    @Test
    void toStruct_arrayTypedProperty_serializesAsListNotStringifiedReference() {
        StructTestArrays entity = new StructTestArrays();
        UUID tagId1 = UUID.randomUUID();
        UUID tagId2 = UUID.randomUUID();
        entity.tagIds = new UUID[] { tagId1, tagId2 };

        Struct struct = StructConverter.toStruct(entity);

        Value tagIds = struct.getFieldsOrThrow("TagIds");
        assertEquals(Value.KindCase.LIST_VALUE, tagIds.getKindCase(),
            "an array-typed FK column must reach the server as a ListValue; a stringified "
            + "\"[Ljava.util.UUID;@...\" would register as an array and then write garbage");
        assertEquals(2, tagIds.getListValue().getValuesCount());
        assertEquals(tagId1.toString(), tagIds.getListValue().getValues(0).getStringValue());
        assertEquals(tagId2.toString(), tagIds.getListValue().getValues(1).getStringValue());
    }

    @Test
    void toStruct_primitiveArrayProperty_serializesAsListOfNumbers() {
        StructTestArrays entity = new StructTestArrays();
        entity.scores = new int[] { 3, 1, 4 };

        Struct struct = StructConverter.toStruct(entity);

        Value scores = struct.getFieldsOrThrow("Scores");
        assertEquals(Value.KindCase.LIST_VALUE, scores.getKindCase());
        assertEquals(3, scores.getListValue().getValuesCount());
        assertEquals(3.0, scores.getListValue().getValues(0).getNumberValue());
        assertEquals(4.0, scores.getListValue().getValues(2).getNumberValue());
    }

    @Test
    void toStruct_byteArrayProperty_serializesAsBase64StringNotAList() {
        StructTestArrays entity = new StructTestArrays();
        entity.thumbnail = new byte[] { 1, 2, 3, (byte) 200 };

        Struct struct = StructConverter.toStruct(entity);

        Value thumb = struct.getFieldsOrThrow("Thumbnail");
        assertEquals(Value.KindCase.STRING_VALUE, thumb.getKindCase(),
            "byte[] is CLR_BYTES, a scalar — it must match the .NET client's base64 encoding");
        assertArrayEquals(
            new byte[] { 1, 2, 3, (byte) 200 },
            java.util.Base64.getDecoder().decode(thumb.getStringValue()));
    }

    @Test
    void fromStruct_arrayTypedProperties_roundTripThroughToStruct() {
        StructTestArrays original = new StructTestArrays();
        original.id = UUID.randomUUID();
        original.tagIds = new UUID[] { UUID.randomUUID(), UUID.randomUUID() };
        original.scores = new int[] { 7, 8 };
        original.thumbnail = new byte[] { 9, (byte) 255 };

        StructTestArrays restored =
            StructConverter.fromStruct(StructConverter.toStruct(original), StructTestArrays.class);

        // Before the array branch existed, each of these came back null: the read side rejected a
        // LIST_VALUE whose target was not a Collection, dropping the column without an error.
        assertArrayEquals(original.tagIds, restored.tagIds);
        assertArrayEquals(original.scores, restored.scores);
        assertArrayEquals(original.thumbnail, restored.thumbnail);
    }

    @Test
    void toStruct_includesForeignKeyFields() {
        StructTestArticle article = new StructTestArticle();
        article.id = UUID.randomUUID();
        article.title = "Hello";
        article.authorId = UUID.randomUUID();
        UUID tagId1 = UUID.randomUUID();
        UUID tagId2 = UUID.randomUUID();
        article.tagIds = List.of(tagId1, tagId2);

        Struct struct = StructConverter.toStruct(article);

        assertTrue(struct.containsFields("AuthorId"));
        assertEquals(article.authorId.toString(), struct.getFieldsOrThrow("AuthorId").getStringValue());

        assertTrue(struct.containsFields("TagIds"));
    }

    @Test
    void toStruct_omitsNavigationProperties() {
        StructTestArticle article = new StructTestArticle();
        article.id = UUID.randomUUID();
        article.title = "Hello";
        article.authorId = UUID.randomUUID();
        StructTestAuthor author = new StructTestAuthor();
        author.id = article.authorId;
        author.name = "Ada";
        article.author = author;

        StructTestTag tag = new StructTestTag();
        tag.id = UUID.randomUUID();
        tag.label = "news";
        article.tags = List.of(tag);
        article.tagIds = List.of(tag.id);

        Struct struct = StructConverter.toStruct(article);

        assertFalse(struct.containsFields("Author"));
        assertFalse(struct.containsFields("Tags"));
    }

    @Test
    void toStruct_serializesForeignKeyCollectionAsListValue_notString() {
        StructTestArticle article = new StructTestArticle();
        article.id = UUID.randomUUID();
        article.title = "Hello";
        UUID tagId1 = UUID.randomUUID();
        UUID tagId2 = UUID.randomUUID();
        article.tagIds = List.of(tagId1, tagId2);

        Struct struct = StructConverter.toStruct(article);

        Value tagIdsValue = struct.getFieldsOrThrow("TagIds");
        assertEquals(Value.KindCase.LIST_VALUE, tagIdsValue.getKindCase(),
            "TagIds must serialize as a ListValue of id strings, not a string");

        List<String> ids = tagIdsValue.getListValue().getValuesList().stream()
            .map(Value::getStringValue)
            .toList();
        assertEquals(List.of(tagId1.toString(), tagId2.toString()), ids);
    }

    @Test
    void fromStruct_deserializesListValueIntoCollectionField() {
        UUID tagId1 = UUID.randomUUID();
        UUID tagId2 = UUID.randomUUID();
        Struct struct = Struct.newBuilder()
            .putFields("TagIds", Value.newBuilder()
                .setListValue(com.google.protobuf.ListValue.newBuilder()
                    .addValues(Value.newBuilder().setStringValue(tagId1.toString()).build())
                    .addValues(Value.newBuilder().setStringValue(tagId2.toString()).build())
                    .build())
                .build())
            .build();

        StructTestArticle article = StructConverter.fromStruct(struct, StructTestArticle.class);

        assertEquals(List.of(tagId1, tagId2), article.tagIds);
    }

    @Test
    void fromStructAsMap_returnsListForArrayColumn() {
        Struct struct = Struct.newBuilder()
            .putFields("Tags", Value.newBuilder()
                .setListValue(com.google.protobuf.ListValue.newBuilder()
                    .addValues(Value.newBuilder().setStringValue("news").build())
                    .addValues(Value.newBuilder().setStringValue("sports").build())
                    .build())
                .build())
            .build();

        var result = StructConverter.fromStructAsMap(struct);

        assertEquals(List.of("news", "sports"), result.get("Tags"));
    }

    @Test
    void fromStruct_deserializesAnnotatedNonEntityCollectionField() {
        UUID tagId1 = UUID.randomUUID();
        UUID tagId2 = UUID.randomUUID();
        Struct struct = Struct.newBuilder()
            .putFields("SomeTagIds", Value.newBuilder()
                .setListValue(com.google.protobuf.ListValue.newBuilder()
                    .addValues(Value.newBuilder().setStringValue(tagId1.toString()).build())
                    .addValues(Value.newBuilder().setStringValue(tagId2.toString()).build())
                    .build())
                .build())
            .build();

        StructTestArticle article = StructConverter.fromStruct(struct, StructTestArticle.class);

        assertEquals(List.of(tagId1, tagId2), article.someTagIds,
            "an annotated FK collection whose element type is not an @IversonEntity must still "
                + "deserialize normally — isNavigationProperty requires both the relation "
                + "annotation AND an entity element type");
    }

    @Test
    void fromStruct_hydratesSingleNavigationPropertyFromNestedStruct() {
        UUID authorId = UUID.randomUUID();
        Struct struct = Struct.newBuilder()
            .putFields("Author", Value.newBuilder()
                .setStructValue(Struct.newBuilder()
                    .putFields("Id", Value.newBuilder().setStringValue(authorId.toString()).build())
                    .putFields("Name", Value.newBuilder().setStringValue("Ada").build())
                    .build())
                .build())
            .build();

        StructTestArticle article = StructConverter.fromStruct(struct, StructTestArticle.class);

        assertNotNull(article.author, "navigation property should be hydrated from a nested struct");
        assertEquals(authorId, article.author.id);
        assertEquals("Ada", article.author.name);
    }

    @Test
    void fromStruct_hydratesCollectionNavigationPropertyFromListOfStructs() {
        UUID tagId1 = UUID.randomUUID();
        UUID tagId2 = UUID.randomUUID();
        Struct struct = Struct.newBuilder()
            .putFields("Tags", Value.newBuilder()
                .setListValue(com.google.protobuf.ListValue.newBuilder()
                    .addValues(Value.newBuilder()
                        .setStructValue(Struct.newBuilder()
                            .putFields("Id", Value.newBuilder().setStringValue(tagId1.toString()).build())
                            .putFields("Label", Value.newBuilder().setStringValue("news").build())
                            .build())
                        .build())
                    .addValues(Value.newBuilder()
                        .setStructValue(Struct.newBuilder()
                            .putFields("Id", Value.newBuilder().setStringValue(tagId2.toString()).build())
                            .putFields("Label", Value.newBuilder().setStringValue("sports").build())
                            .build())
                        .build())
                    .build())
                .build())
            .build();

        StructTestArticle article = StructConverter.fromStruct(struct, StructTestArticle.class);

        assertNotNull(article.tags, "collection navigation property should be hydrated from a list of structs");
        assertEquals(2, article.tags.size());
        assertEquals(List.of(tagId1, tagId2), article.tags.stream().map(t -> t.id).toList());
        assertEquals(List.of("news", "sports"), article.tags.stream().map(t -> t.label).toList());
    }

    // ── I1 regression: struct recursion must not apply to a non-entity target type ──────────

    @IversonEntity
    static class StructTestNonEntityTargetArticle {
        @IversonKey
        private UUID id;

        // Unannotated scalar field whose PascalCase name ("JavaAuthor") collides with the wire
        // key of an unrelated struct-typed payload value. Before the fix, fromValue's
        // STRUCT_VALUE arm called fromStruct(struct, UUID.class) unconditionally, which throws
        // (UUID has no no-arg constructor) and fails the whole read. It must instead yield null,
        // the same as any other struct/target-type mismatch.
        private UUID javaAuthor;
    }

    @Test
    void fromStruct_yieldsNullWhenStructValueTargetsANonEntityType() {
        Struct struct = Struct.newBuilder()
            .putFields("Id", Value.newBuilder().setStringValue(UUID.randomUUID().toString()).build())
            .putFields("JavaAuthor", Value.newBuilder()
                .setStructValue(Struct.newBuilder()
                    .putFields("Id", Value.newBuilder().setStringValue(UUID.randomUUID().toString()).build())
                    .build())
                .build())
            .build();

        StructTestNonEntityTargetArticle result =
            assertDoesNotThrow(() -> StructConverter.fromStruct(struct, StructTestNonEntityTargetArticle.class));

        assertNull(result.javaAuthor,
            "a struct value targeting a non-@IversonEntity field type must fall through to null, not recurse");
    }
}
