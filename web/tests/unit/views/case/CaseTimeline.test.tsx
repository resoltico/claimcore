import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { humanReadHttpReply as response } from "../../../support/human-read-reply.fixtures";
import { endpointResponse } from "../../../support/transport-response.fixtures";
import { openedTimelineEntry, timelineDocument } from "../../../support/timeline-document.fixtures";
import { CaseTimeline } from "../../../../src/views/case/CaseTimeline";
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
    const item =
      input.filter === "WORK"
        ? { ...openedTimelineEntry, summary: { messageId: "act.UPDATE_WORK.label", arguments: [] } }
        : openedTimelineEntry;
    return Promise.resolve(response("case.timeline", timelineDocument([item])));
  });
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(<CaseTimeline target={target} />);
  await screen.findByText("Timeline recorder");
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(JSON.parse(requestText(fetch.mock.calls[0]?.[1]?.body))).toEqual({ target, limit: 50 });
  await user.click(screen.getByRole("tab", { name: "Case facts" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(JSON.parse(requestText(fetch.mock.calls[1]?.[1]?.body))).toEqual({
    target,
    limit: 50,
    filter: "FACTS",
  });
  await user.click(screen.getByRole("tab", { name: "Work and reminders" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(3);
  });
  expect(JSON.parse(requestText(fetch.mock.calls[2]?.[1]?.body))).toEqual({
    target,
    limit: 50,
    filter: "WORK",
  });
  await user.click(screen.getByRole("tab", { name: "Notes" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(4);
  });
  expect(JSON.parse(requestText(fetch.mock.calls[3]?.[1]?.body))).toEqual({
    target,
    limit: 50,
    filter: "NOTES",
  });
  await waitFor(() => {
    expect(
      view.container.querySelectorAll(".case-timeline-panel .region-timeline > li"),
    ).toHaveLength(1);
  });
});
it("follows only the issued cursor, clears refused continuations and retries without a cursor", async () => {
  const refused = vi.fn();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      response("case.timeline", timelineDocument([openedTimelineEntry], "issued")),
    )
    .mockResolvedValueOnce(
      endpointResponse("case.timeline", {
        representation: "HUMAN",
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "REFUSED",
            advice: {
              message: { messageId: "diagnostic.INVALID_CURSOR", arguments: [] },
              action: "READ_CURRENT",
            },
          },
        },
      }),
    )
    .mockResolvedValueOnce(response("case.timeline", timelineDocument([openedTimelineEntry])));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(<CaseTimeline target={target} onDisclosureChanged={refused} />);
  await user.click(await screen.findByRole("button", { name: "Load more" }));
  await screen.findByRole("alert");
  expect(JSON.parse(requestText(fetch.mock.calls[1]?.[1]?.body))).toEqual({
    target,
    limit: 50,
    cursor: "issued",
  });
  expect(
    view.container.querySelectorAll(".case-timeline-panel .region-timeline > li"),
  ).toHaveLength(0);
  expect(refused).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Try again" }));
  await screen.findByText("Timeline recorder");
  expect(JSON.parse(requestText(fetch.mock.calls[2]?.[1]?.body))).toEqual({ target, limit: 50 });
});
it("uses the supplied event timeline and appends pages in core order", async () => {
  const first = {
    ...openedTimelineEntry,
    key: "2/0",
    summary: { messageId: "timeline.event.registrationAmended", arguments: [] },
    highlights: [
      {
        titleId: "field.claimantName.label",
        entries: [
          {
            key: "before",
            labelId: "label.before",
            prominence: "PRIMARY" as const,
            value: { kind: "PARTY" as const, value: "Core before" },
          },
          {
            key: "after",
            labelId: "label.after",
            prominence: "PRIMARY" as const,
            value: { kind: "PARTY" as const, value: "Core after" },
          },
        ],
      },
    ],
  };
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("case.timeline", timelineDocument([first], "issued")))
    .mockResolvedValueOnce(response("case.timeline", timelineDocument([openedTimelineEntry])));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(<CaseTimeline target={target} />);
  await screen.findByText("Timeline recorder");
  expect(view.container).toHaveTextContent("Core before");
  expect(view.container).toHaveTextContent("Core after");
  expect(fetch).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole("button", { name: "Load more" }));
  await waitFor(() => {
    expect(
      view.container.querySelectorAll(".case-timeline-panel .region-timeline > li"),
    ).toHaveLength(2);
  });
  const rows = view.container.querySelectorAll(".case-timeline-panel .region-timeline > li");
  expect(rows[0]).toHaveTextContent("Claim details updated");
  expect(rows[1]).toHaveTextContent("Case opened");
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
      .mockResolvedValueOnce(response("case.timeline", timelineDocument([openedTimelineEntry])))
      .mockImplementation(
        () =>
          new Promise((resolve) => {
            next.release = () => {
              resolve(
                response(
                  "case.timeline",
                  timelineDocument([{ ...openedTimelineEntry, key: "changed-target" }]),
                ),
              );
            };
          }),
      );
    vi.stubGlobal("fetch", fetch);
    const view = render(<CaseTimeline target={selector} />);
    await screen.findByText("Timeline recorder");
    const original = view.container.querySelector(".region-timeline > li")!;
    await act(async () => {
      view.rerender(<CaseTimeline target={{ ...selector }} />);
      await Promise.resolve();
    });
    expect(view.container.querySelector(".region-timeline > li")).toBe(original);
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
    expect(view.container.querySelector(".region-timeline > li")).not.toBe(original);
  },
);
