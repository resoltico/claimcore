import { useEndpointPage } from "../../../src/hooks/useEndpointPage";
import type { HumanReadDocument_caseList, ResponseFor } from "@contracts/client";
import { act } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { useEndpoint } from "../../../src/hooks/useEndpoint";
import { useBusinessDate } from "../../../src/hooks/useBusinessDate";
import { render, screen, waitFor, fireEvent } from "../../support/presentation-test-support";
import { activeRequester } from "../../support/human-requester.fixtures";
const requester = {
  ...activeRequester,
  basis: {
    ...activeRequester.basis,
    observation: { ...activeRequester.basis.observation, businessDate: "2026-11-04" },
  },
};
const document = (businessDate: string): HumanReadDocument_caseList => ({
  screen: { regions: [] },
  context: { kind: "CASES", cases: [], nextCursor: null, exactCaseId: null, tasks: [] },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-04T22:00:00.000000Z",
      businessDate,
      timeZone: "Europe/Riga",
    },
  },
  disclosure: "CURRENT",
});
const response = (businessDate: string) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: document(businessDate) },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
const summaryDate = (value: ResponseFor<"case.list", "HUMAN">["outcome"] | undefined): string => {
  if (value?.kind === "SUCCEEDED") {
    if (value.document.basis.kind === "SNAPSHOT") {
      return value.document.basis.observation.businessDate;
    }
    return "No observation";
  }
  return "Loading";
};
const Probe = () => {
  const day = useBusinessDate(requester);
  const summary = useEndpoint("case.list", { limit: 50 });
  const value = summary.value?.outcome;
  return (
    <>
      <output data-testid="summary">{summaryDate(value)}</output>
      <output data-testid="change">{day.changedDate ?? "unchanged"}</output>
    </>
  );
};
it("refreshes date-dependent reads on focus and server-day changes without polling or work.overview", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
  vi.setSystemTime(new Date("2031-06-15T12:00:00Z"));
  let advanced = false;
  const fetch = vi.fn((url: string) => {
    expect(url).toBe("/api/case.list");
    return Promise.resolve(response(advanced ? "2026-11-05" : "2026-11-04"));
  });
  vi.stubGlobal("fetch", fetch);
  const view = render(<Probe />);
  try {
    await waitFor(() => {
      expect(screen.getByTestId("summary")).toHaveTextContent("2026-11-04");
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    advanced = true;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    fireEvent.focus(window);
    await waitFor(() => {
      expect(screen.getByTestId("summary")).toHaveTextContent("2026-11-05");
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
const listPage = (reply: ResponseFor<"case.list", "HUMAN">) =>
  reply.outcome.kind === "SUCCEEDED" && reply.outcome.document.basis.kind === "SNAPSHOT"
    ? {
        items: reply.outcome.document.context.cases,
        nextCursor: reply.outcome.document.context.nextCursor,
        basis: reply.outcome.document.basis.observation,
        screen: reply.outcome.document.screen,
      }
    : null;
const PageProbe = () => {
  const day = useBusinessDate(requester);
  const page = useEndpointPage<
    "case.list",
    HumanReadDocument_caseList["context"]["cases"][number],
    NonNullable<ReturnType<typeof listPage>>
  >("case.list", listInput, listPage);
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
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockImplementation(() => Promise.resolve(response(advanced ? "2026-11-05" : "2026-11-04")));
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
