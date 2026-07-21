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
