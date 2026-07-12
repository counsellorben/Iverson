package io.iverson.client.core;

import io.grpc.CallCredentials;
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

    /**
     * Creates a plain-text (h2c) channel to the given host and port, authenticating every
     * call with the given credentials (e.g. {@link OAuth2ClientCredentials}).
     */
    public IversonClient(String host, int port, CallCredentials credentials) {
        this(ManagedChannelBuilder.forAddress(host, port).usePlaintext().build(), credentials);
    }

    /**
     * Creates a client using an already-configured channel, attaching the given call
     * credentials to every stub. Confirmed via grpc-java's actual per-call invocation path
     * that plaintext channels accept CallCredentials with no special configuration (unlike
     * the .NET client, which requires an explicit insecure-channel opt-in).
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials) {
        this.channel         = channel;
        this.mappingStub     = ObjectMappingServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.persistenceStub = ObjectPersistenceServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.retrievalStub   = ObjectRetrievalServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.searchStub      = ObjectSearchServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
    }

    @Override
    public void close() throws InterruptedException {
        channel.shutdown().awaitTermination(5, TimeUnit.SECONDS);
    }
}
