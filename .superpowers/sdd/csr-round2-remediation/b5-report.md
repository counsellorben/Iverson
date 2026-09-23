# Batch 5 remediation report — findings #14 and #15

## #14 — build-scope npm advisories

### Iverson.AdminUI

**Before** (`npm audit`):
- High: `nanoid` <3.3.18 (GHSA-2v37-7h3g-55p8, indefinite loop on size=0), pulled in transitively via `vite` → `postcss` → `nanoid@3.3.16`
- Moderate: `@vitest/mocker` / `vitest` (GHSA-82fw-gwwq-j7x9, path traversal / arbitrary file read via redirect mock), range `>=2.1.0 <4.1.11`, on installed `vitest@3.2.7`
- Total: 1 high, 2 moderate findings (3 total, mocker+vitest are the same advisory)

**Change:** added
```json
"overrides": { "nanoid": "^3.3.18" }
```
to `package.json`. `npm install` resolved `nanoid` to 3.3.19 (patch-level bump of a nested transitive dep, no primary-dep version change). `package-lock.json` updated accordingly.

**After** (`npm audit`):
- High: **cleared** (nanoid now 3.3.19)
- Moderate: **remains** — 2 findings (same advisory), `fixAvailable` reports `vitest@5.0.0` (`isSemVerMajor: true`)

**Why the moderate advisory was left open:** I tested overriding `@vitest/mocker` alone to `^4.1.11` (the first patched mocker release) while keeping `vitest` pinned at `^3.2.0`/3.2.7. npm installed it successfully (`@vitest/mocker@4.1.11 overridden`, no peer-dep conflict — its `vite` peer range `^6||^7||^8` is satisfied by our vite 8.1.5) and the existing test suite still passed with it in place. However `npm audit` still reported the moderate finding afterward: the GHSA's affected-package entry for `vitest` itself declares the vulnerable range as `2.1.0-beta.1 – 4.1.10` on the `vitest` package name/version, independent of which `@vitest/mocker` happens to be nested under it — so the advisory is keyed to the primary `vitest` version, not just its `@vitest/mocker` sub-dependency. Overriding only the transitive package cannot satisfy this audit finding; the only fix path is bumping `vitest` itself to `>=4.1.11`, a major version bump (3.x → 4.x) of a primary dev-tool dependency. Per the constraint against forcing a breaking major bump of vitest to clear a transitive-scoped advisory, I reverted the `@vitest/mocker` override (it bought no audit benefit and pairs mismatched internal API versions for no gain) and left this moderate finding open.

**Residual risk:** build/test-scope only (dev dependency, `@vitest/mocker`'s vulnerable code path — a path-traversal in vitest's browser-mode module mocking — is not exercised by anything in this repo's test setup, which runs in `jsdom` with no browser-mode mocking). Recommend revisiting when the project is ready to take vitest 4.x (a separate, scoped migration).

### Iverson.Clients/TypeScript

Same two advisories, same root causes (`nanoid@3.3.16` via `vite@7.3.6`→`postcss`; `@vitest/mocker@3.2.6` via `vitest@3.2.6`).

**Change:** added `"nanoid": "^3.3.18"` to the existing `overrides` block (which already had `protobufjs`):
```json
"overrides": {
  "protobufjs": "^7.6.5",
  "nanoid": "^3.3.18"
}
```

**After:** High (nanoid) cleared; moderate (vitest/mocker, GHSA-82fw-gwwq-j7x9) remains for the identical reason as AdminUI — only fixable via a vitest 3→4 major bump, which was not forced. `npm audit` output: 2 moderate findings, 0 high.

## #15 — remove refresh token from browser storage

File: `Iverson.AdminUI/src/auth/AuthProvider.tsx`

- Removed `offline_access` from the requested OIDC scope: `"openid profile email offline_access"` → `"openid profile email"`. No refresh token is requested, so `oidc-client-ts` never obtains or stores one in `sessionStorage`.
- Set `automaticSilentRenew: false` (was `true`). Without a refresh token, `oidc-client-ts` would otherwise fall back to hidden-iframe silent renewal against the Authentik authority; that iframe is already blocked by this app's CSP (`nginx.conf`: `default-src 'self'` with no `frame-src`), so leaving automatic renewal on would just be a futile, error-logging timer. The CSP was **not** loosened to permit the iframe — that would trade a smaller token-storage exposure for a larger framing exposure, which the task explicitly forbids.
- Added a comment directly above `oidcConfig` documenting the tradeoff: no refresh token / no silent renewal / session ends at access-token expiry requiring a fresh login, accepted because the console currently renders no tenant data (stub pages).
- Updated/added tests in `AuthProvider.test.tsx`: extended the `react-oidc-context` mock to also mock the `AuthProvider` export (capturing the props passed to it, since the existing mock only covered `useAuth`), and added two new tests:
  - asserts `offline_access` is not present in the requested `scope` string
  - asserts `automaticSilentRenew` is `false`
- No other references to `offline_access`, `automaticSilentRenew`, or refresh-token handling exist anywhere else in `Iverson.AdminUI/src` (grepped).

## Gate results

**Iverson.AdminUI**
- `npm test` (vitest run): 3 test files, **11 passed** (was 9; +2 new auth-config tests), 0 failed
- `npm run build` (vite build): succeeded, no errors
- `npm audit`: high 1→0 (cleared), moderate 2→2 (unchanged, documented above)

**Iverson.Clients/TypeScript**
- `npm test` (tsc typecheck + vitest run): typecheck clean, 9 test files, **249 passed**, 0 failed (unchanged test count — no test changes needed here, #15 doesn't touch this project)
- `npm audit`: high 1→0 (cleared), moderate 2→2 (unchanged, documented above)

## Files touched
- `Iverson.AdminUI/package.json` (+overrides.nanoid)
- `Iverson.AdminUI/package-lock.json`
- `Iverson.AdminUI/src/auth/AuthProvider.tsx` (scope/renew change + tradeoff comment)
- `Iverson.AdminUI/src/auth/AuthProvider.test.tsx` (new AuthProvider describe block, 2 tests)
- `Iverson.Clients/TypeScript/package.json` (+overrides.nanoid, alongside existing protobufjs override)
- `Iverson.Clients/TypeScript/package-lock.json`
