import {
  installationActions,
  requesterLinks,
  activeRequester,
} from "../support/human-requester.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";

import * as serviceApi from "../../src/api/service";
import userEvent from "@testing-library/user-event";
import { act, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { ActorRef, RegionTimelineEntry, DataRejection } from "@contracts/client";
import { render, screen } from "../support/presentation-test-support";
import { mockEndpoint, getEndpointMock } from "../support/endpoint-mocks";
import { workBasis } from "../support/inputs";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { navigate } from "../../src/routing/navigation";
import { Dashboard } from "../../src/shell/Dashboard";
import { App } from "../../src/shell/App";

const first = "10000000-0000-4000-8000-000000000001";
const second = "10000000-0000-4000-8000-000000000002";
const author: ActorRef = {
  actorId: "90000000-0000-4000-8000-000000000001",
  displayName: { kind: "NAMED", value: "Current history recorder" },
  principalKind: "HUMAN",
  availability: "AVAILABLE",
};
type HistoryRow = { content: RegionTimelineEntry };
const entry = (actorId = first, sequence = 31): HistoryRow => ({
  content: {
    key: `${sequence}:0`,
    when: "2026-09-01T10:30:00.000000Z",
    who: { kind: "ACTOR", value: author },
    summary: { messageId: "task.people.disable.label", arguments: [] },
    target: {
      kind: "ACTOR",
      value: {
        actorId,
        displayName: {
          kind: "NAMED",
          value: `Current target ${actorId === first ? "one" : "two"}`,
        },
        principalKind: "HUMAN",
        availability: "AVAILABLE",
      },
    },
    highlights: [],
    details: [],
  },
});
const page = (rows: ReadonlyArray<HistoryRow>, nextCursor: string | null = null) =>
  new Response(
    JSON.stringify(
      humanReadReply("people.history", {
        screen: {
          regions: [
            {
              role: "ACTIVITY",
              content: {
                kind: "TIMELINE",
                entries: rows.map((row) => row.content),
              },
            },
          ],
        },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    ),
    { status: 200, headers: { "content-type": "application/json" } },
  );
const rejected = (data: DataRejection) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "REFUSED",
          advice: {
            message: { messageId: `diagnostic.${data.code}`, arguments: [] },
            action: data.action,
          },
        },
      },
    }),
    { status: 200, headers: { "content-type": "application/json" } },
  );
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
  endpoint = "/api/people.history",
): unknown => {
  const [path, options] = fetch.mock.calls[index]!;
  expect(path).toBe(endpoint);
  expect(options?.method).toBe("POST");
  const bytes = options?.body;
  if (typeof bytes !== "string") {
    throw new Error("History must send its actual structured request");
  }
  return JSON.parse(bytes) as unknown;
};
const showHistory = (actorId = first) => {
  navigate(actorId === "" ? "/people-history" : `/people-history?actorId=${actorId}`, true);
  return render(<Dashboard />);
};
beforeEach(() => {
  stubViewCounts();
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("context", {
        ...activeRequester,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        actions: installationActions(["people.list", "people.grants", "people.history"]),
        links: requesterLinks(["OPERATIONS"], ["people.list", "people.grants", "people.history"]),
      }),
    },
  });
});
afterEach(() => vi.unstubAllGlobals());

it("sends the direct actor URL filter on every page without a people lookup", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry()], "older-page"))
    .mockResolvedValueOnce(
      page([
        {
          ...entry(first, 30),
          content: {
            ...entry(first, 30).content,
            summary: { messageId: "task.people.enable.label", arguments: [] },
          },
        },
      ]),
    );
  vi.stubGlobal("fetch", fetch);
  const view = showHistory();
  await userEvent.setup().click(await screen.findByRole("button", { name: "Load more" }));
  await screen.findByText("Enable account");
  expect(view.container.querySelectorAll(".people-history .region-timeline > li")).toHaveLength(2);
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

it("applies and clears the Actor filter through the history route", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry()]))
    .mockResolvedValueOnce(page([entry(second, 29)]))
    .mockResolvedValueOnce(new Response(null, { status: 503 }))
    .mockResolvedValueOnce(page([entry()]));
  vi.stubGlobal("fetch", fetch);
  showHistory("");
  await screen.findByText("Current target one");
  const user = userEvent.setup();
  const actor = () => screen.getByRole("combobox", { name: "Person" });
  await user.type(actor(), second);
  await user.keyboard("{Enter}");
  await screen.findByText("Current target two");
  expect(window.location.search).toBe(`?actorId=${second}`);
  expect(requestAt(fetch, 1)).toEqual({ limit: 50, actorId: second });
  await user.click(screen.getByRole("button", { name: "Download CSV" }));
  expect(requestAt(fetch, 2, "/api/export")).toEqual({
    query: "people.history",
    selection: { actorId: second },
  });
  await user.clear(actor());
  await user.keyboard("{Enter}");
  await screen.findByText("Current target one");
  expect(window.location.search).toBe("");
  expect(requestAt(fetch, 3)).toEqual({ limit: 50 });
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
  await userEvent.setup().click(await screen.findByRole("button", { name: "Load more" }));
  await act(() =>
    Promise.resolve().then(() => {
      navigate(`/people-history?actorId=${second}`);
    }),
  );
  await screen.findByText("Current target two");
  const historyRequests = requests.mock.calls.filter(([endpoint]) => endpoint === "people.history");
  expect(historyRequests[1]![3]?.aborted).toBe(true);
  expect(fetch.mock.calls[1]![1]?.signal?.aborted).toBe(true);
  expect(screen.queryByText("Current target one")).toBeNull();
  await act(() =>
    Promise.resolve().then(() => {
      late.finish(page([entry(first, 28)]));
    }),
  );
  expect(screen.queryByText("Current target one")).toBeNull();
  expect(view.container.querySelectorAll(".people-history .region-timeline > li")).toHaveLength(1);
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
    await user.click(await screen.findByRole("button", { name: "Load more" }));
    await screen.findByRole("alert");
    expect(screen.queryByText("Current target one")).toBeNull();
    expect(screen.queryByText("Current history recorder")).toBeNull();
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Refresh" }));
    await screen.findByText("Current target two");
    expect(requestAt(fetch, 2)).toEqual({ limit: 50, actorId: first });
  },
);

it("masks history on a transient delivery failure and reloads from page one", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page([entry()], "older-page"))
    .mockRejectedValueOnce(new TypeError("Synthetic unreachable service"))
    .mockResolvedValueOnce(
      page([
        {
          ...entry(first, 30),
          content: {
            ...entry(first, 30).content,
            summary: { messageId: "task.people.enable.label", arguments: [] },
          },
        },
      ]),
    );
  vi.stubGlobal("fetch", fetch);
  const view = showHistory();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Load more" }));
  await screen.findByRole("alert");
  expect(screen.queryByText("Current target one")).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Refresh" }));
  await screen.findByText("Enable account");
  expect(view.container.querySelectorAll(".people-history .region-timeline > li")).toHaveLength(1);
  expect(requestAt(fetch, 1)).toEqual({ limit: 50, actorId: first, cursor: "older-page" });
  expect(requestAt(fetch, 2)).toEqual({ limit: 50, actorId: first });
});

it("renders the affected account link and the separate recorder without revealing a bootstrap principal as a name", async () => {
  const scoped: HistoryRow = {
    content: {
      ...entry().content,
      link: { endpoint: "people.grants", input: { actorId: first } },
      target: { kind: "TEXT", value: "Affected account one" },
      summary: { messageId: "task.people.grant.label", arguments: [] },
      highlights: [
        {
          titleId: "label.recordedFacts",
          entries: [
            {
              key: "scopeReference",
              labelId: "field.caseReference.label",
              prominence: "PRIMARY",
              value: { kind: "REFERENCE", value: "CC-FC1F-GPNK-TVKH-8ZEJ-96" },
            },
          ],
        },
      ],
    },
  };
  const registered: HistoryRow = {
    ...entry(second, 30),
    content: {
      ...entry(second, 30).content,
      summary: { messageId: "task.people.register.label", arguments: [] },
      details: [
        {
          titleId: "label.technicalDetails",
          entries: [
            {
              key: "subject",
              labelId: "field.principal.subject.label",
              prominence: "DETAIL",
              value: { kind: "IDENTIFIER", value: "PRIVATE_PRINCIPAL_CANARY" },
            },
          ],
        },
      ],
    },
  };
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(page([scoped, registered]));
  vi.stubGlobal("fetch", fetch);
  showHistory();
  expect(await screen.findByRole("link", { name: "Affected account one" })).toHaveAttribute(
    "href",
    `/people/${first}`,
  );
  expect(screen.getByText("Current target two")).toBeVisible();
  expect(screen.queryByText("PRIVATE_PRINCIPAL_CANARY", { exact: true })).toBeNull();
  const row = screen.getByText("Current target two").closest("li")!;
  await userEvent.click(within(row).getByRole("button", { name: "View details" }));
  expect(
    screen.getByText("PRIVATE_PRINCIPAL_CANARY", { exact: true }).closest(".identifier-value"),
  ).toBeVisible();
  expect(fetch).toHaveBeenCalledOnce();
});

it.each(["", `${first}&actorId=${second}`])(
  "rejects invalid or duplicate actor queries before workspace reads: %s",
  async (actor) => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    vi.stubGlobal("fetch", fetch);
    navigate(`/people-history?actorId=${actor}`, true);
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Page not found" })).toBeVisible();
    expect(fetch.mock.calls.map(([path]) => path)).toEqual(["/api/session"]);
    expect(getEndpointMock("context")).not.toHaveBeenCalled();
  },
);
