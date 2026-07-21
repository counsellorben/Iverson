import { render, screen } from "@testing-library/react";
import { RouterProvider } from "react-router-dom";
import { router } from "./router";
import { describe, it, expect } from "vitest";

describe("router", () => {
  it("renders the root route with Admin UI text", () => {
    render(<RouterProvider router={router} />);
    expect(screen.getByText("Admin UI")).toBeInTheDocument();
  });
});
