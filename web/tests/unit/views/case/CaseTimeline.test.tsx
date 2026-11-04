import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { response } from "../../../support/service-ui.fixtures";
import { timelineEntry, timelinePage } from "../../../support/timeline.fixtures";
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
    expect(view.container.querySelectorAll(".case-timeline > li")).toHaveLength(1);
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
  expect(view.container.querySelectorAll(".case-timeline > li")).toHaveLength(0);
  expect(refused).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Try again" }));
  await screen.findByText("Timeline recorder");
  expect(JSON.parse(requestText(fetch.mock.calls[2]?.[1]?.body))).toEqual({ target, limit: 50 });
});
it("shows page-boundary differences immediately and appends work entries in core order", async () => {
  const first = timelineEntry({
    revision: 2,
    changes: {
      kind: "DISCLOSED",
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
  await screen.findByText("Core after");
  expect(fetch).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole("button", { name: "Load 50 more" }));
  await waitFor(() => {
    expect(view.container.querySelectorAll(".case-timeline > li")).toHaveLength(2);
  });
  expect(view.container.querySelector(".case-timeline > li")).toHaveTextContent("Core after");
});
