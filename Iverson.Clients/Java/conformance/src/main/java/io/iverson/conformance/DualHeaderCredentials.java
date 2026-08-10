package io.iverson.conformance;

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

import java.util.concurrent.Executor;

/**
 * Emits both identities every driver call needs, attached at the channel because
 * {@code EntityCoordinator}'s mapped CRUD methods (persist/update/get/delete) take no
 * acting-user parameter — only the search-family methods do — yet the server denies a write
 * when the acting user is absent on a rules-carrying schema.
 *
 * <p>Modelled on {@code OAuth2ClientCredentials.applyRequestMetadata}: the service identity
 * (client-credentials token, fetched once and cached) rides as {@code Authorization: Bearer},
 * and the acting-user identity (fixed at construction, unlike {@code OAuth2ClientCredentials}'s
 * per-call option) rides as {@code x-acting-user-authorization: Bearer}. Both are attached to
 * every call, on every stub, via {@link io.iverson.client.core.IversonClient#IversonClient(
 * io.grpc.ManagedChannel, CallCredentials)}.
 */
final class DualHeaderCredentials extends CallCredentials {

    private final String clientId;
    private final String clientSecret;
    private final String tokenEndpoint;
    private final String actingToken;
    private final HttpClient httpClient = HttpClient.newHttpClient();

    private volatile String cachedToken;

    DualHeaderCredentials(String clientId, String clientSecret, String tokenEndpoint, String actingToken) {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.tokenEndpoint = tokenEndpoint;
        this.actingToken = actingToken;
    }

    @Override
    public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
        executor.execute(() -> {
            try {
                Metadata headers = new Metadata();

                if (hasServiceCredentials()) {
                    headers.put(
                        Metadata.Key.of("Authorization", Metadata.ASCII_STRING_MARSHALLER),
                        "Bearer " + fetchServiceToken());
                }

                if (actingToken != null && !actingToken.isEmpty()) {
                    headers.put(
                        Metadata.Key.of("x-acting-user-authorization", Metadata.ASCII_STRING_MARSHALLER),
                        "Bearer " + actingToken);
                }

                applier.apply(headers);
            } catch (Exception e) {
                applier.fail(Status.UNAUTHENTICATED.withCause(e));
            }
        });
    }

    private boolean hasServiceCredentials() {
        return clientId != null && !clientId.isEmpty()
            && clientSecret != null && !clientSecret.isEmpty()
            && tokenEndpoint != null && !tokenEndpoint.isEmpty();
    }

    private String fetchServiceToken() throws Exception {
        if (cachedToken != null) return cachedToken;
        synchronized (this) {
            if (cachedToken != null) return cachedToken;

            String form = "grant_type=client_credentials"
                + "&client_id=" + URLEncoder.encode(clientId, StandardCharsets.UTF_8)
                + "&client_secret=" + URLEncoder.encode(clientSecret, StandardCharsets.UTF_8);

            HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(tokenEndpoint))
                .header("Content-Type", "application/x-www-form-urlencoded")
                .POST(HttpRequest.BodyPublishers.ofString(form))
                .build();

            HttpResponse<String> response = httpClient.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) {
                throw new IllegalStateException(
                    "failed to acquire service token: HTTP " + response.statusCode());
            }

            JsonObject body = JsonParser.parseString(response.body()).getAsJsonObject();
            cachedToken = body.get("access_token").getAsString();
            return cachedToken;
        }
    }
}
