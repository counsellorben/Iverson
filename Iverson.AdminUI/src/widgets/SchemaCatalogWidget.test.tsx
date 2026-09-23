import { StrictMode } from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { SchemaCatalogResponse } from "../api/types";
import { SchemaCatalogWidget } from "./SchemaCatalogWidget";

const fetchSchemaMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<SchemaCatalogResponse>>>();

vi.mock("../api/console", () => ({
  fetchSchema: (token: string, signal: AbortSignal) => fetchSchemaMock(token, signal),
}));

const TOKEN = "test-access-token";

function catalog(over: Partial<SchemaCatalogResponse> = {}): ApiResult<SchemaCatalogResponse> {
  return {
    kind: "ok",
    status: 200,
    data: {
      typeCount: 1,
      types: [
        {
          name: "Contact",
          description: "A person",
          fieldCount: 7,
          relations: [
            {
              propertyName: "Account",
              kind: "ManyToOne",
              relatedType: "Account",
              foreignKey: "AccountId",
            },
          ],
        },
      ],
      withheldTypeCount: 0,
      ...over,
    },
  };
}

function cardState(): string | null {
  return screen.getByTestId("widget-schema").getAttribute("data-state");
}

beforeEach(() => {
  fetchSchemaMock.mockReset();
});

describe("SchemaCatalogWidget", () => {
  it("renders the visible types", async () => {
    fetchSchemaMock.mockResolvedValue(catalog());

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("schema-type-count")).toHaveAttribute("data-count", "1");
    expect(screen.getByTestId("schema-type-Contact")).toHaveTextContent("7 fields, 1 relation");
  });

  it("says how many types were withheld", async () => {
    fetchSchemaMock.mockResolvedValue(catalog({ withheldTypeCount: 4 }));

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const note = screen.getByTestId("schema-hidden-types");
    expect(note).toHaveAttribute("data-hidden-count", "4");
    expect(note).toHaveTextContent("4 types not shown (withheld by your permissions).");
  });

  it("does not read an empty catalog as 'no types are registered' when types were withheld", async () => {
    fetchSchemaMock.mockResolvedValue(catalog({ typeCount: 0, types: [], withheldTypeCount: 9 }));

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("schema-hidden-types")).toHaveAttribute("data-hidden-count", "9");
    expect(screen.getByTestId("schema-hidden-types")).toHaveTextContent("9 types not shown");
  });

  it("renders no withheld line when nothing was withheld", async () => {
    fetchSchemaMock.mockResolvedValue(catalog({ withheldTypeCount: 0 }));

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.queryByTestId("schema-hidden-types")).not.toBeInTheDocument();
  });

  it("renders nothing rather than 'NaN types not shown' when the count is missing", async () => {
    // The endpoint always sends withheldTypeCount, but `count <= 0` is FALSE for undefined and
    // for NaN, so an absent field would otherwise reach the formatter and be rendered as the
    // literal string "NaN types not shown".
    fetchSchemaMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { typeCount: 0, types: [] } as unknown as SchemaCatalogResponse,
    });

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.queryByTestId("schema-hidden-types")).not.toBeInTheDocument();
    expect(screen.getByTestId("widget-schema")).not.toHaveTextContent("NaN");
  });

  it("renders a transport failure as an error with nothing to show", async () => {
    fetchSchemaMock.mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("error"));
    expect(screen.getByTestId("widget-schema-notice")).toHaveTextContent("network down");
    expect(screen.queryByTestId("schema-type-count")).not.toBeInTheDocument();
  });

  it("keeps the last good catalog on screen and marks it stale when a refresh fails", async () => {
    fetchSchemaMock
      .mockResolvedValueOnce(catalog({ withheldTypeCount: 4 }))
      .mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    fireEvent.click(screen.getByRole("button", { name: "Refresh Schema" }));

    await waitFor(() =>
      expect(screen.getByTestId("widget-schema-as-of")).toHaveAttribute("data-stale", "true")
    );
    expect(screen.getByTestId("schema-type-Contact")).toBeInTheDocument();
    expect(screen.getByTestId("schema-hidden-types")).toHaveAttribute("data-hidden-count", "4");
  });

  it("waits for the session rather than spinning when there is no token", () => {
    render(<SchemaCatalogWidget accessToken={undefined} />);

    expect(cardState()).toBe("awaitingToken");
    expect(screen.getByRole("button", { name: "Refresh Schema" })).toBeDisabled();
    expect(fetchSchemaMock).not.toHaveBeenCalled();
  });

  it("fetches once on mount and does not poll", async () => {
    fetchSchemaMock.mockResolvedValue(catalog());

    render(<SchemaCatalogWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(fetchSchemaMock).toHaveBeenCalledTimes(1);
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchSchemaMock.mockResolvedValue(catalog({ withheldTypeCount: 3 }));

    render(<SchemaCatalogWidget accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("schema-hidden-types")).toHaveAttribute("data-hidden-count", "3");
  });
});
