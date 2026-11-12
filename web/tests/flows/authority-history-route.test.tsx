import { humanReadReply } from "../support/human-read-reply.fixtures";
import { activeRequester } from "../support/human-requester.fixtures";
import * as serviceApi from "../../src/api/service";
import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { ActorRef, RegionTimelineEntry, Diagnostic } from "@contracts/client";
import { render, screen } from "../support/presentation-test-support";
import { mockEndpoint, getEndpointMock } from "../support/endpoint-mocks";
import { workBasis } from "../support/inputs";
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
type HistoryRow = { content: RegionTimelineEntry; caseId: string | null };
const entry = (actorId = first, sequence = 31): HistoryRow => ({
  caseId: null,
  content: {
    key: `${sequence}:0`,
    when: "2026-09-01T10:30:00.000000Z",
    who: author,
    summary: { messageId: "act.DISABLE.label", arguments: [] },
    highlights: [
      {
        titleId: "label.recordedFacts",
        entries: [
          {
            key: "target",
            labelId: "label.targetAccount",
            prominence: "PRIMARY",
            value: {
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
          },
        ],
      },
    ],
    details: [],
  },
});
const page = (rows: ReadonlyArray<HistoryRow>, nextCursor: string | null = null) =>
  new Response(
    JSON.stringify(
      humanReadReply("authority.history", {
        screen: {
          regions: [
            {
              role: "ACTIVITY",
              content: { kind: "TIMELINE", entries: rows.map((row) => row.content) },
            },
          ],
        },
        context: {
          kind: "AUTHORITY_HISTORY",
          entries: rows.map((row) => ({ key: row.content.key, caseId: row.caseId })),
          nextCursor,
        },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    ),
    { status: 200, headers: { "content-type": "application/json" } },
  );
const rejected = (data: Diagnostic) =>
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
      ...humanReadReply("context", {
        ...activeRequester,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        context: {
          ...activeRequester.context,
          views: ["OPERATIONS"],
          tasks: ["query:authorityList", "query:authorityGrants", "query:authorityHistory"],
        },
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
            summary: { messageId: "act.ENABLE.label", arguments: [] },
          },
        },
      ]),
    );
  vi.stubGlobal("fetch", fetch);
  const view = showHistory();
  await userEvent.setup().click(await screen.findByRole("button", { name: "Load more activity" }));
  await screen.findByText("Enable account");
  expect(view.container.querySelectorAll(".authority-history .region-timeline > li")).toHaveLength(
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
  await userEvent.setup().click(await screen.findByRole("button", { name: "Load more activity" }));
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
  expect(view.container.querySelectorAll(".authority-history .region-timeline > li")).toHaveLength(
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
    await user.click(await screen.findByRole("button", { name: "Load more activity" }));
    await screen.findByRole("alert");
    expect(screen.queryByText("Current target one")).toBeNull();
    expect(screen.queryByText("Current history recorder")).toBeNull();
    expect(screen.queryByRole("button", { name: "Load more activity" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Reload activity" }));
    await screen.findByText("Current target two");
    expect(requestAt(fetch, 2)).toEqual({ limit: 50, actorId: first });
  },
);

it("retains only the same history page on transient delivery failure and retries its exact cursor", async () => {
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
            summary: { messageId: "act.ENABLE.label", arguments: [] },
          },
        },
      ]),
    );
  vi.stubGlobal("fetch", fetch);
  const view = showHistory();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Load more activity" }));
  await screen.findByRole("alert");
  expect(screen.getByText("Current target one")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Load more activity" }));
  await screen.findByText("Enable account");
  expect(view.container.querySelectorAll(".authority-history .region-timeline > li")).toHaveLength(
    2,
  );
  expect(requestAt(fetch, 1)).toEqual({ limit: 50, actorId: first, cursor: "older-page" });
  expect(requestAt(fetch, 2)).toEqual({ limit: 50, actorId: first, cursor: "older-page" });
});

it("renders the provided case scope link and actor refs without revealing a bootstrap principal as a name", async () => {
  const scoped: HistoryRow = {
    caseId: "20000000-0000-4000-8000-000000000001",
    content: {
      ...entry().content,
      summary: { messageId: "act.GRANT.label", arguments: [] },
      highlights: [
        {
          titleId: "label.recordedFacts",
          entries: [
            {
              key: "scopeReference",
              labelId: "field.caseReference.label",
              prominence: "PRIMARY",
              value: { kind: "REFERENCE", value: "FT-0007" },
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
      summary: { messageId: "act.REGISTER.label", arguments: [] },
      details: [
        {
          titleId: "label.recordDetails",
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
  expect(await screen.findByRole("link", { name: "FT-0007" })).toHaveAttribute(
    "href",
    "/cases/id/20000000-0000-4000-8000-000000000001",
  );
  expect(screen.getByText("Current target two")).toBeVisible();
  expect(screen.getByText("PRIVATE_PRINCIPAL_CANARY", { exact: true })).not.toBeVisible();
  expect(
    screen.getByText("PRIVATE_PRINCIPAL_CANARY", { exact: true }).closest(".identifier-value"),
  ).not.toBeNull();
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
