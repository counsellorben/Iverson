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
	if actingUserToken, ok := ctx.Value(actingUserTokenKey{}).(string); ok && actingUserToken != "" {
		md[ActingUserMetadataKey] = "Bearer " + actingUserToken
	}
	return md, nil
}

// RequireTransportSecurity returns false: this repo's deployment is plaintext h2c with
// no TLS anywhere in the stack — confirmed via grpc-go's http2_client.go getCallAuthData
// that this still allows the credential through on a plaintext channel, unlike a
// channel-construction-time TLS gate.
func (c *OAuth2ClientCredentials) RequireTransportSecurity() bool {
	return false
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
