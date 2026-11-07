import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { actorRefInput } from "../../../support/inputs";
import { preferenceKey } from "../../../../src/presentation/preferences";
import { response } from "../../../support/service-ui.fixtures";
import { timelineEntry, timelinePage } from "../../../support/timeline.fixtures";
import { CaseTimeline, TimelineEntries } from "../../../../src/views/case/CaseTimeline";
const requestText = (body: unknown): string => {
  if (typeof body !== "string") {
    throw new Error("Expected JSON timeline request bytes.");
  }
  return body;
};
const target = { kind: "REFERENCE", caseReference: "CASE-1" } as const;
it("requests one unfiltered timeline, shows names and core differences, and resets on server filter selection", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, init) => {
    const input = JSON.parse(requestText(init?.body)) as { filter?: string };
    const item = timelineEntry();
    return Promise.resolve(
      response(
        "case.timeline",
        "SUCCEEDED",
        timelinePage([
          input.filter === "WORK" ? { ...item, kind: "WORK", act: "UPDATE_WORK" } : item,
        ]),
      ),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(<CaseTimeline target={target} />);
  await screen.findByText("Timeline recorder");
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(JSON.parse(requestText(fetch.mock.calls[0]?.[1]?.body))).toEqual({ target, limit: 50 });
  await user.click(screen.getByRole("button", { name: "Facts" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(JSON.parse(requestText(fetch.mock.calls[1]?.[1]?.body))).toEqual({
    target,
    limit: 50,
    filter: "FACTS",
  });
  await user.click(screen.getByRole("button", { name: "Work" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(3);
  });
  expect(JSON.parse(requestText(fetch.mock.calls[2]?.[1]?.body))).toEqual({
    target,
    limit: 50,
    filter: "WORK",
  });
  await waitFor(() => {
    expect(
      view.container.querySelectorAll(".case-timeline-panel .table-view tbody > tr"),
    ).toHaveLength(1);
  });
});
it("follows only the issued cursor, clears refused continuations and retries without a cursor", async () => {
  const refused = vi.fn();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      response("case.timeline", "SUCCEEDED", timelinePage([timelineEntry()], "issued")),
    )
    .mockResolvedValueOnce(
      response("case.timeline", "REJECTED", {
        code: "INVALID_CURSOR",
        action: "READ_CURRENT",
        target: "cursor",
      }),
    )
    .mockResolvedValueOnce(response("case.timeline", "SUCCEEDED", timelinePage([timelineEntry()])));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(<CaseTimeline target={target} onDisclosureRefused={refused} />);
  await user.click(await screen.findByRole("button", { name: "Load 50 more" }));
  await screen.findByRole("alert");
  expect(JSON.parse(requestText(fetch.mock.calls[1]?.[1]?.body))).toEqual({
    target,
    limit: 50,
    cursor: "issued",
  });
  expect(
    view.container.querySelectorAll(".case-timeline-panel .table-view tbody > tr"),
  ).toHaveLength(0);
  expect(refused).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Try again" }));
  await screen.findByText("Timeline recorder");
  expect(JSON.parse(requestText(fetch.mock.calls[2]?.[1]?.body))).toEqual({ target, limit: 50 });
});
it("uses the supplied event table and appends pages in core order", async () => {
  const first = timelineEntry({
    revision: 2,
    changes: {
      kind: "DISCLOSED",
      overview: {
        event: { kind: "REGISTRATION_AMENDED", messageId: "timeline.event.registrationAmended" },
        highlights: [{ kind: "CHANGE", index: 0 }],
      },
      items: [
        {
          kind: "CASE_FIELD",
          change: {
            field: "claimantName",
            before: { kind: "PARTY", value: "Core before" },
            after: { kind: "PARTY", value: "Core after" },
          },
        },
      ],
    },
  });
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.timeline", "SUCCEEDED", timelinePage([first], "issued")))
    .mockResolvedValueOnce(
      response(
        "case.timeline",
        "SUCCEEDED",
        timelinePage([
          {
            ...timelineEntry(),
            kind: "WORK",
            act: "UPDATE_WORK",
            revision: 1,
            position: { sequence: 3, entryHash: "b".repeat(64) },
          },
        ]),
      ),
    );
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(<CaseTimeline target={target} />);
  await screen.findByText("Timeline recorder");
  expect(view.container).not.toHaveTextContent("Core before");
  expect(view.container).not.toHaveTextContent("Core after");
  expect(fetch).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole("button", { name: "Load 50 more" }));
  await waitFor(() => {
    expect(
      view.container.querySelectorAll(".case-timeline-panel .table-view tbody > tr"),
    ).toHaveLength(2);
  });
  const rows = view.container.querySelectorAll(".case-timeline-panel .table-view tbody > tr");
  expect(rows[0]).toHaveTextContent("Open a case");
  expect(rows[1]).toHaveTextContent("Update case work");
});

it("renders only Core-selected highlights while keeping every exact difference in details", async () => {
  const user = userEvent.setup();
  const supplied = timelineEntry({
    act: "DECIDE",
    changes: {
      kind: "DISCLOSED",
      overview: {
        event: { kind: "DECISION_RECORDED", messageId: "timeline.event.decisionRecorded" },
        highlights: [{ kind: "CHANGE", index: 1 }],
      },
      items: [
        {
          kind: "CASE_FIELD",
          change: {
            field: "paymentDecisionDate",
            before: { kind: "DATE", value: "2026-10-01" },
            after: { kind: "DATE", value: "2026-10-10" },
          },
        },
        {
          kind: "CASE_MONEY",
          change: {
            field: "payableAmount",
            change: {
              before: { amount: "25000", currency: "AED" },
              after: { amount: "20000", currency: "AED" },
            },
          },
        },
      ],
    },
  });
  const view = render(<TimelineEntries items={[supplied]} observedAt={null} detail="CURRENT" />);
  const entry = view.container.querySelector(".timeline-entry")!;
  const compact = entry.querySelector(".timeline-changes-compact")!;
  const details = entry.querySelector("details")!;
  expect(compact.querySelectorAll(".timeline-change")).toHaveLength(1);
  expect(compact).toHaveTextContent("25,000.00 AED");
  expect(compact).toHaveTextContent("20,000.00 AED");
  expect(compact).not.toHaveTextContent("01/10/2026");
  expect(compact).not.toHaveTextContent("10/10/2026");
  expect(details.querySelectorAll(".timeline-change")).toHaveLength(2);
  expect(details.querySelector(".timeline-changes")).toHaveTextContent("01/10/2026");
  expect(details.querySelector(".timeline-changes")).toHaveTextContent("10/10/2026");
  expect(details).not.toHaveAttribute("open");
  await user.click(details.querySelector("summary")!);
  expect(details).toHaveAttribute("open");
  expect(details).toHaveTextContent(supplied.operationId);
  expect(compact).not.toHaveTextContent(supplied.operationId);
});
it.each(["SUMMARY", "ERASED"] as const)(
  "keeps %s timeline disclosure free of both compact and expanded differences",
  (detail) => {
    const view = render(
      <TimelineEntries
        items={[
          timelineEntry({
            changes: {
              kind: "DISCLOSED",
              overview: {
                event: {
                  kind: "HANDLING_SUMMARY_ADDED",
                  messageId: "timeline.event.handlingSummaryAdded",
                },
                highlights: [{ kind: "CHANGE", index: 0 }],
              },
              items: [
                {
                  kind: "HANDLING_SUMMARY",
                  change: { before: null, after: "Must stay undisclosed" },
                },
              ],
            },
          }),
        ]}
        observedAt={null}
        detail={detail}
      />,
    );
    expect(view.container.querySelector(".timeline-changes")).toBeNull();
    expect(view.container).not.toHaveTextContent("Must stay undisclosed");
  },
);

it("keeps the visible act, actor and details text in the disclosure's accessible name", () => {
  const view = render(
    <TimelineEntries items={[timelineEntry()]} observedAt={null} detail="CURRENT" />,
  );
  const summary = view.container.querySelector("summary")!;
  expect(summary).not.toHaveAttribute("aria-label");
  expect(summary).not.toHaveAttribute("aria-labelledby");
  expect(summary.querySelector(":scope > strong")).toHaveTextContent("Case opened");
  expect(summary).toHaveAccessibleName(/Case opened/u);
  expect(summary).toHaveAccessibleName(/Timeline recorder/u);
  expect(summary).toHaveAccessibleName(/Show details/u);
  expect(summary).toHaveTextContent("Case opened Timeline recorder");
});

it("names an Arabic responsibility disclosure from its complete visible event and attribution", () => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language: "ar",
      displayLocale: "ar-EG",
      mode: "light",
      accent: "blue",
    }),
  );
  try {
    const original = timelineEntry();
    const view = render(
      <TimelineEntries
        items={[
          {
            ...original,
            kind: "WORK",
            act: "UPDATE_WORK",
            recordedBy: actorRefInput({
              displayName: { kind: "NAMED", value: "Plain Sight Field Owner" },
            }),
            changes: {
              kind: "DISCLOSED",
              items: [],
              overview: {
                event: {
                  kind: "RESPONSIBILITY_ASSIGNED",
                  messageId: "timeline.event.responsibilityAssigned",
                  person: actorRefInput({ displayName: { kind: "NAMED", value: "Alex Handler" } }),
                },
                highlights: [],
              },
            },
          },
        ]}
        observedAt={null}
        detail="CURRENT"
      />,
    );
    const summary = view.container.querySelector("summary")!;
    expect(summary).not.toHaveAttribute("aria-labelledby");
    expect(summary).not.toHaveAttribute("aria-label");
    expect(summary).toHaveAccessibleName(
      /أُسندت المسؤولية إلى Alex Handler Plain Sight Field Owner/u,
    );
    expect(summary).toHaveAccessibleName(/عرض التفاصيل/u);
    expect(summary.querySelectorAll("bdi")).toHaveLength(2);
  } finally {
    localStorage.removeItem(preferenceKey);
  }
});

it.each([
  { kind: "REFERENCE", caseReference: "CASE-1" },
  { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
] as const)(
  "retains the same supplied row for equivalent $kind selectors and reloads changed selectors",
  async (selector) => {
    const next = {
      release: (): void => {
        throw new Error("No changed-target read started.");
      },
    };
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(
        response("case.timeline", "SUCCEEDED", timelinePage([timelineEntry()])),
      )
      .mockImplementation(
        () =>
          new Promise((resolve) => {
            next.release = () => {
              resolve(
                response("case.timeline", "SUCCEEDED", {
                  ...timelinePage([timelineEntry()]),
                  caseId: "20000000-0000-4000-8000-000000000002",
                  caseReference: "CASE-2",
                }),
              );
            };
          }),
      );
    vi.stubGlobal("fetch", fetch);
    const view = render(<CaseTimeline target={selector} />);
    await screen.findByText("Timeline recorder");
    const original = view.container.querySelector(".table-view tbody tr")!;
    await act(async () => {
      view.rerender(<CaseTimeline target={{ ...selector }} />);
      await Promise.resolve();
    });
    expect(view.container.querySelector(".table-view tbody tr")).toBe(original);
    expect(fetch).toHaveBeenCalledTimes(1);

    const changed =
      selector.kind === "ID"
        ? { kind: "ID" as const, caseId: "20000000-0000-4000-8000-000000000002" }
        : { kind: "REFERENCE" as const, caseReference: "CASE-2" };
    await act(async () => {
      view.rerender(<CaseTimeline target={changed} />);
      await Promise.resolve();
    });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(JSON.parse(requestText(fetch.mock.calls[1]?.[1]?.body))).toEqual({
      target: changed,
      limit: 50,
    });
    expect(original.isConnected).toBe(false);
    await act(async () => {
      next.release();
      await Promise.resolve();
    });
    await screen.findByText("Timeline recorder");
    expect(view.container.querySelector(".table-view tbody tr")).not.toBe(original);
  },
);
