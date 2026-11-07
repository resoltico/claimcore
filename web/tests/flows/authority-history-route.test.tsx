import { presentedOutcome } from "../support/read-view.fixtures";
import * as serviceApi from "../../src/api/service";
import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { ActorRef, AuthorityHistoryEntry, Diagnostic } from "@contracts/client";
import { render, screen } from "../support/presentation-test-support";
import { mockEndpoint, getEndpointMock } from "../support/endpoint-mocks";
import { actorContextInput, workBasis } from "../support/inputs";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { navigate } from "../../src/routing/navigation";
import { Dashboard } from "../../src/views/Dashboard";
import { App } from "../../src/App";

const first = "10000000-0000-4000-8000-000000000001";
const second = "10000000-0000-4000-8000-000000000002";
const author: ActorRef = {
  actorId: "90000000-0000-4000-8000-000000000001",
  displayName: { kind: "NAMED", value: "Current history recorder" },
  principalKind: "HUMAN",
  availability: "AVAILABLE",
};
const entry = (actorId = first, sequence = 31): AuthorityHistoryEntry => ({
  sequence,
  recordedAt: "2026-09-01T10:30:00.000000Z",
  recordedBy: author,
  target: {
    actorId,
    displayName: { kind: "NAMED", value: `Current target ${actorId === first ? "one" : "two"}` },
    principalKind: "HUMAN",
    availability: "AVAILABLE",
  },
  act: { kind: "DISABLE" },
});
const page = (entries: ReadonlyArray<AuthorityHistoryEntry>, nextCursor: string | null = null) =>
  new Response(
    JSON.stringify({
      outcome: presentedOutcome("authority.history", "SUCCEEDED", {
        basis: workBasis,
        entries,
        nextCursor,
      }),
    }),
    { status: 200, headers: { "content-type": "application/json" } },
  );
const rejected = (data: Diagnostic) =>
  new Response(JSON.stringify({ outcome: { kind: "REJECTED", data } }), {
    status: 200,
    headers: { "content-type": "application/json" },
  });
const pending = <T,>() => {
  let finish: (value: T) => void = () => {
    throw new Error("Pending history read is not initialized");
  };
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const requestAt = (
  fetch: ReturnType<typeof vi.fn<typeof globalThis.fetch>>,
  index: number,
): unknown => {
  const [path, options] = fetch.mock.calls[index]!;
  expect(path).toBe("/api/authority.history");
  expect(options?.method).toBe("POST");
  const bytes = options?.body;
  if (typeof bytes !== "string") {
    throw new Error("History must send its actual structured request");
  }
  return JSON.parse(bytes) as unknown;
};
const showHistory = (actorId = first) => {
  navigate(actorId === "" ? "/authority-history" : `/authority-history?actorId=${actorId}`, true);
  return render(<Dashboard />);
};
beforeEach(() => {
  stubViewCounts();
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: presentedOutcome("context", "SUCCEEDED", {
        requester: actorContextInput({
          productVersion: "0.15.0",
          businessDate: workBasis.businessDate,
          timeZone: workBasis.timeZone,
          observedAt: workBasis.observedAt,
          capabilities: ["READ_AUTHORITY"],
          views: ["OPERATIONS"],
          offers: [],
        }),
      }),
    },
  });
});
afterEach(() => vi.unstubAllGlobals());

it("sends the direct actor URL filter on every page without a people lookup", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry()], "older-page"))
    .mockResolvedValueOnce(page([{ ...entry(first, 30), act: { kind: "ENABLE" } }]));
  vi.stubGlobal("fetch", fetch);
  const view = showHistory();
  await userEvent.setup().click(await screen.findByRole("button", { name: "Load more history" }));
  await screen.findByText("Enable account");
  expect(view.container.querySelectorAll(".authority-history .table-view tbody > tr")).toHaveLength(
    2,
  );
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(requestAt(fetch, 0)).toEqual({ limit: 50, actorId: first });
  expect(requestAt(fetch, 1)).toEqual({ limit: 50, actorId: first, cursor: "older-page" });
  expect(window.location.search).toBe(`?actorId=${first}`);
});

it("keeps the bare history route unfiltered and renders supplied actors without local row filtering", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry(), entry(second, 30)]));
  vi.stubGlobal("fetch", fetch);
  showHistory("");
  await screen.findByText("Current target two");
  expect(screen.getByText("Current target one")).toBeVisible();
  expect(fetch).toHaveBeenCalledOnce();
  expect(requestAt(fetch, 0)).toEqual({ limit: 50 });
});

it("does not reconstruct or filter authoritative rows from a requested actor ID", async () => {
  // Shape-valid supplied rows are server-owned; this control catches local target filtering,
  // rather than claiming that a mixed page is produced by the Core target-filter query.
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry(), entry(second, 30)]));
  vi.stubGlobal("fetch", fetch);
  showHistory();
  await screen.findByText("Current target two");
  expect(screen.getByText("Current target one")).toBeVisible();
  expect(fetch).toHaveBeenCalledOnce();
  expect(requestAt(fetch, 0)).toEqual({ limit: 50, actorId: first });
});

it("cancels an obsolete actor observation and ignores a late page from an uncooperative transport", async () => {
  const requests = vi.spyOn(serviceApi, "requestEndpoint");
  const late = pending<Response>();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry()], "older-page"))
    .mockReturnValueOnce(late.promise)
    .mockResolvedValueOnce(page([entry(second, 29)]));
  vi.stubGlobal("fetch", fetch);
  const view = showHistory();
  await userEvent.setup().click(await screen.findByRole("button", { name: "Load more history" }));
  await act(() =>
    Promise.resolve().then(() => {
      navigate(`/authority-history?actorId=${second}`);
    }),
  );
  await screen.findByText("Current target two");
  const historyRequests = requests.mock.calls.filter(
    ([endpoint]) => endpoint === "authority.history",
  );
  expect(historyRequests[1]![3]?.aborted).toBe(true);
  expect(fetch.mock.calls[1]![1]?.signal?.aborted).toBe(true);
  expect(screen.queryByText("Current target one")).toBeNull();
  await act(() =>
    Promise.resolve().then(() => {
      late.finish(page([entry(first, 28)]));
    }),
  );
  expect(screen.queryByText("Current target one")).toBeNull();
  expect(view.container.querySelectorAll(".authority-history .table-view tbody > tr")).toHaveLength(
    1,
  );
  expect(fetch).toHaveBeenCalledTimes(3);
  expect(requestAt(fetch, 2)).toEqual({ limit: 50, actorId: second });
});

it.each([
  { code: "RESOURCE_UNAVAILABLE", action: "ASK_FOR_ACCESS" },
  { code: "INVALID_CURSOR", action: "READ_CURRENT", target: "cursor" },
] as const)(
  "clears retained history on $code and reloads the same filter without a cursor",
  async (diagnostic) => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(page([entry()], "older-page"))
      .mockResolvedValueOnce(rejected(diagnostic))
      .mockResolvedValueOnce(page([entry(second, 29)]));
    vi.stubGlobal("fetch", fetch);
    showHistory();
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Load more history" }));
    await screen.findByRole("alert");
    expect(screen.queryByText("Current target one")).toBeNull();
    expect(screen.queryByText("Current history recorder")).toBeNull();
    expect(screen.queryByRole("button", { name: "Load more history" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Reload history" }));
    await screen.findByText("Current target two");
    expect(requestAt(fetch, 2)).toEqual({ limit: 50, actorId: first });
  },
);

it("retains only the same history page on transient delivery failure and retries its exact cursor", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry()], "older-page"))
    .mockRejectedValueOnce(new TypeError("Synthetic unreachable service"))
    .mockResolvedValueOnce(page([{ ...entry(first, 30), act: { kind: "ENABLE" } }]));
  vi.stubGlobal("fetch", fetch);
  const view = showHistory();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Load more history" }));
  await screen.findByRole("alert");
  expect(screen.getByText("Current target one")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Load more history" }));
  await screen.findByText("Enable account");
  expect(view.container.querySelectorAll(".authority-history .table-view tbody > tr")).toHaveLength(
    2,
  );
  expect(requestAt(fetch, 1)).toEqual({ limit: 50, actorId: first, cursor: "older-page" });
  expect(requestAt(fetch, 2)).toEqual({ limit: 50, actorId: first, cursor: "older-page" });
});

it("renders the provided case scope link and actor refs without revealing a bootstrap principal as a name", async () => {
  const scoped: AuthorityHistoryEntry = {
    ...entry(),
    act: {
      kind: "GRANT",
      role: "CASE_READER",
      scope: {
        kind: "CASE",
        caseId: "20000000-0000-4000-8000-000000000001",
        caseReference: "FT-0007",
      },
    },
  };
  const registered: AuthorityHistoryEntry = {
    ...entry(second, 30),
    act: {
      kind: "REGISTER",
      principal: {
        kind: "HUMAN",
        issuer: "https://history.example/",
        subject: "PRIVATE_PRINCIPAL_CANARY",
      },
    },
  };
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(page([scoped, registered]));
  vi.stubGlobal("fetch", fetch);
  showHistory();
  expect(await screen.findByRole("link", { name: "FT-0007" })).toHaveAttribute(
    "href",
    "/cases/id/20000000-0000-4000-8000-000000000001",
  );
  expect(screen.getByText("Current target two")).toBeVisible();
  expect(document.body).not.toHaveTextContent("PRIVATE_PRINCIPAL_CANARY");
  expect(fetch).toHaveBeenCalledOnce();
});

it.each(["", "invalid", `${first}&actorId=${second}`])(
  "keeps supplied invalid or duplicate actor query offline: %s",
  async (actor) => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    vi.stubGlobal("fetch", fetch);
    navigate(`/authority-history?actorId=${actor}`, true);
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Page not found" })).toBeVisible();
    expect(fetch).not.toHaveBeenCalled();
    expect(getEndpointMock("context")).not.toHaveBeenCalled();
  },
);
