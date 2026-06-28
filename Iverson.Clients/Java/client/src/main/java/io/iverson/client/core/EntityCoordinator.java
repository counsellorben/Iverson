package io.iverson.client.core;

import io.grpc.StatusRuntimeException;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.search.QueryBuilder;
import iverson.ObjectMapping;
import iverson.ObjectPersistence;
import iverson.ObjectRetrieval;
import iverson.ObjectSearch;

import java.lang.reflect.Field;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;

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

    public EntityCoordinator(IversonClient client, Class<T> entityType) {
        if (entityType.getAnnotation(IversonEntity.class) == null) {
            throw new IllegalArgumentException(
                entityType.getSimpleName() + " is not annotated with @IversonEntity");
        }
        this.client     = client;
        this.entityType = entityType;
        this.typeName   = entityType.getSimpleName();
        // Validate that a key field exists
        findKeyField(entityType);
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
        ObjectPersistence.PersistResponse response = client.persistenceStub.post(request);
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
        ObjectPersistence.PersistResponse response = client.persistenceStub.update(request);
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
        ObjectRetrieval.RetrievalResponse response = client.retrievalStub.get(request);
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
        Iterator<ObjectRetrieval.RetrievalResponse> stream = client.retrievalStub.getMany(request);
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
            client.mappingStub.delete(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
    }

    // ── Object Search ──────────────────────────────────────────────────────────

    /**
     * Executes a search query and returns all matching results as a list.
     */
    public List<SearchResult<T>> search(QueryBuilder<T> queryBuilder) throws StatusRuntimeException {
        ObjectSearch.SearchRequest request = queryBuilder.build();
        Iterator<ObjectSearch.SearchResponse> stream = client.searchStub.search(request);
        List<SearchResult<T>> results = new ArrayList<>();
        while (stream.hasNext()) {
            ObjectSearch.SearchResponse response = stream.next();
            T entity = StructConverter.fromStruct(response.getData(), entityType);
            if (entity != null) results.add(new SearchResult<>(entity, response.getScore()));
        }
        return results;
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
}
