import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { CaseList } from "../src/views/CaseList";
import { context, response, unavailable } from "./service-ui.fixtures";

const page = (mayOpen: boolean, nextCursor: string | null = "next") =>
  response("case.list", "SUCCEEDED", {
    items: [
      {
        state: "LIVE",
        disposition: "ACTIVE",
        caseReference: "ADVISED-CASE",
        revision: 1,
        status: "OPENED",
      },
    ],
    offers: mayOpen ? context.offers : [],
    nextCursor,
  });
afterEach(() => vi.unstubAllGlobals());
it("uses only the page snapshot's offers and clears advice while a fresh page loads", async () => {
  let finish: (value: Response) => void = () => undefined;
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(page(true))
    .mockImplementationOnce(
      () =>
        new Promise<Response>((resolve) => {
          finish = resolve;
        }),
    );
  vi.stubGlobal("fetch", fetch);
  render(<CaseList token="token" onOpen={vi.fn()} onSelect={vi.fn()} />);
  await screen.findByRole("button", { name: "Open a case" });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(fetch.mock.calls[0]?.[0]).toBe("/api/case.list");
  fireEvent.click(screen.getByRole("button", { name: "Reload cases" }));
  expect(screen.queryByRole("button", { name: "ADVISED-CASE" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Open a case" })).toBeNull();
  await act(() =>
    Promise.resolve().then(() => {
      finish(page(false, null));
    }),
  );
  await screen.findByRole("button", { name: "ADVISED-CASE" });
  expect(screen.queryByRole("button", { name: "Open a case" })).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(2);
});
it.each(["access", "session"])(
  "clears rows continuation and advice on a %s refusal",
  async (kind) => {
    const failure =
      kind === "access"
        ? response("case.list", "REJECTED", unavailable)
        : new Response(
            JSON.stringify({
              kind: "HOST_FAILURE",
              code: "SESSION_REJECTED",
              status: 401,
              diagnostic: { id: "HOST_SESSIONREJECTED", parameters: {} },
              message: "Synthetic",
              executionPhase: "NOT_STARTED",
            }),
            { status: 401, headers: { "content-type": "application/json" } },
          );
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValueOnce(page(true)).mockResolvedValueOnce(failure),
    );
    render(<CaseList token="token" onOpen={vi.fn()} onSelect={vi.fn()} />);
    fireEvent.click(await screen.findByRole("button", { name: "Load more cases" }));
    await screen.findByRole("alert");
    expect(screen.queryByRole("button", { name: "ADVISED-CASE" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Open a case" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Load more cases" })).toBeNull();
  },
);
it("retains the last accepted page advice after a request-local failure", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValueOnce(page(true))
      .mockRejectedValueOnce(new Error("Synthetic transport failure")),
  );
  render(<CaseList token="token" onOpen={vi.fn()} onSelect={vi.fn()} />);
  fireEvent.click(await screen.findByRole("button", { name: "Load more cases" }));
  await waitFor(() => {
    expect(screen.getByRole("alert")).toBeVisible();
  });
  expect(screen.getByRole("button", { name: "ADVISED-CASE" })).toBeVisible();
  expect(screen.getByRole("button", { name: "Open a case" })).toBeVisible();
});
