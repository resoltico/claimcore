import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { AuthorityHistoryEntry } from "@contracts/client";
import { render, screen } from "../../../support/presentation-test-support";
import { context, response } from "../../../support/service-ui.fixtures";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";
import { AuthorityHistory } from "../../../../src/views/authority/AuthorityHistory";
import { preferenceKey } from "../../../../src/presentation/preferences";
const owner: AuthorityHistoryEntry = {
  sequence: 31,
  recordedAt: "2026-01-01T22:30:00.000000Z",
  recordedBy: context.actorId,
  targetActorId: context.actorId,
  act: {
    kind: "PROVISION_OWNER",
    principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "synthetic-owner" },
  },
};
afterEach(() => vi.unstubAllGlobals());
it.each([
  {
    language: "en",
    displayLocale: "en-GB",
    label: "Provision owner",
    you: "You",
    day: "2 Jan 2026",
    time: "00:30",
  },
  {
    language: "lv",
    displayLocale: "lv-LV",
    label: "Iestatīt sākotnējo īpašnieku",
    you: "Jūs",
    day: "2026. gada 2. janv.",
    time: "00:30",
  },
  {
    language: "ar",
    displayLocale: "ar-EG",
    label: "تعيين المالك الأول",
    you: "أنت",
    day: "٠٢\u200f/٠١\u200f/٢٠٢٦",
    time: "١٢:٣٠ ص",
  },
])(
  "localizes bootstrap history and requester attribution in $language",
  async ({ language, displayLocale, label, you, day, time }) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 2, language, displayLocale, theme: "SYSTEM" }),
    );
    const fetch = vi
      .fn()
      .mockResolvedValue(
        response("authority.history", "SUCCEEDED", { entries: [owner], nextCursor: null }),
      );
    vi.stubGlobal("fetch", fetch);
    const view = render(
      <RequesterContext
        value={{ ...context, capabilities: ["READ_AUTHORITY"], timeZone: "Europe/Riga" }}
      >
        <AuthorityHistory actorId="" />
      </RequesterContext>,
    );
    await screen.findByText(label, { exact: true });
    expect(screen.queryByText("PROVISION_OWNER")).toBeNull();
    expect(screen.getAllByText(you, { exact: true })).toHaveLength(2);
    const instant = view.container.querySelector("li header time");
    expect(instant).toHaveTextContent(day);
    expect(instant).toHaveTextContent(time);
    expect(instant).toHaveAttribute("dateTime", owner.recordedAt);
    expect(instant).toHaveAttribute("title", owner.recordedAt);
    expect(fetch).toHaveBeenCalledTimes(1);
  },
);
it("keeps the selected actor filter when appending an older authority history page", async () => {
  const older: AuthorityHistoryEntry = { ...owner, sequence: 30, act: { kind: "DISABLE" } };
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(
      response("authority.history", "SUCCEEDED", { entries: [owner], nextCursor: "older-page" }),
    )
    .mockResolvedValueOnce(
      response("authority.history", "SUCCEEDED", { entries: [older], nextCursor: null }),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(<AuthorityHistory actorId={context.actorId} />);
  const more = await screen.findByRole("button", { name: "Load more history" });
  await userEvent.setup().click(more);
  await screen.findByText("Disable actor", { exact: true });
  expect(view.container.querySelectorAll("li")).toHaveLength(2);
  expect(fetch).toHaveBeenLastCalledWith(
    "/api/authority.history",
    expect.objectContaining({
      body: JSON.stringify({ limit: 50, actorId: context.actorId, cursor: "older-page" }),
    }),
  );
  expect(screen.queryByRole("button", { name: "Load more history" })).toBeNull();
});
