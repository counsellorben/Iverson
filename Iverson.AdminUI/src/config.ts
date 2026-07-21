declare global {
  interface Window { __ADMIN_UI_CONFIG__?: Record<string, string> }
}

function readConfig(key: string): string {
  const runtime = window.__ADMIN_UI_CONFIG__?.[key];
  if (runtime) return runtime;
  const buildTime = import.meta.env[`VITE_${key}`];
  if (buildTime) return buildTime;
  throw new Error(`Missing admin-ui config: ${key}`);
}

export const config = {
  oidcClientId: readConfig("OIDC_CLIENT_ID"),
  oidcAuthority: readConfig("OIDC_AUTHORITY"),
  apiBaseUrl: readConfig("API_BASE_URL"),
};
