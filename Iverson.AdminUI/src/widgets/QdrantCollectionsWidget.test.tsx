import { StrictMode } from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { QdrantResponse } from "../api/types";
import { QdrantCollectionsWidget } from "./QdrantCollectionsWidget";
import { NO_SAMPLE_VALUE } from "./format";

const fetchQdrantMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<QdrantResponse>>>();

vi.mock("../api/console", () => ({
  fetchQdrant: (token: string, signal: AbortSignal) => fetchQdrantMock(token, signal),
}));

const TOKEN = "test-access-token";

function qdrant(over: Partial<QdrantResponse> = {}): ApiResult<QdrantResponse> {
  return {
    kind: "ok",
    status: 200,
    data: {
      collectionCount: 2,
      collections: [
        { name: "tenant_a_objects", pointsCount: 1234567, indexedVectorsCount: 0 },
        { name: "tenant_b_objects", pointsCount: 42, indexedVectorsCount: 42 },
      ],
      unreadableCollectionCount: 0,
      ...over,
    },
  };
}

function card(): HTMLElement {
  return screen.getByTestId("widget-qdrant");
}

function cardState(): string | null {
  return card().getAttribute("data-state");
}

beforeEach(() => {
  fetchQdrantMock.mockReset();
});

describe("QdrantCollectionsWidget", () => {
  it("renders points and indexed vectors per collection", async () => {
    fetchQdrantMock.mockResolvedValue(qdrant());

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("qdrant-collection-count")).toHaveAttribute("data-count", "2");
    expect(screen.getByTestId("qdrant-row-tenant_a_objects")).toHaveTextContent("1,234,567");
    // Indexed vectors legitimately trail points — a collection under Qdrant's indexing
    // threshold serves every query by brute force and reports zero indexed. A measured zero.
    expect(screen.getByTestId("qdrant-row-tenant_a_objects")).toHaveAttribute(
      "data-indexed-null",
      "false"
    );
    expect(screen.getByTestId("qdrant-row-tenant_b_objects")).toHaveTextContent("42");
  });

  it("NEVER renders a count Qdrant did not report as zero points", async () => {
    // "This collection is empty" is a reason to delete a collection. "Qdrant did not report a
    // figure" is not. The visible cell has to keep them apart, not just an attribute.
    fetchQdrantMock.mockResolvedValue(
      qdrant({
        collectionCount: 1,
        collections: [{ name: "tenant_c_objects", pointsCount: null, indexedVectorsCount: null }],
      })
    );

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const row = screen.getByTestId("qdrant-row-tenant_c_objects");
    expect(row).toHaveAttribute("data-points-null", "true");
    expect(row).toHaveAttribute("data-indexed-null", "true");
    const cells = row.querySelectorAll("td");
    expect(cells[1]).toHaveTextContent(NO_SAMPLE_VALUE);
    expect(cells[2]).toHaveTextContent(NO_SAMPLE_VALUE);
    expect(cells[1].textContent ?? "").not.toMatch(/\d/);
    expect(cells[2].textContent ?? "").not.toMatch(/\d/);
  });

  it("renders an unreported count and a measured zero differently in the same table", async () => {
    fetchQdrantMock.mockResolvedValue(
      qdrant({
        collectionCount: 2,
        collections: [
          { name: "unreported", pointsCount: null, indexedVectorsCount: null },
          { name: "genuinely_empty", pointsCount: 0, indexedVectorsCount: 0 },
        ],
      })
    );

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const unreported = screen.getByTestId("qdrant-row-unreported").querySelectorAll("td")[1];
    const empty = screen.getByTestId("qdrant-row-genuinely_empty").querySelectorAll("td")[1];
    expect(unreported.textContent).not.toBe(empty.textContent);
    expect(empty).toHaveTextContent("0");
  });

  it("says how many collections Qdrant listed but could not be read", async () => {
    // The endpoint drops a collection whose GetCollectionInfoAsync failed, so this list can be
    // SHORTER than the deployment. Rendering "2 collections" as a complete answer while Qdrant
    // holds four is the same lie as rendering a denied type as "0 rows".
    fetchQdrantMock.mockResolvedValue(qdrant({ unreadableCollectionCount: 2 }));

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const note = screen.getByTestId("qdrant-unreadable-collections");
    expect(note).toHaveAttribute("data-hidden-count", "2");
    expect(note).toHaveTextContent(
      "2 collections not shown (listed by Qdrant, but their stats could not be read)."
    );
    // The noun is "collections", not the component's default "types".
    expect(note).not.toHaveTextContent("types");
  });

  it("reads correctly at one unreadable collection", async () => {
    fetchQdrantMock.mockResolvedValue(qdrant({ unreadableCollectionCount: 1 }));

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("qdrant-unreadable-collections")).toHaveTextContent(
      "1 collection not shown"
    );
  });

  it("renders no 'not shown' line at all when every listed collection was read", async () => {
    fetchQdrantMock.mockResolvedValue(qdrant());

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.queryByTestId("qdrant-unreadable-collections")).not.toBeInTheDocument();
  });

  it("renders nothing rather than 'NaN collections not shown' when the count is missing", async () => {
    fetchQdrantMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { collectionCount: 0, collections: [] } as unknown as QdrantResponse,
    });

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.queryByTestId("qdrant-unreadable-collections")).not.toBeInTheDocument();
    expect(card()).not.toHaveTextContent("NaN");
  });

  it("renders the 403 every human gets today as an authorization answer, not a fault", async () => {
    // Operator-gated, and nobody satisfies the Operator policy today (Design 4d). A red
    // "request failed" card here would send an operator debugging a working Qdrant.
    fetchQdrantMock.mockResolvedValue({ kind: "forbidden", status: 403, error: null });

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("forbidden"));
    const notice = screen.getByTestId("widget-qdrant-notice");
    expect(notice).toHaveTextContent("requires the Operator role");
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
  });

  it("renders a transport failure as a real error", async () => {
    fetchQdrantMock.mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("error"));
    const notice = screen.getByTestId("widget-qdrant-notice");
    expect(notice).toHaveClass("MuiAlert-colorError");
    expect(notice).toHaveTextContent("network down");
  });

  it("says 'no collections' without a table rather than rendering an empty one", async () => {
    fetchQdrantMock.mockResolvedValue(qdrant({ collectionCount: 0, collections: [] }));

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("qdrant-collection-count")).toHaveTextContent("0 collections");
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });

  it("keeps the last good table on screen and marks it stale when a poll fails", async () => {
    fetchQdrantMock
      .mockResolvedValueOnce(qdrant())
      .mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<QdrantCollectionsWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    fireEvent.click(screen.getByRole("button", { name: "Refresh Qdrant collections" }));

    await waitFor(() =>
      expect(screen.getByTestId("widget-qdrant-as-of")).toHaveAttribute("data-stale", "true")
    );
    expect(screen.getByTestId("qdrant-row-tenant_a_objects")).toHaveTextContent("1,234,567");
  });

  it("waits for the session rather than spinning when there is no token", () => {
    render(<QdrantCollectionsWidget accessToken={undefined} />);

    expect(cardState()).toBe("awaitingToken");
    expect(screen.getByRole("button", { name: "Refresh Qdrant collections" })).toBeDisabled();
    expect(fetchQdrantMock).not.toHaveBeenCalled();
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchQdrantMock.mockResolvedValue(qdrant());

    render(<QdrantCollectionsWidget accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("qdrant-row-tenant_a_objects")).toHaveTextContent("1,234,567");
    expect(card()).not.toHaveTextContent("Loading…");
  });
});
