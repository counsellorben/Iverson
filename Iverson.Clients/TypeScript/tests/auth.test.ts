import { describe, it, expect, vi } from 'vitest';
import { createOAuth2ClientCredentials } from '../src/auth.js';

describe('createOAuth2ClientCredentials', () => {
    it('rejects plaintext token endpoints without allowInsecureCredentials opt-in', () => {
        expect(() => {
            createOAuth2ClientCredentials('id', 'secret', 'http://localhost:9000/application/o/token/');
        }).toThrow(/Refusing to send OAuth2 client credentials to a non-https token endpoint/);
    });

    it('allows plaintext token endpoints with allowInsecureCredentials=true', () => {
        expect(() => {
            createOAuth2ClientCredentials('id', 'secret', 'http://localhost:9000/application/o/token/', undefined, true);
        }).not.toThrow();
    });

    it('attaches a Bearer token from the token endpoint', async () => {
        globalThis.fetch = vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({ access_token: 'test-token', expires_in: 3600 }),
        }) as unknown as typeof fetch;

        const creds = createOAuth2ClientCredentials('id', 'secret', 'http://localhost:9000/application/o/token/', undefined, true);

        const metadata = await (creds as any).generateMetadata({ method_name: '', service_url: '' });

        expect(metadata.get('authorization')).toEqual(['Bearer test-token']);
    });
});
