package iverson

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

type actingUserTokenKey struct{}

// ActingUserMetadataKey is the gRPC metadata key carrying the acting-user's
// own Authentik-issued access token, set via WithActingUserToken.
const ActingUserMetadataKey = "x-acting-user-authorization"

// WithActingUserToken attaches a per-call acting-user token to ctx, read by
// OAuth2ClientCredentials.GetRequestMetadata and forwarded as a second gRPC
// metadata entry alongside the service credential.
func WithActingUserToken(ctx context.Context, token string) context.Context {
	return context.WithValue(ctx, actingUserTokenKey{}, token)
}

// OAuth2ClientCredentials implements credentials.PerRPCCredentials, attaching an
// OAuth2 client-credentials Bearer token to every RPC. The token is fetched lazily
// and cached in memory, refreshing 60 seconds before expiry.
type OAuth2ClientCredentials struct {
	ClientID      string
	ClientSecret  string
	TokenEndpoint string
	Scope         string
	// DefaultActingUserToken is the ambient acting-user token attached when no
	// per-call token is present in ctx. nil means no ambient identity is
	// configured. A non-nil pointer to an empty string is a caller error,
	// deliberately forwarded so the server rejects it loudly rather than the
	// client silently swallowing it into "no identity".
	DefaultActingUserToken *string

	// AllowInsecureCredentials is the explicit, named opt-in required to send
	// this credential's Bearer token over a plaintext (non-TLS) channel.
	// False by default: RequireTransportSecurity reports true, and grpc-go's
	// own ClientConn.validateTransportCredentials refuses to Dial/NewClient at
	// all when insecure transport credentials are combined with a PerRPCCredentials
	// that requires transport security — the token never goes out in the clear.
	// Set true only for a known-local, non-TLS endpoint (e.g. dev/test h2c).
	AllowInsecureCredentials bool

	mu        sync.Mutex
	token     string
	expiresAt time.Time
}

type tokenResponse struct {
	AccessToken string `json:"access_token"`
	ExpiresIn   int64  `json:"expires_in"`
}

func (c *OAuth2ClientCredentials) GetRequestMetadata(ctx context.Context, _ ...string) (map[string]string, error) {
	token, err := c.getToken(ctx)
	if err != nil {
		return nil, err
	}
	md := map[string]string{"authorization": "Bearer " + token}
	// An explicitly-supplied per-call token (including "") must win and be
	// forwarded as-is: an empty token is a caller error that must reach the
	// server and be rejected loudly, and must never silently fall through to
	// the ambient default below.
	if actingUserToken, ok := ctx.Value(actingUserTokenKey{}).(string); ok {
		md[ActingUserMetadataKey] = "Bearer " + actingUserToken
	} else if c.DefaultActingUserToken != nil {
		// A non-nil pointer to "" is deliberately forwarded as "Bearer " so
		// the server rejects it loudly, rather than being swallowed here.
		md[ActingUserMetadataKey] = "Bearer " + *c.DefaultActingUserToken
	}
	return md, nil
}

// RequireTransportSecurity reports true unless AllowInsecureCredentials has been
// explicitly set. Returning true here is what makes grpc-go's own guard bite: at
// Dial/NewClient time, ClientConn.validateTransportCredentials refuses to build a
// channel that combines insecure transport credentials with a PerRPCCredentials
// that requires transport security, so the Bearer token can never ride a plaintext
// channel by default. A caller that genuinely needs a local, non-TLS endpoint must
// set AllowInsecureCredentials to true to defeat this guard deliberately.
func (c *OAuth2ClientCredentials) RequireTransportSecurity() bool {
	return !c.AllowInsecureCredentials
}

func (c *OAuth2ClientCredentials) getToken(ctx context.Context) (string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.token != "" && time.Now().Before(c.expiresAt) {
		return c.token, nil
	}

	form := url.Values{}
	form.Set("grant_type", "client_credentials")
	form.Set("client_id", c.ClientID)
	form.Set("client_secret", c.ClientSecret)
	if c.Scope != "" {
		form.Set("scope", c.Scope)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.TokenEndpoint, strings.NewReader(form.Encode()))
	if err != nil {
		return "", fmt.Errorf("building token request: %w", err)
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return "", fmt.Errorf("requesting token: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("failed to acquire Iverson client token: HTTP %d", resp.StatusCode)
	}

	var body tokenResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return "", fmt.Errorf("decoding token response: %w", err)
	}

	c.token = body.AccessToken
	c.expiresAt = time.Now().Add(time.Duration(body.ExpiresIn-60) * time.Second)
	return c.token, nil
}
