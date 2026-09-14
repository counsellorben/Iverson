package iverson

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

func stringPtr(s string) *string { return &s }

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

func TestOAuth2ClientCredentials_RequireTransportSecurity_DefaultsToTrue(t *testing.T) {
	creds := &OAuth2ClientCredentials{}
	if !creds.RequireTransportSecurity() {
		t.Error("RequireTransportSecurity() = false, want true (default must enforce transport security)")
	}
}

func TestOAuth2ClientCredentials_RequireTransportSecurity_FalseWhenOptedIn(t *testing.T) {
	creds := &OAuth2ClientCredentials{AllowInsecureCredentials: true}
	if creds.RequireTransportSecurity() {
		t.Error("RequireTransportSecurity() = true, want false when AllowInsecureCredentials is set")
	}
}

// TestNewIversonClient_PlaintextWithCredentials_FailsWithoutOptIn pins the
// construction-time behavior the opt-in exists to preserve: grpc-go's own
// ClientConn.validateTransportCredentials refuses to build a channel that
// combines insecure transport credentials with a PerRPCCredentials that
// requires transport security (the default), so the Bearer token can never
// ride a plaintext channel silently.
func TestNewIversonClient_PlaintextWithCredentials_FailsWithoutOptIn(t *testing.T) {
	client, err := NewIversonClient("127.0.0.1:0",
		grpc.WithTransportCredentials(insecure.NewCredentials()),
		grpc.WithPerRPCCredentials(&OAuth2ClientCredentials{
			ClientID: "id", ClientSecret: "secret", TokenEndpoint: "http://example.invalid",
		}),
	)
	if err == nil {
		client.Close()
		t.Fatal("NewIversonClient succeeded, want an error: plaintext channel + credentials with no opt-in must be refused at construction")
	}
}

// TestNewIversonClient_PlaintextWithCredentials_SucceedsWithOptIn is the
// matching positive leg: the same combination succeeds once the caller has
// explicitly set AllowInsecureCredentials, mirroring the conformance driver's
// deliberate dev/test use of a plaintext h2c channel with credentials.
func TestNewIversonClient_PlaintextWithCredentials_SucceedsWithOptIn(t *testing.T) {
	client, err := NewIversonClient("127.0.0.1:0",
		grpc.WithTransportCredentials(insecure.NewCredentials()),
		grpc.WithPerRPCCredentials(&OAuth2ClientCredentials{
			ClientID: "id", ClientSecret: "secret", TokenEndpoint: "http://example.invalid",
			AllowInsecureCredentials: true,
		}),
	)
	if err != nil {
		t.Fatalf("NewIversonClient failed with opt-in set: %v", err)
	}
	client.Close()
}

func TestGetRequestMetadata_CtxTokenWinsOverDefault(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{
		ClientID:               "id",
		ClientSecret:           "secret",
		TokenEndpoint:          server.URL,
		DefaultActingUserToken: stringPtr("ambient"),
	}

	ctx := WithActingUserToken(context.Background(), "percall")
	md, err := creds.GetRequestMetadata(ctx)
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if md[ActingUserMetadataKey] != "Bearer percall" {
		t.Errorf("got %q, want %q", md[ActingUserMetadataKey], "Bearer percall")
	}
}

func TestGetRequestMetadata_DefaultAppliesWhenCtxHasNone(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{
		ClientID:               "id",
		ClientSecret:           "secret",
		TokenEndpoint:          server.URL,
		DefaultActingUserToken: stringPtr("ambient"),
	}

	md, err := creds.GetRequestMetadata(context.Background())
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if md[ActingUserMetadataKey] != "Bearer ambient" {
		t.Errorf("got %q, want %q", md[ActingUserMetadataKey], "Bearer ambient")
	}
}

func TestGetRequestMetadata_ExplicitEmptyPerCallTokenEmitsLoudBearer(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{
		ClientID:      "id",
		ClientSecret:  "secret",
		TokenEndpoint: server.URL,
	}

	ctx := WithActingUserToken(context.Background(), "")
	md, err := creds.GetRequestMetadata(ctx)
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if md[ActingUserMetadataKey] != "Bearer " {
		t.Errorf("got %q, want %q", md[ActingUserMetadataKey], "Bearer ")
	}
}

func TestGetRequestMetadata_ExplicitEmptyPerCallTokenDoesNotFallThroughToDefault(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{
		ClientID:               "id",
		ClientSecret:           "secret",
		TokenEndpoint:          server.URL,
		DefaultActingUserToken: stringPtr("ambient"),
	}

	ctx := WithActingUserToken(context.Background(), "")
	md, err := creds.GetRequestMetadata(ctx)
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if md[ActingUserMetadataKey] != "Bearer " {
		t.Errorf("got %q, want %q (must not fall through to ambient default)", md[ActingUserMetadataKey], "Bearer ")
	}
}

func TestGetRequestMetadata_AmbientEmptyPointerEmitsLoudBearer(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{
		ClientID:               "id",
		ClientSecret:           "secret",
		TokenEndpoint:          server.URL,
		DefaultActingUserToken: stringPtr(""),
	}

	md, err := creds.GetRequestMetadata(context.Background())
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if md[ActingUserMetadataKey] != "Bearer " {
		t.Errorf("got %q, want %q", md[ActingUserMetadataKey], "Bearer ")
	}
}

func TestGetRequestMetadata_NoTokenAnywhereOmitsHeader(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(tokenResponse{AccessToken: "test-token", ExpiresIn: 3600})
	}))
	defer server.Close()

	creds := &OAuth2ClientCredentials{
		ClientID:      "id",
		ClientSecret:  "secret",
		TokenEndpoint: server.URL,
	}

	md, err := creds.GetRequestMetadata(context.Background())
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}
	if _, exists := md[ActingUserMetadataKey]; exists {
		t.Errorf("ActingUserMetadataKey should be absent from metadata, but got %q", md[ActingUserMetadataKey])
	}
}
