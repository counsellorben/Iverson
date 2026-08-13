package io.iverson.client.core;

import io.grpc.CallCredentials;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;
import iverson.ObjectMapping.GetSchemaRequest;
import iverson.ObjectMapping.GetSchemaResponse;
import iverson.ObjectMapping.SchemaType;
import iverson.ObjectMappingServiceGrpc;
import iverson.ObjectPersistenceServiceGrpc;
import iverson.ObjectRetrievalServiceGrpc;
import iverson.ObjectSearchServiceGrpc;

import java.util.List;
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

    /** Ambient acting-user identity applied when no explicit or coordinator-bound token exists. */
    final String actingUserToken;

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
        this.actingUserToken = null;
    }

    /**
     * Creates a plain-text (h2c) channel to the given host and port, authenticating every
     * call with the given credentials (e.g. {@link OAuth2ClientCredentials}).
     */
    public IversonClient(String host, int port, CallCredentials credentials) {
        this(ManagedChannelBuilder.forAddress(host, port).usePlaintext().build(), credentials);
    }

    /**
     * Creates a plain-text (h2c) channel to the given host and port, authenticating every call
     * with the given credentials, and carrying an ambient acting-user token as described in
     * {@link #IversonClient(ManagedChannel, CallCredentials, String)}.
     */
    public IversonClient(String host, int port, CallCredentials credentials, String actingUserToken) {
        this(ManagedChannelBuilder.forAddress(host, port).usePlaintext().build(), credentials, actingUserToken);
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
        this.actingUserToken = null;
    }

    /**
     * Creates a client using an already-configured channel and call credentials, plus an
     * ambient acting-user token applied to every call that carries no more specific identity
     * (per-call explicit token, then coordinator-bound token via {@code withActingUser}, then
     * this ambient one).
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials, String actingUserToken) {
        this.channel         = channel;
        this.mappingStub     = ObjectMappingServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.persistenceStub = ObjectPersistenceServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.retrievalStub   = ObjectRetrievalServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.searchStub      = ObjectSearchServiceGrpc.newBlockingStub(channel).withCallCredentials(credentials);
        this.actingUserToken = actingUserToken;
    }

    /**
     * Test seam: builds a client over a pre-made mapping stub, bypassing channel construction.
     * The channel and the other three stubs are null, so a client built this way serves only
     * mapping calls; {@link #close()} is a no-op for it.
     */
    IversonClient(ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mappingStub) {
        this.channel         = null;
        this.mappingStub     = mappingStub;
        this.persistenceStub = null;
        this.retrievalStub   = null;
        this.searchStub      = null;
        this.actingUserToken = null;
    }

    /**
     * Test seam: builds a client over pre-made stubs (any of which may be null) and an
     * ambient acting-user token, bypassing channel construction. {@link #close()} is a no-op
     * for a client built this way.
     */
    IversonClient(ObjectPersistenceServiceGrpc.ObjectPersistenceServiceBlockingStub persistenceStub,
                  ObjectRetrievalServiceGrpc.ObjectRetrievalServiceBlockingStub retrievalStub,
                  ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mappingStub,
                  ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub searchStub,
                  String actingUserToken) {
        this.channel         = null;
        this.persistenceStub = persistenceStub;
        this.retrievalStub   = retrievalStub;
        this.mappingStub     = mappingStub;
        this.searchStub      = searchStub;
        this.actingUserToken = actingUserToken;
    }

    /**
     * Retrieves the schema catalog of the types the acting user is authorized to read.
     *
     * <p>The acting-user token travels as a per-call option consumed by
     * {@link OAuth2ClientCredentials} — the {@link CallCredentials} given to the constructor are
     * the service's own client-credentials token and do <em>not</em> identify an acting user. This
     * mirrors {@code EntityCoordinator}'s data-plane methods, which take the same trailing
     * parameter.
     *
     * <p>The catalog lists precisely the types the caller can actually query, so an empty result is
     * a normal authorization outcome, not an error. It means every registered type was denied for
     * this caller. The usual causes are: no acting user resolved at all (neither a per-call token
     * nor the client's ambient identity); an acting user with no {@code tenant_id} claim; registered
     * types that declare no authorization rules; or types that declare no tenant field. All four
     * make a type unreadable through every RPC, not just this one.
     *
     * @param actingUserToken the end-user access token to act as for this call; may be null, in
     *                        which case the client's ambient identity is used instead, and only an
     *                        unresolved identity yields an empty catalog.
     */
    public List<SchemaType> getSchema(String traceId, String actingUserToken) {
        GetSchemaResponse response = stubFor(actingUserToken).getSchema(
            GetSchemaRequest.newBuilder().setTraceId(traceId).build());
        return response.getTypesList();
    }

    /**
     * Returns the mapping stub to invoke, attaching the resolved acting-user identity as a call
     * option (consumed by {@link OAuth2ClientCredentials}). Resolution order: the caller's explicit
     * token, then this client's ambient identity; neither present attaches nothing.
     */
    private ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub stubFor(String actingUserToken) {
        String token = actingUserToken != null ? actingUserToken : this.actingUserToken;
        return token != null
            ? mappingStub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, token)
            : mappingStub;
    }

    @Override
    public void close() throws InterruptedException {
        // Null under the package-private test-seam constructor, which builds no channel.
        if (channel != null) {
            channel.shutdown().awaitTermination(5, TimeUnit.SECONDS);
        }
    }
}
