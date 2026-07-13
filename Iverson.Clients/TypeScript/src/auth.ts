/**
 * OAuth2 client-credentials support for gRPC calls.
 */
import * as grpc from '@grpc/grpc-js';

interface TokenResponse {
    access_token: string;
    expires_in: number;
}

/**
 * Creates a grpc.CallCredentials that attaches an OAuth2 client-credentials Bearer
 * token to each RPC, fetching and caching the token in memory and refreshing it
 * 60 seconds before expiry.
 */
export function createOAuth2ClientCredentials(
    clientId: string,
    clientSecret: string,
    tokenEndpoint: string,
    scope?: string,
): grpc.CallCredentials {
    let cachedToken: string | null = null;
    let expiresAt = 0;
    let pending: Promise<string> | null = null;

    async function getToken(): Promise<string> {
        if (cachedToken !== null && Date.now() < expiresAt) {
            return cachedToken;
        }
        if (pending !== null) {
            return pending;
        }
        pending = (async () => {
            const body = new URLSearchParams({
                grant_type: 'client_credentials',
                client_id: clientId,
                client_secret: clientSecret,
                ...(scope ? { scope } : {}),
            });
            const response = await fetch(tokenEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body,
            });
            if (!response.ok) {
                throw new Error(`Failed to acquire Iverson client token: HTTP ${response.status}`);
            }
            const payload = (await response.json()) as TokenResponse;
            cachedToken = payload.access_token;
            expiresAt = Date.now() + (payload.expires_in - 60) * 1000;
            return cachedToken;
        })();
        try {
            return await pending;
        } finally {
            pending = null;
        }
    }

    return grpc.credentials.createFromMetadataGenerator((_options, callback) => {
        getToken()
            .then((token) => {
                const metadata = new grpc.Metadata();
                metadata.add('authorization', `Bearer ${token}`);
                callback(null, metadata);
            })
            .catch((err) => callback(err));
    });
}
