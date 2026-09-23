package io.iverson.client.core;

import io.grpc.CallCredentials;
import io.grpc.Metadata;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;
import io.grpc.Status;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.util.concurrent.Executor;

import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;
import static org.junit.jupiter.api.Assertions.assertThrows;

/**
 * Closes CSR finding #10 for the Java SDK: IversonClient must refuse to attach
 * {@link CallCredentials} to a plaintext channel unless the caller has explicitly opted in via
 * {@code allowInsecureCredentials=true}, mirroring the .NET reference
 * ({@code ServiceCollectionExtensions.AddIversonClient}'s
 * {@code allowInsecureChannelCallCredentials}).
 */
class IversonClientTransportSecurityTest {

    private static final class NoopCallCredentials extends CallCredentials {
        @Override
        public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
            applier.apply(new Metadata());
        }
    }

    private ManagedChannel channel;

    @AfterEach
    void tearDown() {
        if (channel != null) {
            channel.shutdownNow();
        }
    }

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
    void managedChannelConstructor_withCredentials_throwsWithoutOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertThrows(IllegalArgumentException.class, () ->
            new IversonClient(channel, new NoopCallCredentials(), false));
    }

    @Test
    void managedChannelConstructor_withCredentials_succeedsWithOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertDoesNotThrow(() -> new IversonClient(channel, new NoopCallCredentials(), true).close());
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

    @Test
    void managedChannelConstructor_withActingUserTokenOnly_throwsWithoutOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertThrows(IllegalArgumentException.class, () ->
            new IversonClient(channel, null, "acting-user-token", false));
    }

    @Test
    void managedChannelConstructor_withActingUserTokenOnly_succeedsWithOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertDoesNotThrow(() -> new IversonClient(channel, null, "acting-user-token", true).close());
    }
}
