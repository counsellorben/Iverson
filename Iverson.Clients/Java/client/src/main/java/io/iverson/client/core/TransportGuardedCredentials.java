package io.iverson.client.core;

import io.grpc.CallCredentials;
import io.grpc.SecurityLevel;
import io.grpc.Status;

import java.util.concurrent.Executor;

/**
 * Wraps the {@link CallCredentials} that {@link IversonClient} attaches, so that they are sent
 * only over TLS (CSR round-11 #16). grpc-java attaches call credentials to any channel, and an
 * already-built {@link io.grpc.ManagedChannel} does not say whether it is TLS; the transport's
 * security level is known only when a call is sent. On a channel without privacy and integrity,
 * a call fails {@code UNAUTHENTICATED} before the wrapped credentials run, unless the caller set
 * {@code allowInsecureCredentials} for a known-local, non-TLS endpoint.
 */
final class TransportGuardedCredentials extends CallCredentials {

    private final CallCredentials inner;
    private final boolean allowInsecureCredentials;

    TransportGuardedCredentials(CallCredentials inner, boolean allowInsecureCredentials) {
        this.inner = inner;
        this.allowInsecureCredentials = allowInsecureCredentials;
    }

    @Override
    public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
        if (!refusedWithoutTls(requestInfo, allowInsecureCredentials, applier)) {
            // The original RequestInfo: OAuth2ClientCredentials reads the acting-user token from
            // its call options.
            inner.applyRequestMetadata(requestInfo, executor, applier);
        }
    }

    /**
     * The send-time check, shared with {@link OAuth2ClientCredentials}: when the call's transport
     * is not TLS and {@code allowInsecureCredentials} is false, fails the call and returns true.
     */
    static boolean refusedWithoutTls(
            RequestInfo requestInfo, boolean allowInsecureCredentials, MetadataApplier applier) {
        if (allowInsecureCredentials || requestInfo.getSecurityLevel() == SecurityLevel.PRIVACY_AND_INTEGRITY) {
            return false;
        }
        applier.fail(Status.UNAUTHENTICATED.withDescription(
            "call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false"));
        return true;
    }
}
