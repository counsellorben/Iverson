package main

import (
	"context"
	"testing"

	"github.com/iverson/clients/go/iverson"
)

// The service token authorizes RegisterSchema (via its schema_admin scope), but every row
// write is authorized against the ACTING user, which travels in a second header. Dropping it
// makes the server see actor=unknown and deny every create with PermissionDenied — while
// registration still succeeds, so the failure surfaces phases away from its cause.
func TestStaticServiceTokenEmitsActingUserFromContext(t *testing.T) {
	creds := staticServiceToken{token: "svc", actingToken: "acting"}
	ctx := iverson.WithActingUserToken(context.Background(), "acting")

	md, err := creds.GetRequestMetadata(ctx)
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}

	if got := md["authorization"]; got != "Bearer svc" {
		t.Errorf("service identity: got %q, want %q", got, "Bearer svc")
	}
	if got := md[iverson.ActingUserMetadataKey]; got != "Bearer acting" {
		t.Errorf("acting-user identity: got %q, want %q", got, "Bearer acting")
	}
}

// No acting token configured must emit no acting-user header at all, rather than an empty
// "Bearer ": the server rejects a present-but-invalid token outright.
func TestStaticServiceTokenOmitsActingUserWhenUnset(t *testing.T) {
	creds := staticServiceToken{token: "svc"}

	md, err := creds.GetRequestMetadata(context.Background())
	if err != nil {
		t.Fatalf("GetRequestMetadata: %v", err)
	}

	if _, present := md[iverson.ActingUserMetadataKey]; present {
		t.Errorf("acting-user header present with no acting token: %q", md[iverson.ActingUserMetadataKey])
	}
}
