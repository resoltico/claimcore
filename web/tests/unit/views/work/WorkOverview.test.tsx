import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor, fireEvent } from "../../../support/presentation-test-support";
import { context, response, unavailable } from "../../../support/service-ui.fixtures";
import { WorkOverview } from "../../../../src/views/work/WorkOverview";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";

const observation = {
  mine: false,
  basis: {
    observedAt: "2026-11-02T00:00:00.000000Z",
    businessDate: "2026-11-02",
    timeZone: "Etc/UTC",
  },
  actionableCases: 7,
  missingPlanCases: 2,
  overdueCases: 2,
  overdueReminders: 3,
  unassignedCases: 3,
  unavailableAssigneeCases: 1,
  attentionCases: 3,
};
afterEach(() => vi.unstubAllGlobals());
it("shows named case/reminder dimensions and the server's distinct attention union", async () => {
  const fetch = vi.fn().mockResolvedValue(response("work.overview", "SUCCEEDED", observation));
  vi.stubGlobal("fetch", fetch);
  render(<WorkOverview />);
  const label = await screen.findByText("Distinct cases needing attention");
  expect(label.parentElement).toHaveTextContent("3");
  expect(screen.getByText("Overdue reminders").parentElement).toHaveTextContent("3");
  expect(screen.getByText("Cases without a next action").parentElement).toHaveTextContent("2");
  expect(fetch).toHaveBeenCalledWith("/api/work.overview", expect.objectContaining({ body: "{}" }));
});
it("masks old counts on a failed refresh and never turns an unknown result into all-clear", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response("work.overview", "SUCCEEDED", observation))
    .mockResolvedValue(response("work.overview", "REJECTED", unavailable));
  vi.stubGlobal("fetch", fetch);
  render(<WorkOverview />);
  await screen.findByText("Distinct cases needing attention");
  await userEvent.click(screen.getByRole("button", { name: "Refresh work" }));
  await screen.findByText("Work overview is unavailable. No all-clear is established.");
  expect(screen.queryByText("Distinct cases needing attention")).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("refreshes the server observation on focus and keeps Mine explicit in the request", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response("work.overview", "SUCCEEDED", { ...observation, mine: true }))
    .mockResolvedValue(
      response("work.overview", "SUCCEEDED", {
        ...observation,
        mine: true,
        basis: { ...observation.basis, businessDate: "2026-11-03" },
      }),
    );
  vi.stubGlobal("fetch", fetch);
  render(<WorkOverview mine />);
  await screen.findByText("Distinct cases needing attention");
  fireEvent.focus(window);
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  for (const [, options] of fetch.mock.calls) {
    expect(options).toMatchObject({ body: '{"mine":true}' });
  }
});
it("does not disclose aggregate existence when the current requester has no case view", () => {
  const fetch = vi.fn();
  vi.stubGlobal("fetch", fetch);
  render(
    <RequesterContext value={{ ...context, views: ["AUTHORITY"] }}>
      <WorkOverview />
    </RequesterContext>,
  );
  expect(screen.queryByText("Work overview")).toBeNull();
  expect(fetch).not.toHaveBeenCalled();
});
