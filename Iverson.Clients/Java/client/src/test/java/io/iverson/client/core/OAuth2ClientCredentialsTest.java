package io.iverson.client.core;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;
import static org.junit.jupiter.api.Assertions.assertThrows;

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
}
