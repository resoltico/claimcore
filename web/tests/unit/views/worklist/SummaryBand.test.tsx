import { act, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { response, unavailable } from "../../../support/service-ui.fixtures";
import { SummaryBand } from "../../../../src/views/worklist/SummaryBand";
const summary = {
  counts: {
    total: 2,
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
      { value: "PAID", count: 0 },
    ],
  },
  claimed: [{ currency: "EUR", amount: "1999999999999999999.9998" }],
  payable: [{ currency: "KWD", amount: "1.2" }],
};
afterEach(() => vi.unstubAllGlobals());
it("renders independent maximum totals without rounding or single-amount bounds", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response("case.summary", "SUCCEEDED", summary)));
  render(
    <SummaryBand filter={{ assignment: "ASSIGNED_TO_ME", order: "NOTIFIED", text: "needle" }} />,
  );
  await screen.findByText("1,999,999,999,999,999,999.9998 EUR");
  expect(screen.getByText("1.200 KWD")).toBeVisible();
  expect(screen.getByText("Matching cases: 2")).toBeVisible();
  expect(fetch).toHaveBeenCalledWith(
    "/api/case.summary",
    expect.objectContaining({
      body: JSON.stringify({ assignment: "ASSIGNED_TO_ME", order: "NOTIFIED", text: "needle" }),
    }),
  );
});
it("clears prior totals when a different filter is refused", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response("case.summary", "SUCCEEDED", summary))
    .mockResolvedValueOnce(response("case.summary", "REJECTED", unavailable));
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
