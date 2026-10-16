package io.iverson.client.core;

import com.sun.net.httpserver.HttpServer;
import io.grpc.CallCredentials;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;
import io.grpc.Metadata;
import io.grpc.Server;
import io.grpc.ServerCall;
import io.grpc.ServerCallHandler;
import io.grpc.ServerInterceptor;
import io.grpc.ServerInterceptors;
import io.grpc.Status;
import io.grpc.StatusRuntimeException;
import io.grpc.netty.shaded.io.grpc.netty.GrpcSslContexts;
import io.grpc.netty.shaded.io.grpc.netty.NettyServerBuilder;
import io.grpc.netty.shaded.io.netty.handler.ssl.SslContextBuilder;
import io.grpc.stub.StreamObserver;
import iverson.ObjectMapping.GetSchemaRequest;
import iverson.ObjectMapping.GetSchemaResponse;
import iverson.ObjectMappingServiceGrpc;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import javax.net.ssl.KeyManagerFactory;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.KeyStore;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.Executor;
import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

/**
 * The Java SDK's transport guard for call credentials. CSR finding #10 added a construction-time
 * opt-in ({@code allowInsecureCredentials=true}) for plaintext channels; CSR round-11 #16 moves the
 * check for caller-built channels to send time, where the transport's security level is known: on
 * a TLS channel credentials need no opt-in, and on a plaintext channel without the opt-in every call
 * fails {@code UNAUTHENTICATED} before any credential runs. The {@code plaintext(…)} factories,
 * whose transport is known, still refuse at construction.
 *
 * <p>The send-time tests make real calls to two netty servers on localhost, one plaintext and one
 * TLS. The TLS certificate is generated per run with the JDK's keytool and installed as the JVM's
 * default trust store, which the host/port constructors use; the previous trust-store properties
 * are restored afterwards.
 */
class IversonClientTransportSecurityTest {

    private static final Metadata.Key<String> AUTHORIZATION =
        Metadata.Key.of("authorization", Metadata.ASCII_STRING_MARSHALLER);
    private static final Metadata.Key<String> ACTING_USER =
        Metadata.Key.of(OAuth2ClientCredentials.ACTING_USER_METADATA_KEY, Metadata.ASCII_STRING_MARSHALLER);
    private static final String REFUSAL =
        "call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false";
    private static final String TRUST_STORE = "javax.net.ssl.trustStore";
    private static final String TRUST_STORE_PASSWORD = "javax.net.ssl.trustStorePassword";
    private static final String TRUST_STORE_TYPE = "javax.net.ssl.trustStoreType";

    private static final class NoopCallCredentials extends CallCredentials {
        @Override
        public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
            applier.apply(new Metadata());
        }
    }

    /**
     * Sends a fixed service token, plus the acting-user token from the call options as
     * {@link OAuth2ClientCredentials} does, and counts how often it runs.
     */
    private static final class BearerCredentials extends CallCredentials {
        final AtomicInteger applied = new AtomicInteger();

        @Override
        public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
            applied.incrementAndGet();
            Metadata headers = new Metadata();
            headers.put(AUTHORIZATION, "Bearer service-token");
            String acting = requestInfo.getCallOptions().getOption(OAuth2ClientCredentials.ACTING_USER_TOKEN);
            if (acting != null) {
                headers.put(ACTING_USER, "Bearer " + acting);
            }
            applier.apply(headers);
        }
    }

    /** Answers GetSchema with an empty catalog. */
    private static final class MappingService extends ObjectMappingServiceGrpc.ObjectMappingServiceImplBase {
        @Override
        public void getSchema(GetSchemaRequest request, StreamObserver<GetSchemaResponse> responseObserver) {
            responseObserver.onNext(GetSchemaResponse.getDefaultInstance());
            responseObserver.onCompleted();
        }
    }

    /** Every call either server receives, as "authorization | x-acting-user-authorization". */
    private static final List<String> received = new CopyOnWriteArrayList<>();

    private static final ServerInterceptor RECORDER = new ServerInterceptor() {
        @Override
        public <ReqT, RespT> ServerCall.Listener<ReqT> interceptCall(
                ServerCall<ReqT, RespT> call, Metadata headers, ServerCallHandler<ReqT, RespT> next) {
            received.add(headers.get(AUTHORIZATION) + " | " + headers.get(ACTING_USER));
            return next.startCall(call, headers);
        }
    };

    private static Server plaintextServer;
    private static Server tlsServer;
    private static String savedTrustStore;
    private static String savedTrustStorePassword;
    private static String savedTrustStoreType;

    private ManagedChannel channel;

    @BeforeAll
    static void startServers(@TempDir Path dir) throws Exception {
        char[] password = "transport-test".toCharArray();
        Path serverStore = dir.resolve("server.p12");
        Process keytool = new ProcessBuilder(
                Path.of(System.getProperty("java.home"), "bin", "keytool").toString(),
                "-genkeypair", "-alias", "localhost", "-keyalg", "EC", "-groupname", "secp256r1",
                "-dname", "CN=localhost", "-ext", "SAN=dns:localhost", "-validity", "1",
                "-storetype", "PKCS12", "-keystore", serverStore.toString(),
                "-storepass", new String(password))
            .redirectErrorStream(true).start();
        String keytoolOutput = new String(keytool.getInputStream().readAllBytes(), StandardCharsets.UTF_8);
        assertEquals(0, keytool.waitFor(), keytoolOutput);

        KeyStore keys = KeyStore.getInstance("PKCS12");
        try (InputStream in = Files.newInputStream(serverStore)) {
            keys.load(in, password);
        }
        KeyStore trust = KeyStore.getInstance("PKCS12");
        trust.load(null, null);
        trust.setCertificateEntry("localhost", keys.getCertificate("localhost"));
        Path trustStore = dir.resolve("trust.p12");
        try (OutputStream out = Files.newOutputStream(trustStore)) {
            trust.store(out, password);
        }
        savedTrustStore = System.getProperty(TRUST_STORE);
        savedTrustStorePassword = System.getProperty(TRUST_STORE_PASSWORD);
        savedTrustStoreType = System.getProperty(TRUST_STORE_TYPE);
        System.setProperty(TRUST_STORE, trustStore.toString());
        System.setProperty(TRUST_STORE_PASSWORD, new String(password));
        System.setProperty(TRUST_STORE_TYPE, "PKCS12");

        KeyManagerFactory keyManagers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        keyManagers.init(keys, password);
        var service = ServerInterceptors.intercept(new MappingService(), RECORDER);
        plaintextServer = NettyServerBuilder.forAddress(new InetSocketAddress("localhost", 0))
            .addService(service).build().start();
        tlsServer = NettyServerBuilder.forAddress(new InetSocketAddress("localhost", 0))
            .sslContext(GrpcSslContexts.configure(SslContextBuilder.forServer(keyManagers)).build())
            .addService(service).build().start();
    }

    @AfterAll
    static void stopServers() {
        plaintextServer.shutdownNow();
        tlsServer.shutdownNow();
        restore(TRUST_STORE, savedTrustStore);
        restore(TRUST_STORE_PASSWORD, savedTrustStorePassword);
        restore(TRUST_STORE_TYPE, savedTrustStoreType);
    }

    private static void restore(String key, String value) {
        if (value == null) {
            System.clearProperty(key);
        } else {
            System.setProperty(key, value);
        }
    }

    @BeforeEach
    void clearRecord() {
        received.clear();
    }

    @AfterEach
    void tearDown() {
        if (channel != null) {
            channel.shutdownNow();
        }
    }

    private ManagedChannel plaintextChannel() {
        channel = ManagedChannelBuilder.forAddress("localhost", plaintextServer.getPort()).usePlaintext().build();
        return channel;
    }

    /** The call fails UNAUTHENTICATED with the guard's own description, and no server sees it. */
    private static void assertRefusedWithoutCall(Runnable call) {
        StatusRuntimeException e = assertThrows(StatusRuntimeException.class, call::run);
        assertEquals(Status.Code.UNAUTHENTICATED, e.getStatus().getCode());
        assertEquals(REFUSAL, e.getStatus().getDescription());
        assertEquals(List.of(), received, "the server must see no call");
    }

    // ── plaintext(…) factories: the transport is known, so they refuse at construction ──

    @Test
    void plaintextFactory_withCredentials_throwsWithoutOptIn() {
        assertThrows(IllegalArgumentException.class, () ->
            IversonClient.plaintext("localhost", 5000, new NoopCallCredentials(), false));
    }

    @Test
    void plaintextFactory_withCredentials_succeedsWithOptIn() throws Exception {
        try (IversonClient client =
                 IversonClient.plaintext("localhost", 5000, new NoopCallCredentials(), true)) {
            assertDoesNotThrow(() -> {});
        }
    }

    @Test
    void plaintextFactory_deprecatedOverload_withCredentials_alwaysThrows() {
        // The pre-remediation 3-arg overload has no way to opt in, so it must always refuse
        // non-null credentials rather than silently defaulting to the old insecure behavior.
        assertThrows(IllegalArgumentException.class, () ->
            IversonClient.plaintext("localhost", 5000, new NoopCallCredentials()));
    }

    @Test
    void plaintextFactory_withNullCredentials_neverThrows() {
        assertDoesNotThrow(() -> IversonClient.plaintext("localhost", 5000, null, false).close());
    }

    // ── CSR round-3 finding #4: acting-user token bypassed the plaintext-credential guard ──
    // The acting-user token travels via a CallOptions key consumed by
    // OAuth2ClientCredentials#applyRequestMetadata, not via CallCredentials identity itself, so
    // the original credentials != null check alone missed it: a caller supplying only an
    // acting-user token (null service credentials) over a plaintext channel passed silently.

    @Test
    void plaintextFactory_withActingUserTokenOnly_throwsWithoutOptIn() {
        assertThrows(IllegalArgumentException.class, () ->
            IversonClient.plaintext("localhost", 5000, null, "acting-user-token", false));
    }

    @Test
    void plaintextFactory_withActingUserTokenOnly_succeedsWithOptIn() throws Exception {
        try (IversonClient client =
                 IversonClient.plaintext("localhost", 5000, null, "acting-user-token", true)) {
            assertDoesNotThrow(() -> {});
        }
    }

    // ── CSR round-11 #16: a TLS channel needs no opt-in ──

    @Test
    void tlsHostPortConstructor_withCredentials_needsNoOptIn_andCallSucceeds() throws Exception {
        try (IversonClient client = new IversonClient("localhost", tlsServer.getPort(), new BearerCredentials())) {
            assertEquals(List.of(), client.getSchema("trace", "per-call-token"));
        }
        assertEquals(List.of("Bearer service-token | Bearer per-call-token"), received);
    }

    @Test
    void tlsHostPortConstructor_withCredentialsAndActingUserToken_needsNoOptIn_andCallSucceeds() throws Exception {
        try (IversonClient client = new IversonClient(
                "localhost", tlsServer.getPort(), new BearerCredentials(), "ambient-token")) {
            assertEquals(List.of(), client.getSchema("trace", null));
        }
        assertEquals(List.of("Bearer service-token | Bearer ambient-token"), received);
    }

    // ── CSR round-11 #16: a caller-built plaintext channel refuses at send time ──

    @Test
    void managedChannelConstructor_plaintext_withCredentials_failsEveryCallWithoutOptIn() throws Exception {
        BearerCredentials credentials = new BearerCredentials();
        try (IversonClient client = new IversonClient(plaintextChannel(), credentials, false)) {
            assertRefusedWithoutCall(() -> client.getSchema("trace", "per-call-token"));
        }
        assertEquals(0, credentials.applied.get(), "the credentials must never run");
    }

    @Test
    void managedChannelConstructor_plaintext_withCredentialsAndActingUserToken_failsEveryCallWithoutOptIn()
            throws Exception {
        BearerCredentials credentials = new BearerCredentials();
        try (IversonClient client = new IversonClient(plaintextChannel(), credentials, "ambient-token")) {
            assertRefusedWithoutCall(() -> client.getSchema("trace", null));
        }
        assertEquals(0, credentials.applied.get(), "the credentials must never run");
    }

    @Test
    void managedChannelConstructor_plaintext_withCredentials_sendsThemWithOptIn() throws Exception {
        try (IversonClient client =
                 new IversonClient(plaintextChannel(), new BearerCredentials(), "ambient-token", true)) {
            assertEquals(List.of(), client.getSchema("trace", null));
        }
        assertEquals(List.of("Bearer service-token | Bearer ambient-token"), received);
    }

    @Test
    void managedChannelConstructor_withCredentials_succeedsWithOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertDoesNotThrow(() -> new IversonClient(channel, new NoopCallCredentials(), true).close());
    }

    @Test
    void managedChannelConstructor_withActingUserTokenOnly_constructs_andNothingSendsTheToken() throws Exception {
        // With no CallCredentials nothing carries the acting-user token, so there is nothing to refuse.
        try (IversonClient client = new IversonClient(plaintextChannel(), null, "ambient-token", false)) {
            assertEquals(List.of(), client.getSchema("trace", null));
        }
        assertEquals(List.of("null | null"), received);
    }

    @Test
    void managedChannelConstructor_withActingUserTokenOnly_succeedsWithOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertDoesNotThrow(() -> new IversonClient(channel, null, "acting-user-token", true).close());
    }

    // ── OAuth2ClientCredentials applies the same check with its own allowInsecureCredentials ──

    @Test
    void oauth2Credentials_onOwnPlaintextStub_failWithoutTheirOwnOptIn() {
        var credentials = new OAuth2ClientCredentials("client-id", "client-secret", "https://localhost:1/token");
        var stub = ObjectMappingServiceGrpc.newBlockingStub(plaintextChannel()).withCallCredentials(credentials);
        assertRefusedWithoutCall(() -> stub.getSchema(GetSchemaRequest.getDefaultInstance()));
    }

    @Test
    void oauth2Credentials_underOptedInPlaintextFactory_stillFailWithoutTheirOwnOptIn() throws Exception {
        var credentials = new OAuth2ClientCredentials("client-id", "client-secret", "https://localhost:1/token");
        try (IversonClient client =
                 IversonClient.plaintext("localhost", plaintextServer.getPort(), credentials, true)) {
            assertRefusedWithoutCall(() -> client.getSchema("trace", "per-call-token"));
        }
    }

    @Test
    void oauth2Credentials_underOptedInPlaintextFactory_sendWithTheirOwnOptIn() throws Exception {
        HttpServer tokenEndpoint = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        tokenEndpoint.createContext("/token", exchange -> {
            byte[] body = "{\"access_token\":\"oauth2-token\",\"expires_in\":3600}".getBytes(StandardCharsets.UTF_8);
            exchange.getResponseHeaders().add("Content-Type", "application/json");
            exchange.sendResponseHeaders(200, body.length);
            exchange.getResponseBody().write(body);
            exchange.close();
        });
        tokenEndpoint.start();
        try {
            var credentials = new OAuth2ClientCredentials("client-id", "client-secret",
                "http://127.0.0.1:" + tokenEndpoint.getAddress().getPort() + "/token", null, true);
            try (IversonClient client =
                     IversonClient.plaintext("localhost", plaintextServer.getPort(), credentials, true)) {
                assertEquals(List.of(), client.getSchema("trace", "per-call-token"));
            }
        } finally {
            tokenEndpoint.stop(0);
        }
        assertEquals(List.of("Bearer oauth2-token | Bearer per-call-token"), received);
    }
}
