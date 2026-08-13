package io.iverson.client.core;

import io.grpc.StatusRuntimeException;
import io.grpc.stub.AbstractStub;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.search.AggregateBuilder;
import io.iverson.client.search.ChunksBuilder;
import io.iverson.client.search.GroupByBuilder;
import io.iverson.client.search.PipelineBuilder;
import io.iverson.client.search.QueryBuilder;
import io.iverson.client.search.SimilarBuilder;
import iverson.ObjectMapping;
import iverson.ObjectMappingServiceGrpc;
import iverson.ObjectPersistence;
import iverson.ObjectRetrieval;
import iverson.ObjectSearch;
import iverson.ObjectSearchServiceGrpc;

import java.lang.reflect.Field;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Map;

/**
 * Per-entity surface for all CRUD and search operations.
 *
 * <p>Uses the lightweight {@code ObjectPersistenceService} for writes and
 * {@code ObjectRetrievalService} for key-based reads. Search goes through
 * {@code ObjectSearchService}.</p>
 *
 * @param <T> the entity type; must be annotated with {@link IversonEntity}
 */
public final class EntityCoordinator<T> {

    private final IversonClient client;
    private final Class<T> entityType;
    private final String typeName;
    private final ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub searchStub;
    private final String boundActingUserToken;

    public EntityCoordinator(IversonClient client, Class<T> entityType) {
        if (entityType.getAnnotation(IversonEntity.class) == null) {
            throw new IllegalArgumentException(
                entityType.getSimpleName() + " is not annotated with @IversonEntity");
        }
        this.client               = client;
        this.entityType           = entityType;
        this.typeName             = entityType.getSimpleName();
        this.searchStub           = client.searchStub;
        this.boundActingUserToken = null;
        // Validate that a key field exists
        findKeyField(entityType);
    }

    /** Package-private constructor for testing with a mock search stub. */
    EntityCoordinator(ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub searchStub, Class<T> entityType) {
        if (entityType.getAnnotation(IversonEntity.class) == null) {
            throw new IllegalArgumentException(
                entityType.getSimpleName() + " is not annotated with @IversonEntity");
        }
        this.client               = null;
        this.entityType           = entityType;
        this.typeName             = entityType.getSimpleName();
        this.searchStub           = searchStub;
        this.boundActingUserToken = null;
        // Validate that a key field exists
        findKeyField(entityType);
    }

    /** Copy constructor backing {@link #withActingUser(String)}. {@code client} may be null. */
    private EntityCoordinator(IversonClient client,
                               ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub searchStub,
                               Class<T> entityType,
                               String boundActingUserToken) {
        this.client               = client;
        this.entityType           = entityType;
        this.typeName             = entityType.getSimpleName();
        this.searchStub           = searchStub;
        this.boundActingUserToken = boundActingUserToken;
    }

    /**
     * Returns a copy of this coordinator bound to the given acting-user identity. The bound
     * token applies to every subsequent call on the returned coordinator that supplies no more
     * specific (per-call explicit) token, taking precedence over the client's ambient identity.
     */
    public EntityCoordinator<T> withActingUser(String actingUserToken) {
        return new EntityCoordinator<>(client, searchStub, entityType, actingUserToken);
    }

    // ── Object Persistence (lightweight writes) ────────────────────────────────

    /**
     * Persists a new entity and returns the server-assigned key.
     */
    public String persist(T entity) throws StatusRuntimeException {
        ObjectPersistence.PersistRequest request = ObjectPersistence.PersistRequest.newBuilder()
            .setTypeName(typeName)
            .setPayload(StructConverter.toStruct(entity))
            .build();
        ObjectPersistence.PersistResponse response = withIdentity(client.persistenceStub, null).post(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
        return response.getKey();
    }

    /**
     * Updates an existing entity (matched by the key field).
     */
    public void update(T entity) throws StatusRuntimeException {
        ObjectPersistence.PersistRequest request = ObjectPersistence.PersistRequest.newBuilder()
            .setTypeName(typeName)
            .setPayload(StructConverter.toStruct(entity))
            .build();
        ObjectPersistence.PersistResponse response = withIdentity(client.persistenceStub, null).update(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
    }

    // ── Object Retrieval ───────────────────────────────────────────────────────

    /**
     * Fetches a single entity by its key. Returns {@code null} if not found.
     */
    public T get(String id) throws StatusRuntimeException {
        ObjectRetrieval.RetrievalRequest request = ObjectRetrieval.RetrievalRequest.newBuilder()
            .setTypeName(typeName)
            .setKey(id)
            .build();
        ObjectRetrieval.RetrievalResponse response = withIdentity(client.retrievalStub, null).get(request);
        if (!response.getFound()) return null;
        return StructConverter.fromStruct(response.getData(), entityType);
    }

    /**
     * Fetches multiple entities by their keys. Missing entities are silently omitted.
     */
    public List<T> getMany(List<String> ids) throws StatusRuntimeException {
        ObjectRetrieval.RetrievalManyRequest request = ObjectRetrieval.RetrievalManyRequest.newBuilder()
            .setTypeName(typeName)
            .addAllKeys(ids)
            .build();
        Iterator<ObjectRetrieval.RetrievalResponse> stream = withIdentity(client.retrievalStub, null).getMany(request);
        List<T> results = new ArrayList<>();
        while (stream.hasNext()) {
            ObjectRetrieval.RetrievalResponse response = stream.next();
            if (!response.getFound()) continue;
            T entity = StructConverter.fromStruct(response.getData(), entityType);
            if (entity != null) results.add(entity);
        }
        return results;
    }

    /**
     * Deletes the entity with the given key.
     */
    public void delete(String id) throws StatusRuntimeException {
        ObjectMapping.MappingDeleteRequest request =
            ObjectMapping.MappingDeleteRequest.newBuilder()
                .setTypeName(typeName)
                .setKey(id)
                .build();
        ObjectMapping.MappingDeleteResponse response =
            withIdentity(client.mappingStub, null).delete(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
    }

    // ── Object Mapping (full CRUD with relation resolution) ────────────────────

    /**
     * Fetches a single entity by key with server-side relation resolution to {@code depth}.
     * Returns {@code null} if not found.
     */
    public T getMapped(String id, int depth, String actingUserToken) throws StatusRuntimeException {
        ObjectMapping.MappingGetRequest request = ObjectMapping.MappingGetRequest.newBuilder()
            .setTypeName(typeName)
            .setKey(id)
            .setDepth(depth)
            .build();
        ObjectMapping.MappingResponse response = mappingStubFor(actingUserToken).get(request);
        if (!response.getSuccess()) return null;
        return StructConverter.fromStruct(response.getData(), entityType);
    }

    /**
     * Creates an entity through the mapping path, which resolves its relations server-side.
     * Returns the entity hydrated from the response, carrying the server-assigned key — the
     * caller never assigns one.
     */
    public T postMapped(T entity, String actingUserToken) throws StatusRuntimeException {
        ObjectMapping.MappingWriteRequest request = ObjectMapping.MappingWriteRequest.newBuilder()
            .setTypeName(typeName)
            .setPayload(StructConverter.toStruct(entity))
            .build();
        ObjectMapping.MappingResponse response = mappingStubFor(actingUserToken).post(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
        return StructConverter.fromStruct(response.getData(), entityType);
    }

    /** Updates an existing entity through the mapping path. */
    public T updateMapped(T entity, String actingUserToken) throws StatusRuntimeException {
        ObjectMapping.MappingWriteRequest request = ObjectMapping.MappingWriteRequest.newBuilder()
            .setTypeName(typeName)
            .setPayload(StructConverter.toStruct(entity))
            .build();
        ObjectMapping.MappingResponse response = mappingStubFor(actingUserToken).update(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
        return StructConverter.fromStruct(response.getData(), entityType);
    }

    // ── Object Search ──────────────────────────────────────────────────────────

    /**
     * Executes a search query and returns all matching results as a list.
     */
    public List<SearchResult<T>> search(QueryBuilder<T> queryBuilder) throws StatusRuntimeException {
        ObjectSearch.SearchRequest request = queryBuilder.build();
        Iterator<ObjectSearch.SearchResponse> stream = stubFor(null).search(request);
        List<SearchResult<T>> results = new ArrayList<>();
        while (stream.hasNext()) {
            ObjectSearch.SearchResponse response = stream.next();
            T entity = StructConverter.fromStruct(response.getData(), entityType);
            if (entity != null) results.add(new SearchResult<>(entity, response.getScore()));
        }
        return results;
    }

    /**
     * Executes a compound GROUP BY aggregation and returns one row per output group. Column
     * set depends on the query's keys/metrics, so rows come back as string-keyed maps rather
     * than typed entities.
     */
    public List<Map<String, Object>> groupBy(GroupByBuilder builder) throws StatusRuntimeException {
        return groupBy(builder, null);
    }

    /** Same as {@link #groupBy(GroupByBuilder)}, propagating an acting-user token if given. */
    public List<Map<String, Object>> groupBy(GroupByBuilder builder, String actingUserToken)
            throws StatusRuntimeException {
        ObjectSearch.GroupByRequest request = builder.build();
        Iterator<ObjectSearch.SearchResponse> stream = stubFor(actingUserToken).groupBy(request);
        List<Map<String, Object>> results = new ArrayList<>();
        while (stream.hasNext()) {
            results.add(StructConverter.fromStructAsMap(stream.next().getData()));
        }
        return results;
    }

    /**
     * Executes an aggregation request and returns the full {@link ObjectSearch.AggregateResponse}
     * (one {@code AggregationResult} per requested {@code AggregationSpec}).
     */
    public ObjectSearch.AggregateResponse aggregate(AggregateBuilder builder) throws StatusRuntimeException {
        return aggregate(builder, null);
    }

    /** Same as {@link #aggregate(AggregateBuilder)}, propagating an acting-user token if given. */
    public ObjectSearch.AggregateResponse aggregate(AggregateBuilder builder, String actingUserToken)
            throws StatusRuntimeException {
        ObjectSearch.AggregateRequest request = builder.build();
        return stubFor(actingUserToken).aggregate(request);
    }

    /**
     * Executes a pipeline (CTE chain) and returns one row per output row. Column set depends
     * on the pipeline's final step, so rows come back as string-keyed maps, same as
     * {@link #groupBy(GroupByBuilder)}.
     */
    public List<Map<String, Object>> pipeline(PipelineBuilder builder) throws StatusRuntimeException {
        return pipeline(builder, null);
    }

    /** Same as {@link #pipeline(PipelineBuilder)}, propagating an acting-user token if given. */
    public List<Map<String, Object>> pipeline(PipelineBuilder builder, String actingUserToken)
            throws StatusRuntimeException {
        ObjectSearch.PipelineRequest request = builder.build();
        Iterator<ObjectSearch.SearchResponse> stream = stubFor(actingUserToken).pipeline(request);
        List<Map<String, Object>> results = new ArrayList<>();
        while (stream.hasNext()) {
            results.add(StructConverter.fromStructAsMap(stream.next().getData()));
        }
        return results;
    }

    /** Executes a Qdrant vector similarity search and returns matching entities with scores. */
    public List<SearchResult<T>> searchSimilar(SimilarBuilder builder) throws StatusRuntimeException {
        return searchSimilar(builder, null);
    }

    /** Same as {@link #searchSimilar(SimilarBuilder)}, propagating an acting-user token if given. */
    public List<SearchResult<T>> searchSimilar(SimilarBuilder builder, String actingUserToken)
            throws StatusRuntimeException {
        ObjectSearch.SearchSimilarRequest request = builder.build();
        Iterator<ObjectSearch.SearchResponse> stream = stubFor(actingUserToken).searchSimilar(request);
        List<SearchResult<T>> results = new ArrayList<>();
        while (stream.hasNext()) {
            ObjectSearch.SearchResponse response = stream.next();
            T entity = StructConverter.fromStruct(response.getData(), entityType);
            if (entity != null) results.add(new SearchResult<>(entity, response.getScore()));
        }
        return results;
    }

    /**
     * Executes a Qdrant chunk/RAG search and returns matching passage chunks with their
     * parent entity key and relevance score.
     */
    public List<ChunkSearchResult> searchChunks(ChunksBuilder builder) throws StatusRuntimeException {
        return searchChunks(builder, null);
    }

    /** Same as {@link #searchChunks(ChunksBuilder)}, propagating an acting-user token if given. */
    public List<ChunkSearchResult> searchChunks(ChunksBuilder builder, String actingUserToken)
            throws StatusRuntimeException {
        ObjectSearch.SearchChunksRequest request = builder.build();
        Iterator<ObjectSearch.ChunkSearchResponse> stream = stubFor(actingUserToken).searchChunks(request);
        List<ChunkSearchResult> results = new ArrayList<>();
        while (stream.hasNext()) {
            ObjectSearch.ChunkSearchResponse response = stream.next();
            results.add(new ChunkSearchResult(response.getParentKey(), response.getChunkText(), response.getScore()));
        }
        return results;
    }

    /**
     * Attaches the resolved acting-user identity to {@code stub} as a call option (consumed by
     * {@link OAuth2ClientCredentials}). Resolution order: the caller's explicit token, then this
     * coordinator's bound identity, then the client's ambient one; none attaches nothing.
     */
    private <S extends AbstractStub<S>> S withIdentity(S stub, String explicitToken) {
        String token = explicitToken != null ? explicitToken
            : boundActingUserToken != null ? boundActingUserToken
            : (client != null ? client.actingUserToken : null);
        return token != null ? stub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, token) : stub;
    }

    /**
     * Returns the search stub to invoke, attaching the resolved acting-user identity as a call
     * option (consumed by {@link OAuth2ClientCredentials}).
     */
    private ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub stubFor(String actingUserToken) {
        return withIdentity(searchStub, actingUserToken);
    }

    /**
     * Returns the mapping stub to invoke, attaching the resolved acting-user identity as a call
     * option (consumed by {@link OAuth2ClientCredentials}). The constructor's
     * {@link io.grpc.CallCredentials} are the service's own client-credentials token and do
     * <em>not</em> identify an acting user; the server denies any write without one.
     */
    private ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mappingStubFor(String actingUserToken) {
        return withIdentity(client.mappingStub, actingUserToken);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Field findKeyField(Class<?> cls) {
        for (Field field : getAllFields(cls)) {
            if (field.getAnnotation(IversonKey.class) != null) {
                field.setAccessible(true);
                return field;
            }
        }
        throw new IllegalArgumentException(
            cls.getSimpleName() + " has no field annotated with @IversonKey");
    }

    private static List<Field> getAllFields(Class<?> cls) {
        List<Field> fields = new ArrayList<>();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                if (!f.isSynthetic()) fields.add(f);
            }
            cls = cls.getSuperclass();
        }
        return fields;
    }

    /** Wraps a search result entity with its relevance score. */
    public record SearchResult<T>(T entity, float score) {}

    /** Wraps a chunk/RAG search hit with its parent entity key, passage text, and relevance score. */
    public record ChunkSearchResult(String parentKey, String chunkText, float score) {}
}
