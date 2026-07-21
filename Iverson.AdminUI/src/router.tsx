import { createBrowserRouter, Navigate } from "react-router-dom";
import { AuthGate } from "./auth/AuthProvider";
import { CallbackPage } from "./auth/CallbackPage";
import { AppLayout } from "./layout/AppLayout";
import { PerformancePage } from "./pages/PerformancePage";
import { StoragePage } from "./pages/StoragePage";
import { TenantsPage } from "./pages/TenantsPage";
import { TenantAdminPage } from "./pages/TenantAdminPage";

export const router = createBrowserRouter(
  [
    { path: "/callback", element: <CallbackPage /> },
    {
      path: "/",
      element: <AuthGate><AppLayout /></AuthGate>,
      children: [
        { index: true, element: <Navigate to="/performance" replace /> },
        { path: "performance", element: <PerformancePage /> },
        { path: "storage", element: <StoragePage /> },
        { path: "tenants", element: <TenantsPage /> },
        { path: "tenant-admin", element: <TenantAdminPage /> },
      ],
    },
  ],
  { basename: import.meta.env.DEV ? "" : "/admin" }
);
