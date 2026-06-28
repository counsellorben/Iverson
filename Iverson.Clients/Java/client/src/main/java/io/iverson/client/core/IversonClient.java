package io.iverson.client.core;

import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;
import iverson.ObjectMappingServiceGrpc;
import iverson.ObjectPersistenceServiceGrpc;
import iverson.ObjectRetrievalServiceGrpc;
import iverson.ObjectSearchServiceGrpc;

import java.util.concurrent.TimeUnit;

/**
 * Entry point that owns the gRPC channel and vends typed stubs.
 * Create one instance per server endpoint and share it across all coordinators.
 *
 * <pre>{@code
 * try (IversonClient client = new IversonClient("localhost", 5000)) {
 *     var registrar = new SchemaRegistrar(client);
 *     registrar.registerAll(Article.class, Author.class);
 *
 *     var coordinator = new EntityCoordinator<>(client, Article.class);
 *     String id = coordinator.persist(article);
 * }
 * }</pre>
 */
public final class IversonClient implements AutoCloseable {

    private final ManagedChannel channel;

    final ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub    mappingStub;
    final ObjectPersistenceServiceGrpc.ObjectPersistenceServiceBlockingStub persistenceStub;
    final ObjectRetrievalServiceGrpc.ObjectRetrievalServiceBlockingStub retrievalStub;
    final ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub       searchStub;

    /**
     * Creates a plain-text (h2c) channel to the given host and port.
     */
    public IversonClient(String host, int port) {
        this(ManagedChannelBuilder.forAddress(host, port).usePlaintext().build());
    }

    /**
     * Creates a client using an already-configured channel (useful for testing or
     * when TLS / interceptors need to be wired up externally).
     */
    public IversonClient(ManagedChannel channel) {
        this.channel         = channel;
        this.mappingStub     = ObjectMappingServiceGrpc.newBlockingStub(channel);
        this.persistenceStub = ObjectPersistenceServiceGrpc.newBlockingStub(channel);
        this.retrievalStub   = ObjectRetrievalServiceGrpc.newBlockingStub(channel);
        this.searchStub      = ObjectSearchServiceGrpc.newBlockingStub(channel);
    }

    @Override
    public void close() throws InterruptedException {
        channel.shutdown().awaitTermination(5, TimeUnit.SECONDS);
    }
}
