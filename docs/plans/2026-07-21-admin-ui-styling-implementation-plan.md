# Admin UI Styling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-21-admin-ui-styling-design.md` (commit SHA: `04b15461881a3aa2b6547776edc0b7fa4a04a096`)

**Goal:** Give `Iverson.AdminUI` a styled, branded shell using MUI, and apply the same brand to Authentik's login/consent pages.

**Architecture:** A single token file (`src/theme/tokens.ts`) feeds an MUI theme (`src/theme/theme.ts`) wired into the app via `ThemeProvider`/`CssBaseline`, and feeds — by hand, no automated sync — an Authentik Brand blueprint entry setting matching CSS custom properties on Authentik's login pages.

**Tech stack:** React 18.3, MUI 9 (`@mui/material`, `@emotion/react`, `@emotion/styled`), `@fontsource/fraunces`, Vite, Vitest, Authentik 2026.5.3 blueprints.

---

## Global Constraints

Brand token values every task must use identically — these appear in both the MUI theme (Task 1) and the Authentik blueprint's `branding_custom_css` (Task 4); a mismatch between them defeats the point of a shared brand:

- `primary`: `#006BB6`
- `secondary`: `#ED174C`
- `background` (default): `#010101`
- `background` (paper): `#141414`
- Heading font: Fraunces, weight 900 (Black) — placeholder for ITC Clearface Black pending licensing

## File Structure

- Create: `Iverson.AdminUI/src/theme/tokens.ts` — brand primitive values
- Create: `Iverson.AdminUI/src/theme/theme.ts` — MUI `createTheme()` wiring
- Modify: `Iverson.AdminUI/package.json` — add 4 runtime dependencies
- Modify: `Iverson.AdminUI/src/main.tsx` — font import, `ThemeProvider`/`CssBaseline` wrap
- Modify: `Iverson.AdminUI/src/layout/AppLayout.tsx` — `AppBar`/`Toolbar` header, flex shell
- Modify: `Iverson.AdminUI/src/layout/Sidebar.tsx` — `Drawer`/`List` nav
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` — Brand blueprint entry
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml` — Brand blueprint entry (kind)

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time; not re-verified here (see spec's "Verified assumptions" section for full evidence):

- `@mui/material` + `@emotion/react` + `@emotion/styled` install cleanly against this app's React 18.3.
- `@mui/icons-material` is not needed — nothing in the design uses an icon.
- `@fontsource/fraunces` exists and ships a 900 (Black) weight.
- `authentik_brands.brand` is the correct blueprint model identifier; `domain` is a valid `identifiers:` matching field.
- Kind's default Brand shares the `authentik-default` domain (upstream-image evidence, not live-kind-verified).
- MUI v9.2.0's `createTheme`/`ThemeProvider`/`CssBaseline` API surface matches the design.
- `ListItemButton`'s polymorphic `component` prop renders react-router's `Link` as a real `<a href>`.
- Authentik renders `branding_custom_css` verbatim into a server-rendered `<style data-id="brand-css">` tag, positioned after `flow-2026.5.3.css` in document order — no `!important` needed.
- No existing admin-ui test breaks from the restyle (`Sidebar.test.tsx`, `router.test.tsx`, `AuthProvider.test.tsx` enumerated).
- MUI's actual default `palette.mode` is `'light'`, not dark — this plan's theme sets it explicitly (see Global Constraints / Task 1).

## Verified plan-level assumptions

Newly introduced by this plan; verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.AdminUI/src/theme/` does not yet exist | `ls src/theme` → "No such file or directory" |
| 2 | File path | `package.json`, `main.tsx`, `AppLayout.tsx`, `Sidebar.tsx` current content matches this plan's diffs | Fresh `Read` of all four files this session |
| 3 | API signature | `createTheme()`'s `palette.background` accepts a `paper` sub-key | `@mui/material@9.2.0`'s `styles/createPalette.js:38,73` define `paper` in both light/dark default background objects |
| 4 | Command | `npm test` → `vitest run`, discovers all 3 existing `*.test.tsx` files | `package.json` scripts; `vitest.config.ts` has no `include` override, so Vitest's default glob applies |
| 5 | Command | `npm run build` → `vite build`, a valid existing command | `package.json` scripts |
| 6 | Task ordering | Task 4 (YAML-only) introduces nothing Task 3 imports; Task 2's diff (`main.tsx`) doesn't reference anything Task 3 creates | Read of `main.tsx`: imports `./router`, not `AppLayout`/`Sidebar` directly; Task 2's diff only touches theme/font wiring |
| 7 | Code validity | `@fontsource/fraunces/900.css` is the correct import path | Package's own `README.md`: `import "@fontsource/fraunces/900.css"; // Specify weight` |
| 8 | Code validity | `Drawer variant="permanent"` flows in normal flex layout (not `position: fixed`), so `Box sx={{display:'flex'}}` + `Box component="main" sx={{flexGrow:1}}` needs no extra width coordination | `@mui/material@9.2.0`'s `Drawer/Drawer.js:61-69`: the permanent variant's `DrawerDockedRoot` style is `{ flex: '0 0 auto' }` only — the `position: fixed` style elsewhere in the file is explicitly commented `// temporary style` and applies only to the temporary/modal variant's Paper |
| 9 | Consumer impact (Cat 6) | `Sidebar.test.tsx`'s `getByRole('link', {name: 'Performance'})` etc. still match `ListItemButton component={Link}` + `ListItemText` | `ButtonBase`'s polymorphic `component` prop renders `Link` → real `<a href>` (spec-inherited); `ListItemText`'s rendered text is descendant content of that anchor, so accessible-name computation is unaffected |
| 10 | Consumer impact (Cat 6) | Nothing else in the admin-ui codebase depends on `AppLayout`/`Sidebar`'s current plain `header`/`nav` DOM tags | `grep` for `querySelector`, `getByRole("navigation"\|"banner")`, and bare `'header'`/`'nav'` string references across `src/` outside the two files themselves — no hits |
| 11 | YAML validity | Inserting the new Brand block at the identified points preserves valid list indentation in both files | Direct `sed` inspection of both files: compose's `iverson-api` application block ends at line 164 (2-space list items); kind's ends at line 131 (6-space list items, nested under Helm nesting) |
| 12 | Dependency resolution | `@mui/material@^9.2.0` + `@emotion/react@^11.14.0` + `@emotion/styled@^11.14.1` + `@fontsource/fraunces@^5.3.0` install together without `ERESOLVE` conflicts | `npm install --dry-run` with all 4 pinned versions in `Iverson.AdminUI/`: clean, no peer-dependency warnings |
| 13 | Sibling sweep | The Brand model has no environment-specific (`ingressHost`-dependent) fields relevant to this change, so the identical block is correct for both compose and kind | Brand model fields (`domain`, `branding_title`, `branding_custom_css`, etc., per spec's Verified Assumption #4) contain nothing analogous to `OAuth2Provider.redirect_uris`, which is the only field in this chart that needs `{{ .Values.global.ingressHost }}` templating |

## Tasks

### Task 1: Dependencies and theme foundation

**Files:**
- Modify: `Iverson.AdminUI/package.json`
- Create: `Iverson.AdminUI/src/theme/tokens.ts`
- Create: `Iverson.AdminUI/src/theme/theme.ts`

**Interfaces:**
- Produces: `tokens` (from `tokens.ts`) and `theme` (from `theme.ts`), consumed by Task 2 (`theme`) and Task 3 (`tokens.fontHeading`).

- [ ] **Step 1: Add the 4 new dependencies to `package.json`**

In the `dependencies` object (after `"react-oidc-context": "^3.3.1",` — position doesn't matter, just add alongside the existing runtime deps):
```json
"@mui/material": "^9.2.0",
"@emotion/react": "^11.14.0",
"@emotion/styled": "^11.14.1",
"@fontsource/fraunces": "^5.3.0",
```
Then run:
```bash
cd Iverson.AdminUI && npm install
```

- [ ] **Step 2: Create `src/theme/tokens.ts`**
```ts
export const tokens = {
  primary: "#006BB6",
  secondary: "#ED174C",
  backgroundDefault: "#010101",
  backgroundPaper: "#141414",
  fontHeading: "Fraunces, serif",
} as const;
```

- [ ] **Step 3: Create `src/theme/theme.ts`**
```ts
import { createTheme } from "@mui/material/styles";
import { tokens } from "./tokens";

export const theme = createTheme({
  palette: {
    mode: "dark",
    primary: { main: tokens.primary },
    secondary: { main: tokens.secondary },
    background: {
      default: tokens.backgroundDefault,
      paper: tokens.backgroundPaper,
    },
  },
});
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.AdminUI/package.json Iverson.AdminUI/package-lock.json Iverson.AdminUI/src/theme/tokens.ts Iverson.AdminUI/src/theme/theme.ts
git commit -m "feat(admin-ui): add MUI dependencies and brand theme foundation"
```

### Task 2: Wire theme and font into main.tsx

**Files:**
- Modify: `Iverson.AdminUI/src/main.tsx`

**Interfaces:**
- Consumes: `theme` from Task 1's `src/theme/theme.ts`.

- [ ] **Step 1: Add the font import and `ThemeProvider`/`CssBaseline` wrap**

Current content:
```tsx
import React from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider } from "react-router-dom";
import { router } from "./router";
import { AuthProvider } from "./auth/AuthProvider";
import { initTelemetry } from "./telemetry";

initTelemetry();

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <AuthProvider>
      <RouterProvider router={router} future={{ v7_startTransition: true }} />
    </AuthProvider>
  </React.StrictMode>
);
```

New content:
```tsx
import React from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider } from "react-router-dom";
import { ThemeProvider, CssBaseline } from "@mui/material";
import "@fontsource/fraunces/900.css";
import { theme } from "./theme/theme";
import { router } from "./router";
import { AuthProvider } from "./auth/AuthProvider";
import { initTelemetry } from "./telemetry";

initTelemetry();

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <AuthProvider>
        <RouterProvider router={router} future={{ v7_startTransition: true }} />
      </AuthProvider>
    </ThemeProvider>
  </React.StrictMode>
);
```

- [ ] **Step 2: Run the test suite**
```bash
cd Iverson.AdminUI && npm test
```
No test touches `main.tsx` directly; this run confirms nothing else broke.

- [ ] **Step 3: Commit**
```bash
git add Iverson.AdminUI/src/main.tsx
git commit -m "feat(admin-ui): wire MUI theme and brand font into the app root"
```

### Task 3: Restyle the admin UI shell

**Files:**
- Modify: `Iverson.AdminUI/src/layout/AppLayout.tsx`
- Modify: `Iverson.AdminUI/src/layout/Sidebar.tsx`

**Interfaces:**
- Consumes: `tokens.fontHeading` from Task 1's `src/theme/tokens.ts`.

- [ ] **Step 1: Restyle `AppLayout.tsx`**

Current content:
```tsx
import { useAuth } from "react-oidc-context";
import { Outlet } from "react-router-dom";
import { Sidebar } from "./Sidebar";

export function AppLayout() {
  const auth = useAuth();
  const userEmail = auth.user?.profile?.email || "User";

  return (
    <div>
      <header>
        <span>{userEmail}</span>
        <button onClick={() => auth.signoutRedirect()}>Logout</button>
      </header>
      <Sidebar />
      <main>
        <Outlet />
      </main>
    </div>
  );
}
```

New content:
```tsx
import { useAuth } from "react-oidc-context";
import { Outlet } from "react-router-dom";
import { AppBar, Toolbar, Typography, Button, Box } from "@mui/material";
import { Sidebar } from "./Sidebar";
import { tokens } from "../theme/tokens";

export function AppLayout() {
  const auth = useAuth();
  const userEmail = auth.user?.profile?.email || "User";

  return (
    <Box>
      <AppBar position="static">
        <Toolbar>
          <Typography variant="h6" sx={{ fontFamily: tokens.fontHeading, flexGrow: 1 }}>
            Iverson
          </Typography>
          <span>{userEmail}</span>
          <Button color="inherit" onClick={() => auth.signoutRedirect()}>
            Logout
          </Button>
        </Toolbar>
      </AppBar>
      <Box sx={{ display: "flex" }}>
        <Sidebar />
        <Box component="main" sx={{ flexGrow: 1 }}>
          <Outlet />
        </Box>
      </Box>
    </Box>
  );
}
```

- [ ] **Step 2: Restyle `Sidebar.tsx`**

Current content:
```tsx
import { useAuth } from "react-oidc-context";
import { Link } from "react-router-dom";

export function Sidebar() {
  const auth = useAuth();
  const groups = auth.user?.profile?.groups || [];

  return (
    <nav>
      <Link to="/performance">Performance</Link>
      <Link to="/storage">Storage</Link>
      {groups.includes("operators") && <Link to="/tenants">Tenants</Link>}
      {groups.includes("tenant-admins") && <Link to="/tenant-admin">Tenant Admin</Link>}
    </nav>
  );
}
```

New content:
```tsx
import { useAuth } from "react-oidc-context";
import { Link } from "react-router-dom";
import { Drawer, List, ListItemButton, ListItemText } from "@mui/material";

export function Sidebar() {
  const auth = useAuth();
  const groups = auth.user?.profile?.groups || [];

  return (
    <Drawer variant="permanent">
      <List>
        <ListItemButton component={Link} to="/performance">
          <ListItemText primary="Performance" />
        </ListItemButton>
        <ListItemButton component={Link} to="/storage">
          <ListItemText primary="Storage" />
        </ListItemButton>
        {groups.includes("operators") && (
          <ListItemButton component={Link} to="/tenants">
            <ListItemText primary="Tenants" />
          </ListItemButton>
        )}
        {groups.includes("tenant-admins") && (
          <ListItemButton component={Link} to="/tenant-admin">
            <ListItemText primary="Tenant Admin" />
          </ListItemButton>
        )}
      </List>
    </Drawer>
  );
}
```

- [ ] **Step 3: Run tests and build**
```bash
cd Iverson.AdminUI && npm test && npm run build
```
Expect all 3 existing test files (`Sidebar.test.tsx`, `router.test.tsx`, `AuthProvider.test.tsx`) to pass unchanged, and the build to complete without type/import errors.

- [ ] **Step 4: Visual check**
```bash
cd Iverson.AdminUI && npm run dev
```
Open `http://localhost:5173/` and confirm the AppBar/Drawer render in the dark palette (near-black background, `#006BB6` AppBar) with the "Iverson" title in Fraunces, before proceeding to commit.

- [ ] **Step 5: Commit**
```bash
git add Iverson.AdminUI/src/layout/AppLayout.tsx Iverson.AdminUI/src/layout/Sidebar.tsx
git commit -m "feat(admin-ui): restyle app shell with MUI AppBar/Drawer"
```

### Task 4: Authentik brand blueprint

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`

- [ ] **Step 1: Commit the pre-existing `grant_types` fix first**

This file already has an unrelated uncommitted change from earlier this session (a fix for an Authentik login bug). Commit it on its own before touching the file for this task:
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml
git commit -m "fix(deploy): add missing grant_types to iverson-oidc-default provider"
```

- [ ] **Step 2: Insert the Brand block into the compose blueprint**

In `blueprints/compose-only/service-clients.yaml`, insert immediately after line 164 (the `iverson-api` application block's `provider:` line) and before line 165 (`  - model: authentik_providers_oauth2.oauth2provider`):
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

- [ ] **Step 3: Insert the identical Brand block into the kind blueprint template**

In `templates/blueprints-configmap-service-clients.yaml`, insert immediately after line 131 (the `iverson-api` application block's `provider:` line) and before line 132 (`      - model: authentik_providers_oauth2.oauth2provider`), indented to match the surrounding 6-space list items:
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

- [ ] **Step 4: Apply and verify against the running compose stack**
```bash
docker compose -f Iverson.Server/docker-compose.yml restart authentik-worker
sleep 10
```
Then confirm the change landed:
```bash
docker exec iverson-postgres psql -U authentik -d authentik -c "SELECT branding_title FROM authentik_brands_brand WHERE domain = 'authentik-default';"
```
Expect `Iverson`. Then load `http://localhost:9000/if/flow/default-authentication-flow/` and confirm the page's `<style data-id="brand-css">` tag contains the CSS above.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml
git commit -m "feat(deploy): brand Authentik's login pages to match the admin UI"
```

## Known issues inherited from spec

ITC Clearface Black requires a licensed font file or a Monotype/Adobe Fonts subscription with web-embedding rights for this domain — neither exists yet. Fraunces (Black) is the interim substitute; swapping in the real typeface later only requires updating `tokens.ts` and the Authentik blueprint's `custom_css` font-family values once licensing is sorted.
