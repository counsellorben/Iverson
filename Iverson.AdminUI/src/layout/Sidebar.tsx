import { useAuth } from "react-oidc-context";
import { Link } from "react-router";
import { Drawer, List, ListItemButton, ListItemText } from "@mui/material";
import { hasGroup } from "../auth/groups";

export function Sidebar() {
  const auth = useAuth();
  // Membership goes through the same helper the `RequireGroup` route guard uses, so a hidden
  // nav item and a blocked route can never disagree about what "in a group" means. The old
  // `auth.user?.profile?.groups.includes(...)` here was also a type error — an OIDC profile
  // claim is `unknown`, so `.includes` was not actually known to exist on it.
  const profile = auth.user?.profile;

  return (
    <Drawer variant="permanent">
      <List>
        <ListItemButton component={Link} to="/performance">
          <ListItemText primary="Performance" />
        </ListItemButton>
        <ListItemButton component={Link} to="/storage">
          <ListItemText primary="Storage" />
        </ListItemButton>
        {hasGroup(profile, "operators") && (
          <ListItemButton component={Link} to="/tenants">
            <ListItemText primary="Tenants" />
          </ListItemButton>
        )}
        {hasGroup(profile, "tenant-admins") && (
          <ListItemButton component={Link} to="/tenant-admin">
            <ListItemText primary="Tenant Admin" />
          </ListItemButton>
        )}
      </List>
    </Drawer>
  );
}
