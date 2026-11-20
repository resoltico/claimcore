import userEvent from "@testing-library/user-event";
import { act, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { humanReadHttpReply as response } from "../../../support/human-read-reply.fixtures";
import { endpointResponse } from "../../../support/transport-response.fixtures";
import type { HumanDocument } from "@contracts/client";
import { workBasis } from "../../../support/inputs";
import { SummaryBand } from "../../../../src/views/worklist/SummaryBand";
const summary: HumanDocument = {
  screen: {
    regions: [
      {
        role: "TOTALS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "total",
              labelId: "label.total",
              prominence: "PRIMARY",
              value: { kind: "COUNT", value: 2 },
            },
            {
              key: "claimed",
              labelId: "field.claimedAmount.label",
              prominence: "PRIMARY",
              value: {
                kind: "MONEY_TOTAL",
                currency: "EUR",
                amount: "1999999999999999999.9998",
                displayScale: 4,
              },
            },
            {
              key: "payable",
              labelId: "field.payableAmount.label",
              prominence: "PRIMARY",
              value: { kind: "MONEY_TOTAL", currency: "KWD", amount: "1.2", displayScale: 3 },
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "LIST" },
  actions: [],
  links: [],
  history: null,
  nextCursor: null,
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
it("renders independent maximum totals without rounding or single-amount bounds", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockResolvedValue(response("case.summary", summary)),
  );
  render(<SummaryBand filter={{ assignment: "MINE", order: "RECEIVED", text: "needle" }} />);
  await screen.findByText("1,999,999,999,999,999,999.9998 EUR");
  expect(screen.getByText("1.200 KWD")).toBeVisible();
  expect(screen.getByText("2", { exact: true })).toBeVisible();
  expect(fetch).toHaveBeenCalledWith(
    "/api/case.summary",
    expect.objectContaining({
      body: JSON.stringify({ assignment: "MINE", order: "RECEIVED", text: "needle", limit: 50 }),
    }),
  );
});
it("clears prior totals when a different filter is refused", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.summary", summary))
    .mockResolvedValueOnce(
      endpointResponse("case.summary", {
        representation: "HUMAN",
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "REFUSED",
            advice: {
              message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
              action: "ASK_FOR_ACCESS",
            },
          },
        },
      }),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(<SummaryBand filter={{ text: "old" }} />);
  await screen.findByText("1,999,999,999,999,999,999.9998 EUR");
  view.rerender(<SummaryBand filter={{ text: "new" }} />);
  await screen.findByRole("alert");
  expect(screen.queryByText("1,999,999,999,999,999,999.9998 EUR")).toBeNull();
});
it("keeps its own loading state until its request completes", async () => {
  let finish: (value: Response) => void = () => undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(
      () =>
        new Promise<Response>((resolve) => {
          finish = resolve;
        }),
    ),
  );
  render(<SummaryBand filter={{}} />);
  expect(screen.getByRole("status")).toBeVisible();
  await act(() =>
    Promise.resolve().then(() => {
      finish(response("case.summary", summary));
    }),
  );
  await waitFor(() => {
    expect(screen.queryByRole("status")).toBeNull();
  });
});

it("forwards the core responsibility descriptor filter unchanged to the actual summary request", async () => {
  const responsibleActor = "10000000-0000-4000-8000-000000000001";
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockResolvedValue(response("case.summary", summary)),
  );
  render(<SummaryBand filter={{ view: "ALL", responsibleActor }} />);
  await screen.findByText("2", { exact: true });
  expect(fetch).toHaveBeenCalledWith(
    "/api/case.summary",
    expect.objectContaining({ body: JSON.stringify({ view: "ALL", responsibleActor, limit: 50 }) }),
  );
});

it("offers an explicit retry for read pressure without changing its query", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }),
        { status: 429, headers: { "Content-Type": "application/json" } },
      ),
    )
    .mockResolvedValueOnce(response("case.summary", summary));
  vi.stubGlobal("fetch", fetch);
  render(<SummaryBand filter={{ view: "ALL", text: "same query" }} />);
  await screen.findByRole("alert");
  expect(fetch).toHaveBeenCalledTimes(1);
  await userEvent.click(screen.getByRole("button", { name: "Try again" }));
  await screen.findByText("1,999,999,999,999,999,999.9998 EUR");
  expect(fetch).toHaveBeenCalledTimes(2);
  for (const [path, request] of fetch.mock.calls) {
    expect(path).toBe("/api/case.summary");
    expect(request?.body).toBe(JSON.stringify({ view: "ALL", text: "same query", limit: 50 }));
  }
});

it("submits the selected grouping with the existing dates and offers its CSV", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.summary", summary))
    .mockResolvedValueOnce(response("case.summary", summary));
  vi.stubGlobal("fetch", fetch);
  render(
    <SummaryBand filter={{ registeredFrom: "2026-11-01", registeredThrough: "2026-11-30" }} />,
  );
  await screen.findByText("2", { exact: true });
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: "Group by" }));
  await user.click(screen.getByRole("option", { name: "Received month" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenLastCalledWith(
      "/api/case.summary",
      expect.objectContaining({
        body: JSON.stringify({
          registeredFrom: "2026-11-01",
          registeredThrough: "2026-11-30",
          groupBy: "RECEIVED_MONTH",
          limit: 50,
        }),
      }),
    );
  });
  expect(await screen.findByRole("button", { name: "Export report CSV" })).toBeVisible();
});

it("starts a new selection at page one and requires a successful result for its export", async () => {
  let completeOld: (value: Response) => void = () => undefined;
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.summary", { ...summary, nextCursor: "old-next" }))
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          completeOld = resolve;
        }),
    )
    .mockResolvedValueOnce(response("case.summary", summary))
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }),
        { status: 429, headers: { "Content-Type": "application/json" } },
      ),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(<SummaryBand filter={{ text: "old" }} />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Next report page" }));
  expect(fetch).toHaveBeenLastCalledWith(
    "/api/case.summary",
    expect.objectContaining({
      body: JSON.stringify({ text: "old", limit: 50, cursor: "old-next" }),
    }),
  );
  view.rerender(<SummaryBand filter={{ text: "new" }} />);
  expect(fetch).toHaveBeenLastCalledWith(
    "/api/case.summary",
    expect.objectContaining({ body: JSON.stringify({ text: "new", limit: 50 }) }),
  );
  act(() => {
    completeOld(response("case.summary", { ...summary, nextCursor: "ignored" }));
  });
  await user.click(screen.getByRole("combobox", { name: "Group by" }));
  await user.click(screen.getByRole("option", { name: "Received month" }));
  await screen.findByRole("alert");
  expect(screen.getByRole("button", { name: "Export report CSV" })).toBeDisabled();
});

it("allows cursorless export after a continuation refusal and blocks a real access refusal", async () => {
  const refusal = (messageId: string, action: "READ_CURRENT" | "ASK_FOR_ACCESS") =>
    endpointResponse("case.summary", {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: { kind: "REFUSED", advice: { message: { messageId, arguments: [] }, action } },
      },
    });
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.summary", summary))
    .mockResolvedValueOnce(response("case.summary", { ...summary, nextCursor: "page-2" }))
    .mockResolvedValueOnce(refusal("diagnostic.INVALID_CURSOR", "READ_CURRENT"))
    .mockResolvedValueOnce(refusal("diagnostic.RESOURCE_UNAVAILABLE", "ASK_FOR_ACCESS"));
  vi.stubGlobal("fetch", fetch);
  render(<SummaryBand filter={{ view: "ALL" }} />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: "Group by" }));
  await user.click(screen.getByRole("option", { name: "Received month" }));
  await user.click(await screen.findByRole("button", { name: "Next report page" }));
  await screen.findByText(/this page link is no longer valid/iu);
  expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
  const download = screen.getByRole("button", { name: "Export report CSV" });
  expect(download).toBeEnabled();
  await user.click(screen.getByRole("button", { name: "Restart report" }));
  await waitFor(() => {
    expect(download).toBeDisabled();
  });
});

it("blocks export across a held disclosure retry and reopens it only after success", async () => {
  const denied = () =>
    endpointResponse("case.summary", {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "REFUSED",
          advice: {
            message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
            action: "ASK_FOR_ACCESS",
          },
        },
      },
    });
  let finishRetry: (reply: Response) => void = () => undefined;
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.summary", summary))
    .mockResolvedValueOnce(denied())
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finishRetry = resolve;
        }),
    )
    .mockResolvedValueOnce(response("case.summary", summary));
  vi.stubGlobal("fetch", fetch);
  render(<SummaryBand filter={{ view: "ALL" }} />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: "Group by" }));
  await user.click(screen.getByRole("option", { name: "Received month" }));
  await screen.findByRole("alert");
  const download = screen.getByRole("button", { name: "Export report CSV" });
  expect(download).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Try again" }));
  await screen.findByRole("status");
  expect(download).toBeDisabled();
  act(() => {
    finishRetry(
      new Response(
        JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }),
        { status: 429, headers: { "Content-Type": "application/json" } },
      ),
    );
  });
  await screen.findByRole("alert");
  expect(download).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Try again" }));
  await screen.findByText("2", { exact: true });
  expect(download).toBeEnabled();
});

it("blocks export on a host session refusal", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.summary", summary))
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({ hostFailure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" } }),
        { status: 401, headers: { "Content-Type": "application/json" } },
      ),
    );
  vi.stubGlobal("fetch", fetch);
  render(<SummaryBand filter={{ view: "ALL" }} />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: "Group by" }));
  await user.click(screen.getByRole("option", { name: "Received month" }));
  await screen.findByRole("alert");
  expect(screen.getByRole("button", { name: "Export report CSV" })).toBeDisabled();
});
