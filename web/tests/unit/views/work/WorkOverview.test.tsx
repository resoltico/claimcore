import { afterEach, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import { useEndpoint } from "../../../../src/hooks/useEndpoint";
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
  // Missing-plan and overdue cases are disjoint; assignment flags overlap within those four.
  attentionCases: 4,
};
afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});
it("shows named case/reminder dimensions and the server's distinct attention union", async () => {
  const fetch = vi.fn().mockResolvedValue(response("work.overview", "SUCCEEDED", observation));
  vi.stubGlobal("fetch", fetch);
  render(<WorkOverview />);
  const label = await screen.findByText("Distinct cases needing attention");
  expect(label.parentElement).toHaveTextContent("4");
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
    expect(fetch).toHaveBeenCalledTimes(3);
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

it("keeps accessible attention and basis visible in compact mode while detailed dimensions expand", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(response("work.overview", "SUCCEEDED", observation)),
  );
  const view = render(<WorkOverview compact />);
  await screen.findByRole("heading", { name: "Accessible work" });
  await screen.findAllByText("Distinct cases needing attention");
  expect(view.container.querySelector(".compact-work-overview")).not.toBeNull();
  expect(view.container.querySelector(".work-attention-count")).toHaveTextContent("4");
  expect(view.container.querySelectorAll(".observation-basis")[0]).toHaveTextContent("02/11/2026");
  expect(view.container.querySelectorAll(".observation-basis")[0]).toHaveTextContent("Etc/UTC");
  expect(screen.getByRole("button", { name: "Refresh work" })).toBeVisible();
  expect(screen.getByText("Active open cases")).not.toBeVisible();
  await userEvent.click(screen.getByText("Show summary totals", { selector: "summary" }));
  expect(screen.getByText("Active open cases")).toBeVisible();
});

it("refreshes a long-lived screen from the server day and invalidates date-dependent reads", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
  // The client clock deliberately disagrees with both observations; it only schedules a read.
  vi.setSystemTime(new Date("2031-06-15T12:00:00Z"));
  const before = {
    ...observation,
    basis: {
      observedAt: "2026-11-02T21:59:45.000000Z",
      businessDate: "2026-11-02",
      timeZone: "Europe/Riga",
    },
  };
  const after = {
    ...observation,
    basis: {
      observedAt: "2026-11-02T22:00:45.000000Z",
      businessDate: "2026-11-03",
      timeZone: "Europe/Riga",
    },
  };
  let advanced = false;
  let overviewReads = 0;
  let summaryReads = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string) => {
      const current = advanced ? after : before;
      if (url === "/api/work.overview") {
        overviewReads += 1;
        return Promise.resolve(response("work.overview", "SUCCEEDED", current));
      }
      if (url === "/api/case.summary") {
        summaryReads += 1;
        return Promise.resolve(
          response("case.summary", "SUCCEEDED", {
            basis: current.basis,
            counts: {
              total: 1,
              lifecycle: [],
              status: [],
              progress: [],
              assignment: [],
              due: [{ value: advanced ? "OVERDUE" : "TODAY", count: 1 }],
            },
            claimed: [],
            payable: [],
          }),
        );
      }
      throw new Error(`Unexpected request ${url}`);
    }),
  );
  const DependentSummary = () => {
    const result = useEndpoint("case.summary", {});
    const value = result.value?.outcome;
    return (
      <output data-testid="dependent-summary">
        {value?.kind === "SUCCEEDED"
          ? `${value.data.basis.businessDate}: ${value.data.counts.due[0]?.value}`
          : "Loading"}
      </output>
    );
  };
  const view = render(
    <>
      <WorkOverview />
      <DependentSummary />
    </>,
  );
  await waitFor(() => {
    expect(screen.getByTestId("dependent-summary")).toHaveTextContent("2026-11-02: TODAY");
  });
  expect(view.container.querySelectorAll(".observation-basis")[0]).toHaveTextContent("02/11/2026");
  expect(overviewReads).toBe(1);
  expect(summaryReads).toBe(1);
  advanced = true;
  await act(async () => {
    await vi.advanceTimersByTimeAsync(59_999);
  });
  expect(overviewReads).toBe(1);
  expect(summaryReads).toBe(1);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1);
  });
  await waitFor(() => {
    expect(screen.getByTestId("dependent-summary")).toHaveTextContent("2026-11-03: OVERDUE");
  });
  expect(view.container.querySelectorAll(".observation-basis")[0]).toHaveTextContent("03/11/2026");
  expect(view.container.querySelectorAll(".observation-basis")[0]).toHaveTextContent("Europe/Riga");
  expect(overviewReads).toBe(3); // Interval observation, then the shared day-change invalidation.
  expect(summaryReads).toBe(2);
  view.unmount();
  await vi.advanceTimersByTimeAsync(60_000);
  expect(overviewReads).toBe(3);
  expect(summaryReads).toBe(2);
});
