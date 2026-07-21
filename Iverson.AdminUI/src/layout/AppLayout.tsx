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
