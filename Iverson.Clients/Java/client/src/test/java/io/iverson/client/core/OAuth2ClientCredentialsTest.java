package io.iverson.client.core;

import com.sun.net.httpserver.HttpServer;
import io.grpc.Attributes;
import io.grpc.CallCredentials;
import io.grpc.Metadata;
import io.grpc.MethodDescriptor;
import io.grpc.SecurityLevel;
import io.grpc.Status;
import org.junit.jupiter.api.Test;

import java.net.InetSocketAddress;
import java.util.concurrent.atomic.AtomicBoolean;

import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Closes CSR finding #2 for the Java SDK: {@link OAuth2ClientCredentials} must refuse to send
 * client credentials to a non-https token endpoint unless the caller has explicitly opted in via
 * {@code allowInsecureCredentials=true}, mirroring the analogous fix applied to the .NET/Python/
 * Go/TypeScript SDKs.
 */
class OAuth2ClientCredentialsTest {

    private static final String HTTP_ENDPOINT = "http://token.example.com/oauth2/token";
    private static final String HTTPS_ENDPOINT = "https://token.example.com/oauth2/token";

    @Test
    void fiveArgConstructor_withHttpEndpoint_throwsWithoutOptIn() {
        assertThrows(IllegalArgumentException.class, () ->
            new OAuth2ClientCredentials("client-id", "client-secret", HTTP_ENDPOINT, "scope", false));
    }

    @Test
    void fiveArgConstructor_withHttpEndpoint_succeedsWithOptIn() {
        assertDoesNotThrow(() ->
            new OAuth2ClientCredentials("client-id", "client-secret", HTTP_ENDPOINT, "scope", true));
    }

    @Test
    void fiveArgConstructor_withHttpsEndpoint_succeedsWithoutOptIn() {
        assertDoesNotThrow(() ->
            new OAuth2ClientCredentials("client-id", "client-secret", HTTPS_ENDPOINT, "scope", false));
    }

    @Test
    void threeArgConstructor_withHttpsEndpoint_neverThrows() {
        assertDoesNotThrow(() ->
            new OAuth2ClientCredentials("client-id", "client-secret", HTTPS_ENDPOINT));
    }

    @Test
    void threeArgConstructor_withHttpEndpoint_alwaysThrows() {
        // The pre-remediation 3-arg overload has no way to opt in, so it must always refuse a
        // non-https token endpoint rather than silently defaulting to the old insecure behavior.
        assertThrows(IllegalArgumentException.class, () ->
            new OAuth2ClientCredentials("client-id", "client-secret", HTTP_ENDPOINT));
    }

    @Test
    void fourArgConstructor_withHttpsEndpoint_neverThrows() {
        assertDoesNotThrow(() ->
            new OAuth2ClientCredentials("client-id", "client-secret", HTTPS_ENDPOINT, "scope"));
    }

    @Test
    void fourArgConstructor_withHttpEndpoint_alwaysThrows() {
        // Same rationale as the 3-arg overload above: no opt-in path, so it must always refuse.
        assertThrows(IllegalArgumentException.class, () ->
            new OAuth2ClientCredentials("client-id", "client-secret", HTTP_ENDPOINT, "scope"));
    }

    @Test
    void applyRequestMetadata_onRedirectFromTokenEndpoint_doesNotFollowAndFails() throws Exception {
        AtomicBoolean redirectTargetHit = new AtomicBoolean(false);
        HttpServer redirectTarget = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        redirectTarget.createContext("/", ex -> { redirectTargetHit.set(true); ex.sendResponseHeaders(200, -1); });
        redirectTarget.start();

        HttpServer tokenEndpoint = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        tokenEndpoint.createContext("/token", ex -> {
            ex.getResponseHeaders().add("Location", "http://127.0.0.1:" + redirectTarget.getAddress().getPort() + "/");
            ex.sendResponseHeaders(307, -1);
            ex.close();
        });
        tokenEndpoint.start();

        var credentials = new OAuth2ClientCredentials("client-id", "client-secret",
            "http://127.0.0.1:" + tokenEndpoint.getAddress().getPort() + "/token", null, true);

        AtomicBoolean failed = new AtomicBoolean(false);
        credentials.applyRequestMetadata(
            new CallCredentials.RequestInfo() {
                public MethodDescriptor<?, ?> getMethodDescriptor() { return null; }
                public SecurityLevel getSecurityLevel() { return SecurityLevel.NONE; }
                public String getAuthority() { return "test"; }
                public Attributes getTransportAttrs() { return Attributes.EMPTY; }
            },
            Runnable::run,
            new CallCredentials.MetadataApplier() {
                public void apply(Metadata headers) { }
                public void fail(Status status) { failed.set(true); }
            });

        assertTrue(failed.get());
        assertFalse(redirectTargetHit.get());
        redirectTarget.stop(0);
        tokenEndpoint.stop(0);
    }
}
