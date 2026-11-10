import { requestText } from "../../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { CaseTimeline } from "../../../../src/views/case/CaseTimeline";
import type { HumanReadDocument_caseTimeline } from "@contracts/client";

const target = { kind: "REFERENCE", caseReference: "Original reference Ω" } as const;
const document = (
  caption: string,
  disclosure: "FULL" | "SUMMARY",
  nextCursor: string | null,
): HumanReadDocument_caseTimeline => ({
  screen: {
    regions: [
      {
        role: "ACTIVITY",
        content: {
          kind: "TIMELINE",
          entries: [
            {
              key: caption,
              when: "2026-11-09T12:00:00.000000Z",
              who: {
                actorId: "00000000-0000-4000-8000-000000000001",
                displayName: { kind: "NAMED", value: caption },
                principalKind: "HUMAN",
                availability: "AVAILABLE",
              },
              summary: { messageId: "timeline.event.caseOpened", arguments: [] },
              highlights: [],
              details: [],
            },
          ],
        },
      },
    ],
  },
  context: { kind: "PAGE", tasks: [], nextCursor },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure,
});
const reply = (doc: HumanReadDocument_caseTimeline) =>
  new Response(
    JSON.stringify({ representation: "HUMAN", outcome: { kind: "SUCCEEDED", document: doc } }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
afterEach(() => vi.unstubAllGlobals());

it("renders only Core history and replaces prior full pages on a summary continuation", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(reply(document("Core full recorder Ω", "FULL", "issued")))
    .mockResolvedValueOnce(reply(document("Core summary recorder Ψ", "SUMMARY", null)));
  vi.stubGlobal("fetch", fetch);
  const changed = vi.fn();
  render(<CaseTimeline target={target} onDisclosureChanged={changed} />);
  await screen.findByText("Core full recorder Ω");
  await userEvent.click(screen.getByRole("button", { name: "Load more" }));
  await screen.findByText("Core summary recorder Ψ");
  expect(screen.queryByText("Core full recorder Ω")).not.toBeInTheDocument();
  expect(changed).toHaveBeenCalledTimes(1);
  expect(JSON.parse(requestText(fetch.mock.calls[1]![1]?.body))).toEqual({
    target,
    limit: 50,
    cursor: "issued",
  });
  expect(fetch.mock.calls[0]![1]?.headers).toMatchObject({ "ClaimCore-Representation": "HUMAN" });
});

it("uses declared semantic filters and masks history synchronously when its permission ceiling tightens", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(reply(document("Core full recorder Ω", "FULL", null)))
    .mockResolvedValueOnce(reply(document("Core work recorder Ψ", "FULL", null)))
    .mockImplementation(
      () =>
        new Promise(() => {
          /* Keep the resource read pending until disclosure masks it. */
        }),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(<CaseTimeline target={target} />);
  await screen.findByText("Core full recorder Ω");
  await userEvent.click(screen.getByRole("tab", { name: "Work and reminders" }));
  await screen.findByText("Core work recorder Ψ");
  expect(JSON.parse(requestText(fetch.mock.calls[1]![1]?.body))).toEqual({
    target,
    limit: 50,
    filter: "WORK",
  });
  view.rerender(<CaseTimeline target={target} disclosure="SUMMARY" />);
  expect(screen.queryByText("Core work recorder Ψ")).not.toBeInTheDocument();
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(3);
  });
});
