import { createBrowserRouter } from "react-router-dom";

export const router = createBrowserRouter(
  [{ path: "/", element: <div>Admin UI</div> }],
  { basename: import.meta.env.DEV ? "" : "/admin" }
);
