package io.iverson.client.core;

import com.google.protobuf.Struct;
import com.google.protobuf.Value;
import io.grpc.StatusRuntimeException;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.search.AggregateBuilder;
import io.iverson.client.search.ChunksBuilder;
import io.iverson.client.search.GroupByBuilder;
import io.iverson.client.search.PipelineBuilder;
import io.iverson.client.search.Query;
import io.iverson.client.search.SimilarBuilder;
import iverson.ObjectMapping;
import iverson.ObjectMappingServiceGrpc;
import iverson.ObjectSearch;
import iverson.ObjectSearchServiceGrpc;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.ArgumentCaptor;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;

import java.util.List;
import java.util.Map;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.*;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.lenient;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

/**
 * Unit tests for {@link EntityCoordinator}'s search-family execution methods
 * (groupBy/aggregate/pipeline/searchSimilar/searchChunks). All tests mock the gRPC search
 * stub — no live server is required. Mocking convention follows
 * {@code SchemaRegistrarTest} (Mockito + a package-private test constructor).
 */
@ExtendWith(MockitoExtension.class)
class EntityCoordinatorTest {

    @IversonEntity
    static class CoordinatorTestArticle {
        @IversonKey
        private UUID id;
        private String title;
    }

    @Mock
    private ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub mockStub;

    @Mock
    private ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mockMappingStub;

    private EntityCoordinator<CoordinatorTestArticle> sut;
    private EntityCoordinator<CoordinatorTestArticle> mappingSut;

    @BeforeEach
    void setUp() {
        // lenient: only the acting-user-token tests exercise withOption
        lenient().when(mockStub.withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), anyString()))
            .thenReturn(mockStub);
        sut = new EntityCoordinator<>(mockStub, CoordinatorTestArticle.class);

        lenient().when(mockMappingStub.withOption(
                eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), anyString()))
            .thenReturn(mockMappingStub);
        mappingSut = new EntityCoordinator<>(new IversonClient(mockMappingStub), CoordinatorTestArticle.class);
    }

    // ── groupBy ─────────────────────────────────────────────────────────────────

    @Test
    void groupBy_streamsRowsAsMaps() {
        ObjectSearch.SearchResponse row = ObjectSearch.SearchResponse.newBuilder()
            .setData(Struct.newBuilder()
                .putFields("Category", Value.newBuilder().setStringValue("tech").build())
                .build())
            .build();
        when(mockStub.groupBy(any())).thenReturn(List.of(row).iterator());

        GroupByBuilder builder = Query.groupBy("CoordinatorTestArticle").keys("Category").countAll("n");
        List<Map<String, Object>> results = sut.groupBy(builder);

        assertEquals(1, results.size());
        assertEquals("tech", results.get(0).get("Category"));
        verify(mockStub).groupBy(any());
    }

    @Test
    void groupBy_withActingUserToken_usesWithOption() {
        when(mockStub.groupBy(any())).thenReturn(List.<ObjectSearch.SearchResponse>of().iterator());

        GroupByBuilder builder = Query.groupBy("CoordinatorTestArticle").keys("Category").countAll("n");
        sut.withActingUser("user-token-123").groupBy(builder);

        verify(mockStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "user-token-123");
    }

    @Test
    void groupBy_withNoActingUserBound_doesNotCallWithOption() {
        when(mockStub.groupBy(any())).thenReturn(List.<ObjectSearch.SearchResponse>of().iterator());

        GroupByBuilder builder = Query.groupBy("CoordinatorTestArticle").keys("Category").countAll("n");
        sut.groupBy(builder);

        verify(mockStub, never()).withOption(any(), any());
    }

    // ── aggregate ───────────────────────────────────────────────────────────────

    @Test
    void aggregate_returnsFullResponse() {
        ObjectSearch.AggregateResponse response = ObjectSearch.AggregateResponse.newBuilder()
            .setTotal(42)
            .build();
        when(mockStub.aggregate(any())).thenReturn(response);

        AggregateBuilder builder = Query.aggregate("CoordinatorTestArticle").countAll("n");
        ObjectSearch.AggregateResponse result = sut.aggregate(builder);

        assertEquals(42, result.getTotal());
        verify(mockStub).aggregate(any());
    }

    @Test
    void aggregate_withActingUserToken_usesWithOption() {
        when(mockStub.aggregate(any())).thenReturn(ObjectSearch.AggregateResponse.getDefaultInstance());

        AggregateBuilder builder = Query.aggregate("CoordinatorTestArticle").countAll("n");
        sut.withActingUser("user-token-456").aggregate(builder);

        verify(mockStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "user-token-456");
    }

    // ── pipeline ────────────────────────────────────────────────────────────────

    @Test
    void pipeline_streamsRowsAsMaps() {
        ObjectSearch.SearchResponse row = ObjectSearch.SearchResponse.newBuilder()
            .setData(Struct.newBuilder()
                .putFields("Total", Value.newBuilder().setNumberValue(7).build())
                .build())
            .build();
        when(mockStub.pipeline(any())).thenReturn(List.of(row).iterator());

        PipelineBuilder builder = Query.pipeline("CoordinatorTestArticle");
        List<Map<String, Object>> results = sut.pipeline(builder);

        assertEquals(1, results.size());
        assertEquals(7.0, (Double) results.get(0).get("Total"), 0.001);
    }

    @Test
    void pipeline_withActingUserToken_usesWithOption() {
        when(mockStub.pipeline(any())).thenReturn(List.<ObjectSearch.SearchResponse>of().iterator());

        PipelineBuilder builder = Query.pipeline("CoordinatorTestArticle");
        sut.withActingUser("user-token-999").pipeline(builder);

        verify(mockStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "user-token-999");
    }

    // ── searchSimilar ───────────────────────────────────────────────────────────

    @Test
    void searchSimilar_returnsTypedEntitiesWithScores() {
        UUID id = UUID.randomUUID();
        ObjectSearch.SearchResponse row = ObjectSearch.SearchResponse.newBuilder()
            .setData(Struct.newBuilder()
                .putFields("Id", Value.newBuilder().setStringValue(id.toString()).build())
                .putFields("Title", Value.newBuilder().setStringValue("Hello").build())
                .build())
            .setScore(0.87f)
            .build();
        when(mockStub.searchSimilar(any())).thenReturn(List.of(row).iterator());

        SimilarBuilder builder = Query.similar("CoordinatorTestArticle", "title").text("hello");
        List<EntityCoordinator.SearchResult<CoordinatorTestArticle>> results = sut.searchSimilar(builder);

        assertEquals(1, results.size());
        assertEquals("Hello", results.get(0).entity().title);
        assertEquals(0.87f, results.get(0).score(), 0.001f);
    }

    @Test
    void searchSimilar_withActingUserToken_usesWithOption() {
        when(mockStub.searchSimilar(any())).thenReturn(List.<ObjectSearch.SearchResponse>of().iterator());

        SimilarBuilder builder = Query.similar("CoordinatorTestArticle", "title").text("hello");
        sut.withActingUser("user-token-321").searchSimilar(builder);

        verify(mockStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "user-token-321");
    }

    // ── searchChunks ────────────────────────────────────────────────────────────

    @Test
    void searchChunks_returnsChunkSearchResults() {
        ObjectSearch.ChunkSearchResponse chunk = ObjectSearch.ChunkSearchResponse.newBuilder()
            .setParentKey("article-1")
            .setChunkText("some passage")
            .setScore(0.91f)
            .build();
        when(mockStub.searchChunks(any())).thenReturn(List.of(chunk).iterator());

        ChunksBuilder builder = Query.chunks("CoordinatorTestArticle", "summary").text("hello");
        List<EntityCoordinator.ChunkSearchResult> results = sut.searchChunks(builder);

        assertEquals(1, results.size());
        assertEquals("article-1", results.get(0).parentKey());
        assertEquals("some passage", results.get(0).chunkText());
        assertEquals(0.91f, results.get(0).score(), 0.001f);
    }

    @Test
    void searchChunks_withActingUserToken_usesWithOption() {
        when(mockStub.searchChunks(any())).thenReturn(List.<ObjectSearch.ChunkSearchResponse>of().iterator());

        ChunksBuilder builder = Query.chunks("CoordinatorTestArticle", "summary").text("hello");
        sut.withActingUser("user-token-789").searchChunks(builder);

        verify(mockStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "user-token-789");
    }

    // ── getMapped / postMapped / updateMapped ──────────────────────────────────

    @Test
    void getMappedPassesDepthThrough() {
        UUID id = UUID.randomUUID();
        ObjectMapping.MappingResponse response = ObjectMapping.MappingResponse.newBuilder()
            .setSuccess(true)
            .setData(Struct.newBuilder()
                .putFields("Id", Value.newBuilder().setStringValue(id.toString()).build())
                .putFields("Title", Value.newBuilder().setStringValue("Hello").build())
                .build())
            .build();
        when(mockMappingStub.get(any())).thenReturn(response);

        CoordinatorTestArticle result = mappingSut.withActingUser("tok").getMapped(id.toString(), 3);

        assertNotNull(result);
        assertEquals("Hello", result.title);

        ArgumentCaptor<ObjectMapping.MappingGetRequest> captor =
            ArgumentCaptor.forClass(ObjectMapping.MappingGetRequest.class);
        verify(mockMappingStub).get(captor.capture());
        assertEquals(3, captor.getValue().getDepth());
        assertEquals(id.toString(), captor.getValue().getKey());
    }

    @Test
    void getMapped_returnsNull_whenNotFound() {
        ObjectMapping.MappingResponse response = ObjectMapping.MappingResponse.newBuilder()
            .setSuccess(false)
            .build();
        when(mockMappingStub.get(any())).thenReturn(response);

        assertNull(mappingSut.withActingUser("tok").getMapped("missing-id", 1));
    }

    @Test
    void postMappedReturnsEntityHydratedFromData() {
        UUID id = UUID.randomUUID();
        ObjectMapping.MappingResponse response = ObjectMapping.MappingResponse.newBuilder()
            .setSuccess(true)
            .setData(Struct.newBuilder()
                .putFields("Id", Value.newBuilder().setStringValue(id.toString()).build())
                .putFields("Title", Value.newBuilder().setStringValue("Server Title").build())
                .build())
            .build();
        when(mockMappingStub.post(any())).thenReturn(response);

        CoordinatorTestArticle entity = new CoordinatorTestArticle();
        entity.title = "Client Title";

        CoordinatorTestArticle result = mappingSut.withActingUser("tok").postMapped(entity);

        assertEquals(id, result.id);
        assertEquals("Server Title", result.title);

        verify(mockMappingStub).withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), eq("tok"));
    }

    @Test
    void postMapped_throws_whenServerReportsFailure() {
        ObjectMapping.MappingResponse response = ObjectMapping.MappingResponse.newBuilder()
            .setSuccess(false)
            .setError("denied")
            .build();
        when(mockMappingStub.post(any())).thenReturn(response);

        CoordinatorTestArticle entity = new CoordinatorTestArticle();
        assertThrows(StatusRuntimeException.class, () -> mappingSut.withActingUser("tok").postMapped(entity));
    }

    @Test
    void updateMappedSendsTheKeyItWasGiven() {
        UUID id = UUID.randomUUID();
        ObjectMapping.MappingResponse response = ObjectMapping.MappingResponse.newBuilder()
            .setSuccess(true)
            .setData(Struct.newBuilder()
                .putFields("Id", Value.newBuilder().setStringValue(id.toString()).build())
                .build())
            .build();
        when(mockMappingStub.update(any())).thenReturn(response);

        CoordinatorTestArticle entity = new CoordinatorTestArticle();
        entity.id = id;
        entity.title = "Updated Title";

        mappingSut.withActingUser("tok").updateMapped(entity);

        ArgumentCaptor<ObjectMapping.MappingWriteRequest> captor =
            ArgumentCaptor.forClass(ObjectMapping.MappingWriteRequest.class);
        verify(mockMappingStub).update(captor.capture());
        Struct sentPayload = captor.getValue().getPayload();
        assertEquals(id.toString(), sentPayload.getFieldsOrThrow("Id").getStringValue());
        assertEquals("Updated Title", sentPayload.getFieldsOrThrow("Title").getStringValue());
    }

    @Test
    void updateMapped_throws_whenServerReportsFailure() {
        ObjectMapping.MappingResponse response = ObjectMapping.MappingResponse.newBuilder()
            .setSuccess(false)
            .setError("denied")
            .build();
        when(mockMappingStub.update(any())).thenReturn(response);

        CoordinatorTestArticle entity = new CoordinatorTestArticle();
        assertThrows(StatusRuntimeException.class, () -> mappingSut.withActingUser("tok").updateMapped(entity));
    }
}
