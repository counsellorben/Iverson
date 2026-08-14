package io.iverson.client.core;

import com.google.protobuf.Struct;
import com.google.protobuf.Value;
import io.iverson.client.annotations.*;
import org.junit.jupiter.api.Test;

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
        @IversonTenant
        private String tenantId;
        private String name;
    }

    @IversonEntity
    static class StructTestTag {
        @IversonKey
        private UUID id;
        @IversonTenant
        private String tenantId;
        private String label;
    }

    @IversonEntity
    static class StructTestArticle {
        @IversonKey
        private UUID id;
        @IversonTenant
        private String tenantId;
        private String title;

        private UUID authorId;

        @ManyToOne(type = StructTestAuthor.class)
        private StructTestAuthor author;

        private List<UUID> tagIds;

        @ManyToMany(type = StructTestTag.class)
        private List<StructTestTag> tags;
    }

    @Test
    void toStruct_includesForeignKeyFields() {
        StructTestArticle article = new StructTestArticle();
        article.id = UUID.randomUUID();
        article.tenantId = "tenant-1";
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
        article.tenantId = "tenant-1";
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
        article.tenantId = "tenant-1";
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
    void fromStruct_skipsNavigationPropertyLeavingItNull() {
        Struct struct = Struct.newBuilder()
            .putFields("Tags", Value.newBuilder()
                .setListValue(com.google.protobuf.ListValue.newBuilder()
                    .addValues(Value.newBuilder()
                        .setStructValue(Struct.newBuilder()
                            .putFields("Label", Value.newBuilder().setStringValue("news").build())
                            .build())
                        .build())
                    .build())
                .build())
            .build();

        StructTestArticle article = StructConverter.fromStruct(struct, StructTestArticle.class);

        assertNull(article.tags);
    }
}
