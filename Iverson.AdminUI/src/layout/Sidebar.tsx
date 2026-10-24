import { Link } from "react-router";
import { Drawer, List, ListItemButton, ListItemText } from "@mui/material";
import { hasRole, useConsoleRoles } from "../auth/roles";

export function Sidebar() {
  // The answer every route guard reads (RequireRole), so a hidden nav item and a blocked route can
  // never disagree about who holds which role.
  const roles = useConsoleRoles();

  return (
    <Drawer variant="permanent">
      <List>
        {hasRole(roles, "operator") && (
          <ListItemButton component={Link} to="/tenants">
            <ListItemText primary="Tenants" />
          </ListItemButton>
        )}
        {hasRole(roles, "tenantAdmin") && (
          <ListItemButton component={Link} to="/tenant-admin">
            <ListItemText primary="Tenant Admin" />
          </ListItemButton>
        )}
      </List>
    </Drawer>
  );
}
