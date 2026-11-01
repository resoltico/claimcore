import { afterEach, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { caseSummaryInput, workBasis } from "../support/inputs";
import { current } from "../support/presentation-state.fixtures";
import { Dashboard } from "../../src/views/Dashboard";
import { navigate } from "../../src/routing/navigation";
const descriptors = {
  scrollY: Object.getOwnPropertyDescriptor(window, "scrollY"),
  scrollTo: Object.getOwnPropertyDescriptor(window, "scrollTo"),
};
afterEach(() => {
  for (const [key, descriptor] of Object.entries(descriptors)) {
    if (descriptor !== undefined) {
      Object.defineProperty(window, key, descriptor);
    }
  }
});
it.each([
  { retained: true, interrupt: false },
  { retained: false, interrupt: false },
  { retained: true, interrupt: true },
])(
  "returns to the exact work query with row availability=$retained and user override=$interrupt and leaves history state empty",
  async ({ retained, interrupt }) => {
    stubDashboardContext();
    const value = caseSummaryInput({
      caseId: current.caseId,
      caseReference: current.caseReference,
      state: { kind: "ACTIVE", status: "OPENED" },
    });
    const page = (items: (typeof value)[]) =>
      ({
        kind: "outcome",
        status: 200,
        value: {
          outcome: {
            kind: "SUCCEEDED",
            data: { basis: workBasis, offers: [], items, nextCursor: null },
          },
        },
      }) as const;
    const pending = Promise.withResolvers<ReturnType<typeof page>>();
    const listing = mockEndpoint("case.list").mockResolvedValueOnce(page([value]));
    if (interrupt) {
      listing.mockReturnValue(pending.promise);
    } else {
      listing.mockResolvedValue(page(retained ? [value] : []));
    }
    mockEndpoint("case.summary").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            basis: workBasis,
            counts: { total: 1, lifecycle: [], status: [], progress: [], assignment: [], due: [] },
            claimed: [],
            payable: [],
          },
        },
      },
    });
    mockEndpoint("case.get").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: { outcome: { kind: "SUCCEEDED", data: current } },
    });
    mockEndpoint("case.history").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            caseId: current.caseId,
            caseReference: current.caseReference,
            entries: [],
            nextCursor: null,
          },
        },
      },
    });
    const scroll = vi.fn();
    Object.defineProperty(window, "scrollY", { configurable: true, value: 340 });
    Object.defineProperty(window, "scrollTo", { configurable: true, value: scroll });
    navigate("/cases?q=CASE&order=RECENT", true);
    const user = userEvent.setup();
    render(<Dashboard />);
    await user.click(await screen.findByRole("link", { name: current.caseReference }));
    await screen.findByRole("heading", { name: "Case detail" });
    await user.click(screen.getByRole("button", { name: "Back to cases" }));
    await waitFor(() => {
      expect(location.pathname + location.search).toBe("/cases?q=CASE&order=RECENT");
    });
    if (interrupt) {
      const summary = document.querySelector<HTMLElement>(".exact-lookup > summary")!;
      await user.pointer({ target: summary, keys: "[MouseLeft>]" });
      await act(async () => {
        pending.resolve(page([value]));
        await pending.promise;
      });
      await user.pointer({ target: summary, keys: "[/MouseLeft]" });
      await waitFor(() => {
        expect(document.querySelector(".exact-lookup")).toHaveAttribute("open", "");
      });
      expect(scroll).not.toHaveBeenCalled();
    } else if (retained) {
      await waitFor(() => {
        expect(screen.getByRole("link", { name: current.caseReference })).toHaveFocus();
      });
      expect(scroll).toHaveBeenLastCalledWith(0, 340);
    } else {
      await waitFor(() => {
        expect(screen.getByRole("heading", { name: "Cases", level: 1 })).toHaveFocus();
      });
      expect(scroll).not.toHaveBeenCalled();
    }
    expect(history.state).toBeNull();
  },
);
