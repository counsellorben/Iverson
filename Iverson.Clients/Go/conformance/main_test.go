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

// The harness passes the secret flags in the environment rather than on the command line; a flag
// given on the command line still wins, so a driver run by hand keeps working.
func TestOptionalSecretFlagEnvironmentFallback(t *testing.T) {
	secretFlags := map[string]string{
		"--client-secret":      "IVERSON_DRIVER_CLIENT_SECRET",
		"--service-token":      "IVERSON_DRIVER_SERVICE_TOKEN",
		"--acting-token":       "IVERSON_DRIVER_ACTING_TOKEN",
		"--wrong-acting-token": "IVERSON_DRIVER_WRONG_ACTING_TOKEN",
	}
	for flag, variable := range secretFlags {
		t.Run(flag, func(t *testing.T) {
			t.Setenv(variable, "ENV")
			if got := parseArgs(nil).optional(flag); got != "ENV" {
				t.Errorf("flag absent: got %q, want %q", got, "ENV")
			}
			if got := parseArgs([]string{flag, "FLAG"}).optional(flag); got != "FLAG" {
				t.Errorf("flag present: got %q, want %q", got, "FLAG")
			}
			t.Setenv(variable, "")
			if got := parseArgs(nil).optional(flag); got != "" {
				t.Errorf("variable empty: got %q, want empty", got)
			}
		})
	}
}

func TestOptionalNeverReadsTheEnvironmentForANonSecretFlag(t *testing.T) {
	t.Setenv("IVERSON_DRIVER_CLIENT_ID", "ENV")
	if got := parseArgs(nil).optional("--client-id"); got != "" {
		t.Errorf("got %q, want empty", got)
	}
}
