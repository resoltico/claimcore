import { activeRequester } from "../../../support/human-requester.fixtures";
import { humanReadReply } from "../../../support/human-read-reply.fixtures";
import { workBasis } from "../../../support/inputs";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { RegionTimelineEntry } from "@contracts/client";
import { render, screen } from "../../../support/presentation-test-support";
import { context } from "../../../support/service-ui.fixtures";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";
import { AuthorityHistory } from "../../../../src/views/authority/AuthorityHistory";
import { preferenceKey } from "../../../../src/presentation/preferences";
const owner: RegionTimelineEntry = {
  key: "31:0",
  when: "2026-01-01T22:30:00.000000Z",
  who: context.self,
  summary: { messageId: "act.PROVISION_OWNER.label", arguments: [] },
  highlights: [
    {
      titleId: "label.recordedFacts",
      entries: [
        {
          key: "target",
          labelId: "label.targetAccount",
          prominence: "PRIMARY",
          value: { kind: "ACTOR", value: context.self },
        },
      ],
    },
  ],
  details: [],
};
const historyResponse = (
  entries: ReadonlyArray<RegionTimelineEntry>,
  nextCursor: string | null = null,
) =>
  new Response(
    JSON.stringify(
      humanReadReply("authority.history", {
        screen: { regions: [{ role: "ACTIVITY", content: { kind: "TIMELINE", entries } }] },
        context: {
          kind: "AUTHORITY_HISTORY",
          entries: entries.map((entry) => ({ key: entry.key, caseId: null })),
          nextCursor,
        },
        basis: { kind: "SNAPSHOT", observation: { ...workBasis, timeZone: "Europe/Riga" } },
        disclosure: "CURRENT",
      }),
    ),
    { status: 200, headers: { "content-type": "application/json" } },
  );
afterEach(() => vi.unstubAllGlobals());
it.each([
  {
    language: "en",
    displayLocale: "en-GB",
    label: "Set up the first owner",

    day: "2 Jan 2026",
    time: "00:30",
  },
  {
    language: "lv",
    displayLocale: "lv-LV",
    label: "Izveidot pirmo īpašnieku",

    day: "2026. gada 2. janv.",
    time: "00:30",
  },
  {
    language: "ar",
    displayLocale: "ar-EG",
    label: "إعداد المالك الأول",

    day: "٠٢\u200f/٠١\u200f/٢٠٢٦",
    time: "١٢:٣٠ ص",
  },
])(
  "localizes bootstrap history and renders actor names in $language",
  async ({ language, displayLocale, label, day, time }) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 3,
        language,
        displayLocale,
        mode: "system",
        accent: "green" as const,
      }),
    );
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(historyResponse([owner]));
    vi.stubGlobal("fetch", fetch);
    const view = render(
      <RequesterContext value={activeRequester}>
        <AuthorityHistory actorId="" />
      </RequesterContext>,
    );
    await screen.findByText(label, { exact: true });
    expect(screen.queryByText("PROVISION_OWNER")).toBeNull();
    expect(screen.getAllByText("Named account", { exact: true })).toHaveLength(2);
    const instant = view.container.querySelector(".region-timeline > li > p time");
    expect(instant).toHaveTextContent(day);
    expect(instant).toHaveTextContent(time);
    expect(instant).toHaveAttribute("dateTime", owner.when);
    expect(instant).toHaveAttribute("title", owner.when);
    expect(fetch).toHaveBeenCalledTimes(1);
  },
);
it("keeps the selected actor filter when appending an older authority history page", async () => {
  const older: RegionTimelineEntry = {
    ...owner,
    key: "30:0",
    summary: { messageId: "act.DISABLE.label", arguments: [] },
  };
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(historyResponse([owner], "older-page"))
    .mockResolvedValueOnce(historyResponse([older]));
  vi.stubGlobal("fetch", fetch);
  const view = render(<AuthorityHistory actorId={context.self.actorId} />);
  const more = await screen.findByRole("button", { name: "Load more activity" });
  await userEvent.setup().click(more);
  await screen.findByText("Disable account", { exact: true });
  expect(view.container.querySelectorAll(".region-timeline > li")).toHaveLength(2);
  expect(fetch).toHaveBeenLastCalledWith(
    "/api/authority.history",
    expect.objectContaining({
      body: JSON.stringify({ limit: 50, actorId: context.self.actorId, cursor: "older-page" }),
    }),
  );
  expect(screen.queryByRole("button", { name: "Load more activity" })).toBeNull();
});
