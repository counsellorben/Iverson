import { createBrowserRouter } from "react-router";
import { AuthGate } from "./auth/AuthProvider";
import { RequireGroup } from "./auth/RequireGroup";
import { CallbackPage } from "./auth/CallbackPage";
import { AppLayout } from "./layout/AppLayout";
import { LandingPage } from "./pages/LandingPage";
import { PerformancePage } from "./pages/PerformancePage";
import { StoragePage } from "./pages/StoragePage";
import { TenantsPage } from "./pages/TenantsPage";
import { TenantAdminPage } from "./pages/TenantAdminPage";

/**
 * The route table, exported separately from `router` so tests can mount it under a memory
 * router at an arbitrary starting path. `createBrowserRouter` fixes its initial location when
 * this module is first imported, which makes "navigate straight to /tenants and see what
 * renders" untestable through `router` itself.
 */
export const routes = [
  { path: "/callback", element: <CallbackPage /> },
  {
    path: "/",
    element: <AuthGate><AppLayout /></AuthGate>,
    children: [
      // Deliberately UNGUARDED. The landing page shows Operator-gated widgets to every
      // authenticated user and degrades card by card; wrapping it in a RequireGroup would
      // replace nine informative cards with a single refusal.
      { index: true, element: <LandingPage /> },
      { path: "performance", element: <PerformancePage /> },
      { path: "storage", element: <StoragePage /> },
      // Guarded so these pages are UNREACHABLE, not merely unlinked: the sidebar hides the
      // nav item for a non-member, but the address bar still mounted the page. The server
      // enforces the same two group rules on the endpoints behind them.
      {
        path: "tenants",
        element: <RequireGroup group="operators"><TenantsPage /></RequireGroup>,
      },
      {
        path: "tenant-admin",
        element: <RequireGroup group="tenant-admins"><TenantAdminPage /></RequireGroup>,
      },
    ],
  },
];

export const router = createBrowserRouter(routes, {
  basename: import.meta.env.DEV ? "" : "/admin",
});
