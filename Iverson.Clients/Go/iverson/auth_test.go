package iverson

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestOAuth2ClientCredentials_GetRequestMetadata_FetchesAndCachesToken(t *testing.T) {
	requestCount := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requestCount++
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{ClientID: "id", ClientSecret: "secret", TokenEndpoint: server.URL}

	md, err := creds.GetRequestMetadata(context.Background())
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if md["authorization"] != "Bearer test-token" {
		t.Errorf("got %q, want %q", md["authorization"], "Bearer test-token")
	}

	if _, err := creds.GetRequestMetadata(context.Background()); err != nil {
		t.Fatalf("GetRequestMetadata (cached): %v", err)
	}
	if requestCount != 1 {
		t.Errorf("expected 1 token request, got %d", requestCount)
	}
}

func TestOAuth2ClientCredentials_RequireTransportSecurity_ReturnsFalse(t *testing.T) {
	creds := &OAuth2ClientCredentials{}
	if creds.RequireTransportSecurity() {
		t.Error("RequireTransportSecurity() = true, want false (plaintext h2c deployment)")
	}
}
