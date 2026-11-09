import { useEndpointPage } from "../../../src/hooks/useEndpointPage";
import type { CaseSummary, ResponseByEndpoint } from "@contracts/client";
import { act } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { useEndpoint } from "../../../src/hooks/useEndpoint";
import { useBusinessDate } from "../../../src/hooks/useBusinessDate";
import { render, screen, waitFor, fireEvent } from "../../support/presentation-test-support";
import { context, response } from "../../support/service-ui.fixtures";
const requester = { ...context, businessDate: "2026-11-04" };
const Probe = () => {
  const day = useBusinessDate(requester);
  const summary = useEndpoint("case.summary", {});
  const value = summary.value?.outcome;
  return (
    <>
      <output data-testid="summary">
        {value?.kind === "SUCCEEDED"
          ? `${value.data.basis.businessDate}: ${value.data.counts.due[0]?.value}`
          : "Loading"}
      </output>
      <output data-testid="change">{day.changedDate ?? "unchanged"}</output>
    </>
  );
};
it("refreshes date-dependent reads on focus and server-day changes without polling or work.overview", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
  vi.setSystemTime(new Date("2031-06-15T12:00:00Z"));
  let advanced = false;
  const fetch = vi.fn((url: string) => {
    expect(url).toBe("/api/case.summary");
    return Promise.resolve(
      response("case.summary", "SUCCEEDED", {
        basis: {
          observedAt: "2026-11-04T22:00:00.000000Z",
          businessDate: advanced ? "2026-11-05" : "2026-11-04",
          timeZone: "Europe/Riga",
        },
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
        paid: [],
      }),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const view = render(<Probe />);
  try {
    await waitFor(() => {
      expect(screen.getByTestId("summary")).toHaveTextContent("2026-11-04: TODAY");
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    advanced = true;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    fireEvent.focus(window);
    await waitFor(() => {
      expect(screen.getByTestId("summary")).toHaveTextContent("2026-11-05: OVERDUE");
    });
    expect(screen.getByTestId("change")).toHaveTextContent("2026-11-05");
    expect(fetch).toHaveBeenCalledTimes(3);
    view.unmount();
    fireEvent.focus(window);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch).toHaveBeenCalledTimes(3);
  } finally {
    view.unmount();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  }
});

const listInput = () => ({ limit: 50 });
const listPage = (reply: ResponseByEndpoint["case.list"]) =>
  reply.outcome.kind === "SUCCEEDED" ? reply.outcome.data : null;
const PageProbe = () => {
  const day = useBusinessDate(requester);
  const page = useEndpointPage<"case.list", CaseSummary, NonNullable<ReturnType<typeof listPage>>>(
    "case.list",
    listInput,
    listPage,
  );
  return (
    <>
      <output data-testid="page-date">{page.page?.basis.businessDate ?? "loading"}</output>
      <output data-testid="page-change">{day.changedDate ?? "unchanged"}</output>
    </>
  );
};
it("refreshes completed paginated rows on focus and business-date invalidation without polling", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
  let advanced = false;
  const fetch = vi.fn().mockImplementation(() =>
    Promise.resolve(
      response("case.list", "SUCCEEDED", {
        basis: {
          observedAt: "2026-11-04T22:00:00.000000Z",
          businessDate: advanced ? "2026-11-05" : "2026-11-04",
          timeZone: "Europe/Riga",
        },
        items: [],
        offers: [],
        nextCursor: null,
        exactReference: null,
      }),
    ),
  );
  vi.stubGlobal("fetch", fetch);
  const view = render(<PageProbe />);
  try {
    await waitFor(() => {
      expect(screen.getByTestId("page-date")).toHaveTextContent("2026-11-04");
    });
    advanced = true;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    fireEvent.focus(window);
    await waitFor(() => {
      expect(screen.getByTestId("page-date")).toHaveTextContent("2026-11-05");
    });
    expect(screen.getByTestId("page-change")).toHaveTextContent("2026-11-05");
    expect(fetch).toHaveBeenCalledTimes(3);
  } finally {
    view.unmount();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  }
});
