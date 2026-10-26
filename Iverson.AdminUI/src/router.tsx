import { createBrowserRouter } from "react-router";
import { AuthGate } from "./auth/AuthProvider";
import { RequireRole, RolesProvider } from "./auth/roles";
import { CallbackPage } from "./auth/CallbackPage";
import { AppLayout } from "./layout/AppLayout";
import { LandingPage } from "./pages/LandingPage";
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
    // RolesProvider reads the caller's console roles once per session token; every guard below and
    // the sidebar render from that one answer.
    element: <AuthGate><RolesProvider><AppLayout /></RolesProvider></AuthGate>,
    children: [
      // CSR round 12 §2.2: the console is for admins only. The landing page admits an Operator or a
      // tenant admin, and shows the cards their roles allow.
      { index: true, element: <RequireRole role="either"><LandingPage /></RequireRole> },
      // Guarded so these pages are UNREACHABLE, not merely unlinked: the sidebar hides the nav item
      // for a caller without the role, but the address bar would still mount the page. The server
      // enforces the same roles on the endpoints behind them.
      {
        path: "tenants",
        element: <RequireRole role="operator"><TenantsPage /></RequireRole>,
      },
      {
        path: "tenant-admin",
        element: <RequireRole role="tenantAdmin"><TenantAdminPage /></RequireRole>,
      },
    ],
  },
];

export const router = createBrowserRouter(routes, {
  basename: import.meta.env.DEV ? "" : "/admin",
});
