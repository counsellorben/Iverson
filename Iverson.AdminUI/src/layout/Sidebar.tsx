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
