# Admin UI Styling Design

## Problem

`Iverson.AdminUI` currently has zero styling — no CSS files, no component library, and every page renders as bare unstyled `<div>`s (`PerformancePage`, `StoragePage`, etc. are literal "Coming soon" stubs; `AppLayout`/`Sidebar` are plain `<div>`/`<nav>` elements). This design picks a styling foundation, applies it to the existing app shell, and establishes a matching brand on Authentik's login/consent pages so the two feel like one product.

## Requirements

- Polished, branded look (not speed-only or minimal-footprint-only).
- A React component library, not a utility-CSS-only or headless-primitives approach.
- The chosen library must coexist with D3 for future chart components.
- The same brand (colors, logo, fonts) applied to Authentik's login/consent flow pages — not pixel-level component parity, since Authentik is a separate Lit/PatternFly frontend, not React.

## Chosen approach: MUI

Compared against Mantine and Ant Design (see "Library comparison" below); MUI is the only option with zero migration cost against this app's installed React version *and* the strongest verified D3 lineage in its own charting package.

### Architecture

A single source of truth, `Iverson.AdminUI/src/theme/tokens.ts`, holds the brand's primitive values (colors, font). Two independent consumers read from it:

1. **MUI theme** (`src/theme/theme.ts`) — `createTheme()` mapping tokens onto MUI's palette/typography, wired into `main.tsx` via `<ThemeProvider>` + `<CssBaseline>`.
2. **Authentik's Brand** — a new blueprint entry setting `branding_title`/`branding_custom_css` on the existing `authentik-default` Brand row, using the same token values by hand (no shared build step links a TS file to a Python-side YAML blueprint — this is a "change both, same values" convention).

There is no automated mechanism keeping the two in sync; if the palette changes, both files need updating.

### Brand tokens

| Token | Value | Use |
|---|---|---|
| `primary` | `#006BB6` | Buttons, active nav item, links |
| `secondary` | `#ED174C` | Accents, secondary actions |
| `background` (dark) | `#010101` | App shell surface |
| `font` (heading) | Fraunces, Black (900) weight, self-hosted via `@fontsource/fraunces` | Placeholder for **ITC Clearface Black** — a commercial Monotype/ITC typeface with no license currently available. Fraunces is an open-source (SIL) display serif with a similar warm, high-contrast old-style character, used as a stand-in until licensed files/subscription exist. Swapping it in later is a one-line change to `tokens.ts`. |

No dark/light toggle — not requested; the app is dark-only for now (`palette.mode` stays at MUI's default). Self-hosting the font (rather than a Google Fonts `<link>`) matches this repo's existing pattern of nothing calling out to an external host at runtime (local Ollama embeddings, no external API keys — see root `README.md`).

### Dependencies added

`Iverson.AdminUI/package.json`:
```
@mui/material
@emotion/react
@emotion/styled
@fontsource/fraunces
```
(`@mui/icons-material` was considered and dropped — nothing in this design uses an icon; see "Verified assumptions.")

### Admin UI shell restyle

- **`main.tsx`**: wraps the existing tree in `<ThemeProvider theme={theme}><CssBaseline />...</ThemeProvider>`, outside `<AuthProvider>` (theme doesn't depend on auth state).
- **`AppLayout.tsx`**: `<AppBar position="static">` + `<Toolbar>` for the header (user email + an MUI `<Button>` for logout, replacing the bare `<header>`); `<Box sx={{ display: 'flex' }}>` wrapping `<Sidebar>` + `<Box component="main">` for the two-column shell.
- **`Sidebar.tsx`**: an MUI `<Drawer variant="permanent">` containing a `<List>` of `<ListItemButton component={Link} to="...">` + `<ListItemText>` per nav link, replacing the bare `<nav>`/`<Link>` list. Still driven by the same `groups`-based conditional rendering already there. `component={Link}` is MUI's standard polymorphic-component pattern (`ButtonBase`'s `component` prop, which `ListItemButton` inherits) — it renders react-router's `Link`, which renders a real `<a href>`, so `getByRole('link', ...)` in the existing tests keeps matching unchanged.

No changes to `router.tsx`, `AuthProvider.tsx`, or the page components — they stay as "Coming soon" stubs. Restyling empty stubs beyond the shared shell isn't meaningful work yet.

### Authentik brand blueprint

Applied to **both** `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` and `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml` (kind) — same config-as-code convention already used for every provider/application in those files. Scope: login/consent flow pages only, not Authentik's own internal admin interface (only the Iverson team touches that; no end-user-facing benefit to branding it).

```yaml
- model: authentik_brands.brand
  identifiers:
    domain: authentik-default
  attrs:
    branding_title: "Iverson"
    branding_custom_css: |
      :root, html[data-theme=dark] {
        --pf-global--primary-color--100: #006BB6;
        --pf-global--primary-color--200: #006BB6;
        --pf-global--primary-color--300: #006BB6;
        --pf-global--primary-color--dark-100: #006BB6;
        --pf-global--primary-color--light-100: #006BB6;
        --pf-global--secondary-color--100: #ED174C;
        --ak-dark-background: #010101;
        --ak-dark-background-light: #0A0A0A;
        --ak-dark-background-lighter: #141414;
        --ak-font-family-sans-serif: 'Fraunces', serif;
        --ak-generic-display: 'Fraunces', serif;
      }
```

`branding_logo`/`branding_favicon` are left at Authentik's defaults — no logo asset exists or was requested.

Applying the blueprint change follows the mechanism already proven working this session: editing the blueprint file (bind-mounted into the container) and running `docker compose restart authentik-worker`, which re-triggers `blueprints_discovery` → `apply_blueprint` immediately rather than waiting for the periodic interval.

### D3 integration convention (documented, not built)

No chart component exists yet — `PerformancePage` has no data source or design of its own, and building one is out of scope for this styling design. The convention to follow when one is built:

> A chart component owns a single `<div ref={...}>` mounted once by React; all D3 calls (scales, shapes, transitions) happen inside a `useEffect` that runs against that ref and cleans up on unmount. No MUI (or other React) state re-render touches that subtree — React mounts the container once and D3 owns everything inside it from then on.

This is the standard "React mounts the doorway, D3 owns the room" pattern, not specific to MUI. Recurs identically for every future chart component — one rule, not bespoke handling per chart.

### Testing and verification

- Existing tests (`Sidebar.test.tsx`, `router.test.tsx`) — confirmed compatible (see "Verified assumptions"); re-run after the restyle, no rewrites needed.
- Admin UI visual check: `npm run dev`, confirm the AppBar/Sidebar render in the new palette with Fraunces loaded — dev-server check, not automated (no new logic to unit-test; a theme object and a CSS string aren't meaningfully unit-testable).
- Authentik check: after the blueprint change, `docker compose restart authentik-worker`, then load `http://localhost:9000/if/flow/default-authentication-flow/` and confirm the CSS variables resolve to the new colors.

## Library comparison

All three candidates were checked against this app's actual `package.json` (`react: ^18.3.0`) and against real npm dependency trees — not assumed from general reputation.

| | MUI | Mantine | Ant Design |
|---|---|---|---|
| React 18.3 compatible today | Yes (`^17\|^18\|^19`) | **No** — current major (`@mantine/core` 9.4.2) requires React `^19.2.0`. An older major (v7.17.8) supports React 18, but that's two majors behind. | Yes (`>=18`) |
| D3 in its charting package | `@mui/x-charts` → `@mui/x-charts-vendor` directly vendors `d3-shape`, `d3-scale`, `d3-interpolate`, `d3-array`, `d3-path`, `d3-time` | Mantine's chart package wraps Recharts → `victory-vendor`, which vendors the same D3 primitives — D3-based, two hops removed | `@ant-design/plots` depends entirely on AntV's `@antv/g2`/`@antv/g` — **no D3 anywhere in the chain** |
| Verdict | **Chosen** — no migration cost, strongest D3 precedent | Ruled out for now — React 19 upgrade is a real, separate prerequisite not requested here | Legitimate second choice (strong for data-dense tables/forms), but its chart ecosystem has no D3 relationship, cutting against the stated requirement |

## Verified assumptions

All checked empirically against the actual codebase/running services before this spec was written (not assumed):

1. **`@mui/material` + `@emotion/react` + `@emotion/styled` install cleanly** against the current `package.json` — `npm install --dry-run` in `Iverson.AdminUI/` completed with no `ERESOLVE`/peer-dependency warnings.
2. **`@mui/icons-material` is not actually used anywhere in the design** — self-check against the approved Section 2/3 content found no icon usage (plain `Button`, not `IconButton`, for logout; no icons in the nav list). Dropped from the dependency list.
3. **`@fontsource/fraunces` exists and ships a 900 (Black) weight** — confirmed on the npm registry (v5.3.0) and by downloading and inspecting the actual package tarball, which contains `900.css`, `latin-900.css`, `latin-ext-900.css`, `vietnamese-900.css`.
4. **`authentik_brands.brand` is the correct blueprint model identifier** — derived directly from the running Authentik instance's own Django metadata: `Brand._meta.app_label` = `authentik_brands`, `Brand._meta.model_name` = `brand`.
5. **`domain` is a valid `identifiers:` field for blueprint-matching an existing Brand row** — confirmed by reading Authentik's blueprint importer source (`/authentik/blueprints/v1/importer.py`): `identifiers` is a generic dict used to build an OR'd query across arbitrary model fields, not restricted to specific "natural key" fields (the same generic mechanism every other blueprint entry in this repo already relies on).
6. **Kind's Authentik deployment has an equivalent default Brand with the same `domain` value** — the `authentik-default` domain string is not set by this repo's Helm chart or by any deployment-specific config; it appears in Authentik's own upstream test fixtures (`/authentik/brands/tests.py`) as the fixed default. Since compose and kind run the same upstream image and migrations, this transfers; a live kind cluster wasn't available to double-check directly during this session (its API server was unreachable), so this rests on upstream/image-level evidence rather than a live kind inspection.
7. **MUI v9.2.0's theming API matches what the design uses** — downloaded and inspected the actual package tarball; `createTheme`, `ThemeProvider`, and `CssBaseline` all exist as real exports at the expected paths.
8. **MUI's polymorphic `component` prop pattern works for the `ListItemButton` + react-router `Link` integration** — confirmed via source: `ButtonBase` (which `ListItemButton` is built on) accepts a `component` prop (default `'button'`, otherwise any component type with props forwarded through), the standard documented MUI pattern.
9. **Authentik's `branding_custom_css` injection point and cascade precedence** — fetched the full rendered HTML of `/if/flow/default-authentication-flow/` and found a server-rendered `<style data-id="brand-css"></style>` element positioned after the `flow-2026.5.3.css` `<link>` in document order. Django renders the Brand's `branding_custom_css` value verbatim into this tag (not injected by client-side JS). Because it comes later in document order, equal-specificity rules (like our `:root`/`html[data-theme=dark]` overrides) win the cascade without needing `!important`.
10. **No other admin-ui test breaks from this design** — enumerated all three test files in the project (`Sidebar.test.tsx`, `router.test.tsx`, `AuthProvider.test.tsx`; confirmed no others exist via `find`). `AuthProvider.test.tsx` renders `AuthGate` directly with a plain `<div>` child, never through `main.tsx`/`AppLayout`, so it's entirely unaffected by the `ThemeProvider` wrap or the shell restyle.

## Known open item

ITC Clearface Black requires a licensed font file or a Monotype/Adobe Fonts subscription with web-embedding rights for this domain — neither exists yet. Fraunces (Black) is the interim substitute; swapping in the real typeface later only requires updating `tokens.ts` and the Authentik blueprint's `custom_css` font-family values once licensing is sorted.
