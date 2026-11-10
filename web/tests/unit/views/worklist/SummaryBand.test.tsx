import { act, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { humanReadHttpResponse as response } from "../../../support/human-read-response.fixtures";
import { endpointResponse } from "../../../support/transport-response.fixtures";
import { workBasis } from "../../../support/inputs";
import { SummaryBand } from "../../../../src/views/worklist/SummaryBand";
const summary = {
  basis: workBasis,
  counts: {
    total: 2,
    assignment: [],
    due: [],
    lifecycle: [
      { value: "ACTIVE", count: 2 },
      { value: "VOIDED", count: 0 },
      { value: "ERASED", count: 0 },
    ],
    status: [
      { value: "OPENED", count: 2 },
      { value: "CLOSED", count: 0 },
    ],
    progress: [
      { value: "UNDECIDED", count: 1 },
      { value: "DECIDED", count: 1 },
      { value: "PARTLY_PAID", count: 0 },
      { value: "PAID", count: 0 },
    ],
  },
  claimed: [{ currency: "EUR", amount: "1999999999999999999.9998" }],
  payable: [{ currency: "KWD", amount: "1.2" }],
  paid: [],
};
afterEach(() => vi.unstubAllGlobals());
it("renders independent maximum totals without rounding or single-amount bounds", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(response("case.summary", "SUCCEEDED", summary)),
  );
  render(<SummaryBand filter={{ assignment: "MINE", order: "NOTIFIED", text: "needle" }} />);
  await screen.findByText("1,999,999,999,999,999,999.9998 EUR");
  expect(screen.getByText("1.200 KWD")).toBeVisible();
  expect(screen.getByText("2", { exact: true })).toBeVisible();
  expect(fetch).toHaveBeenCalledWith(
    "/api/case.summary",
    expect.objectContaining({
      body: JSON.stringify({ assignment: "MINE", order: "NOTIFIED", text: "needle" }),
    }),
  );
});
it("clears prior totals when a different filter is refused", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.summary", "SUCCEEDED", summary))
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
      finish(response("case.summary", "SUCCEEDED", summary));
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
    vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(response("case.summary", "SUCCEEDED", summary)),
  );
  render(<SummaryBand filter={{ view: "ALL", responsibleActor }} />);
  await screen.findByText("2", { exact: true });
  expect(fetch).toHaveBeenCalledWith(
    "/api/case.summary",
    expect.objectContaining({ body: JSON.stringify({ view: "ALL", responsibleActor }) }),
  );
});
