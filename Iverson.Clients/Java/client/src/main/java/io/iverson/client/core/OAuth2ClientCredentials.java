package io.iverson.client.core;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import io.grpc.CallCredentials;
import io.grpc.Metadata;
import io.grpc.Status;

import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.util.concurrent.Executor;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Attaches an OAuth2 client-credentials Bearer token to every gRPC call, caching the
 * token in memory and refreshing it 60 seconds before expiry.
 */
public final class OAuth2ClientCredentials extends CallCredentials {

    private final String clientId;
    private final String clientSecret;
    private final String tokenEndpoint;
    private final String scope;
    private final HttpClient httpClient = HttpClient.newHttpClient();
    private final ReentrantLock lock = new ReentrantLock();

    private volatile String cachedToken;
    private volatile Instant expiresAt = Instant.MIN;

    public OAuth2ClientCredentials(String clientId, String clientSecret, String tokenEndpoint) {
        this(clientId, clientSecret, tokenEndpoint, null);
    }

    public OAuth2ClientCredentials(String clientId, String clientSecret, String tokenEndpoint, String scope) {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.tokenEndpoint = tokenEndpoint;
        this.scope = scope;
    }

    @Override
    public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
        executor.execute(() -> {
            try {
                Metadata headers = new Metadata();
                headers.put(Metadata.Key.of("Authorization", Metadata.ASCII_STRING_MARSHALLER), "Bearer " + getToken());
                applier.apply(headers);
            } catch (Exception e) {
                applier.fail(Status.UNAUTHENTICATED.withCause(e));
            }
        });
    }

    private String getToken() throws Exception {
        if (cachedToken != null && Instant.now().isBefore(expiresAt)) {
            return cachedToken;
        }
        lock.lock();
        try {
            if (cachedToken != null && Instant.now().isBefore(expiresAt)) {
                return cachedToken;
            }
            String form = "grant_type=client_credentials"
                + "&client_id=" + URLEncoder.encode(clientId, StandardCharsets.UTF_8)
                + "&client_secret=" + URLEncoder.encode(clientSecret, StandardCharsets.UTF_8);
            if (scope != null) {
                form += "&scope=" + URLEncoder.encode(scope, StandardCharsets.UTF_8);
            }
            HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(tokenEndpoint))
                .header("Content-Type", "application/x-www-form-urlencoded")
                .POST(HttpRequest.BodyPublishers.ofString(form))
                .build();
            HttpResponse<String> response = httpClient.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) {
                throw new IllegalStateException("Failed to acquire Iverson client token: HTTP " + response.statusCode());
            }
            JsonObject body = JsonParser.parseString(response.body()).getAsJsonObject();
            cachedToken = body.get("access_token").getAsString();
            expiresAt = Instant.now().plusSeconds(body.get("expires_in").getAsLong() - 60);
            return cachedToken;
        } finally {
            lock.unlock();
        }
    }
}
