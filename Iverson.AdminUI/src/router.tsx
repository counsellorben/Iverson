import { createBrowserRouter } from "react-router-dom";
import { AuthGate } from "./auth/AuthProvider";
import { CallbackPage } from "./auth/CallbackPage";

// TODO(Task 4): replace with the real AppLayout/Sidebar.
function AppLayoutPlaceholder() {
  return <div>Admin UI</div>;
}

export const router = createBrowserRouter(
  [
    { path: "/callback", element: <CallbackPage /> },
    { path: "/", element: <AuthGate><AppLayoutPlaceholder /></AuthGate> },
  ],
  { basename: import.meta.env.DEV ? "" : "/admin" }
);
