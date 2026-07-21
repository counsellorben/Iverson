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
