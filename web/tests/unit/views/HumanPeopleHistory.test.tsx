import { requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { PeopleHistory } from "../../../src/views/people/PeopleHistory";
import { type HumanReadDocument_people_history } from "@contracts/client";
const caseId = "00000000-0000-4000-8000-000000000001";
const document: HumanReadDocument_people_history = {
  screen: {
    regions: [
      {
        role: "ACTIVITY",
        content: {
          kind: "TIMELINE",
          entries: [
            {
              key: "1/0",
              when: "2026-11-09T12:00:00.000000Z",
              who: {
                actorId: caseId,
                displayName: {
                  kind: "NAMED",
                  value: "Core recorder Ψ",
                },
                principalKind: "HUMAN",
                availability: "AVAILABLE",
              },
              summary: {
                messageId: "task.people.grant.label",
                arguments: [],
              },
              highlights: [
                {
                  titleId: "label.peopleHistory",
                  entries: [
                    {
                      key: "scopeReference",
                      labelId: "field.caseReference.label",
                      prominence: "PRIMARY",
                      value: {
                        kind: "TEXT",
                        value: "Core scoped caption Ω",
                      },
                    },
                  ],
                },
              ],
              details: [],
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "PEOPLE_HISTORY",
    nextCursor: null,
    entries: [
      {
        key: "1/0",
        caseId,
      },
    ],
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const reply = (doc: unknown) => ({
  representation: "HUMAN",
  outcome: {
    kind: "SUCCEEDED",
    document: doc,
  },
});
afterEach(() => vi.unstubAllGlobals());
it("links an opaque supplied scope caption using its typed effect destination", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
    new Response(JSON.stringify(reply(document)), {
      status: 200,
      headers: {
        "Content-Type": "application/json",
      },
    }),
  );
  vi.stubGlobal("fetch", fetch);
  render(<PeopleHistory actorId={caseId} />);
  const link = await screen.findByRole("link", {
    name: "Core scoped caption Ω",
  });
  expect(link).toHaveAttribute("href", expect.stringContaining(caseId));
  expect(screen.getByText("Core recorder Ψ")).toBeVisible();
  expect(JSON.parse(requestText(fetch.mock.calls[0]![1]?.body))).toEqual({
    actorId: caseId,
    limit: 50,
  });
});
