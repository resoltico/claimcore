import { within, waitFor } from "@testing-library/react";
import { service } from "../../../../src/api/service";
import { actorRefInput } from "../../../support/inputs";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { response } from "../../../support/service-ui.fixtures";
import { ReminderList } from "../../../../src/views/work/ReminderList";
import { preferenceKey } from "../../../../src/presentation/preferences";
import type { ReminderReadModel } from "@contracts/client";

const basis = {
  observedAt: "2026-11-03T08:00:00.000000Z",
  businessDate: "2026-11-03",
  timeZone: "Etc/UTC",
};
const row = (
  index: number,
  dueDate: string,
  timing: ReminderReadModel["timing"],
): ReminderReadModel => ({
  claimantName: "Synthetic claimant",
  responsible: null,
  reminder: {
    reminderId: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    caseId: "00000000-0000-4000-8000-000000000099",
    revision: 1,
    action: `Action ${index}`,
    dueDate,
    state: "PENDING",
  },
  caseReference: "TIMING-CASE",
  recordedAt: basis.observedAt,
  recordedBy: actorRefInput({ actorId: "00000000-0000-4000-8000-000000000098" }),
  acts: [],
  active: true,
  timing,
});
const rows = [
  row(1, "2026-11-01", "OVERDUE"),
  row(2, "2026-11-03", "TODAY"),
  row(3, "2026-11-10", "UPCOMING"),
];
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  localStorage.removeItem(preferenceKey);
});

for (const [language, labels, pending] of [
  ["en", ["Overdue", "Today", "Upcoming"], "Pending"],
  ["lv", ["Termiņš nokavēts", "Šodien", "Gaidāms"], "Gaidošs"],
  ["ar", ["متأخر", "اليوم", "قادم"], "معلق"],
] as const) {
  for (const scoped of [false, true]) {
    it(`renders server timing and independent reminder state in ${language}, case scope ${scoped}`, async () => {
      vi.useFakeTimers({ toFake: ["Date"] });
      vi.setSystemTime(new Date("2031-06-15T12:00:00Z"));
      localStorage.setItem(
        preferenceKey,
        JSON.stringify({
          version: 3,
          language,
          displayLocale: "en-GB",
          mode: "system",
          accent: "green" as const,
        }),
      );
      vi.stubGlobal(
        "fetch",
        vi
          .fn()
          .mockResolvedValue(
            response("reminder.list", "SUCCEEDED", { basis, items: rows, nextCursor: null }),
          ),
      );
      const view = render(
        <ReminderList
          filter={{ state: "PENDING" }}
          {...(scoped
            ? { target: { kind: "REFERENCE" as const, caseReference: "TIMING-CASE" } }
            : {})}
        />,
      );
      await screen.findByText("Action 1");
      const items = view.container.querySelectorAll(".reminder-list .table-view tbody > tr");
      expect(items).toHaveLength(3);
      for (const [index, item] of [...items].entries()) {
        expect(
          within(item as HTMLElement).getByText(labels[index]!, { exact: true }),
        ).toBeVisible();
        expect(item).toHaveTextContent(pending);
      }
      expect(view.container.querySelector("[data-region=DETAILS]")).toHaveTextContent("03/11/2026");
      expect(view.container.querySelector("[data-region=DETAILS]")).toHaveTextContent("Etc/UTC");
      expect(view.container.querySelectorAll(".reminder-list a")).toHaveLength(scoped ? 0 : 3);
    });
  }
}

it("keeps null timing for terminal and inactive parent observations without guessing from the due date", async () => {
  const items = [
    {
      ...row(1, "2026-11-01", null),
      reminder: { ...row(1, "2026-11-01", null).reminder, state: "COMPLETED" },
    },
    {
      ...row(2, "2026-11-01", null),
      reminder: { ...row(2, "2026-11-01", null).reminder, state: "CANCELLED" },
    },
    { ...row(3, "2026-11-01", null), active: false },
    { ...row(4, "2026-11-10", null), active: false },
  ];
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(
        response("reminder.list", "SUCCEEDED", { basis, items, nextCursor: null }),
      ),
  );
  const view = render(<ReminderList filter={{ includeInactive: true }} />);
  await screen.findByText("Action 1");
  expect(view.container.querySelectorAll(".reminder-list .table-view tbody > tr")).toHaveLength(4);
  expect(view.container.querySelectorAll(".reminder-timing")).toHaveLength(0);
  expect(screen.getByText("Completed", { exact: false })).toBeVisible();
  expect(screen.getByText("Cancelled", { exact: false })).toBeVisible();
  const rendered = view.container.querySelectorAll(".reminder-list .table-view tbody > tr");
  expect(rendered[2]).toHaveTextContent("Inactive work");
  expect(rendered[3]).toHaveTextContent("Inactive work");
});

it("retains each loaded row's server timing and date basis when a later page uses another basis", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValueOnce(
        response("reminder.list", "SUCCEEDED", {
          basis,
          items: [rows[1]],
          nextCursor: "next-page",
        }),
      )
      .mockResolvedValueOnce(
        response("reminder.list", "SUCCEEDED", {
          basis: {
            observedAt: "2026-11-03T22:01:00.000000Z",
            businessDate: "2026-11-04",
            timeZone: "Europe/Riga",
          },
          items: [row(4, "2026-11-03", "OVERDUE")],
          nextCursor: null,
        }),
      ),
  );
  const view = render(<ReminderList filter={{ state: "PENDING" }} />);
  await screen.findByText("Action 2");
  await userEvent.click(screen.getByRole("button", { name: "More reminders" }));
  await screen.findByText("Action 4");
  const items = view.container.querySelectorAll(".reminder-list .table-view tbody > tr");
  expect(items).toHaveLength(2);
  expect(items[0]).toHaveTextContent("Today");
  expect(items[1]).toHaveTextContent("Overdue");
  const observations = view.container.querySelectorAll("[data-region=DETAILS]");
  expect(observations[0]).toHaveTextContent("03/11/2026");
  expect(observations[0]).toHaveTextContent("Etc/UTC");
  expect(observations[0]).not.toHaveTextContent("Europe/Riga");
  expect(observations[1]).toHaveTextContent("04/11/2026");
  expect(observations[1]).toHaveTextContent("Europe/Riga");
});

const actionRow = (acts: ReminderReadModel["acts"]): ReminderReadModel => ({
  ...row(7, "2026-11-01", "OVERDUE"),
  acts,
});
const renderActions = (
  item: ReminderReadModel,
  onAct?: (row: ReminderReadModel, kind: string, originId?: string) => void,
) => {
  const fetch = vi
    .fn()
    .mockResolvedValue(
      response("reminder.list", "SUCCEEDED", { basis, items: [item], nextCursor: null }),
    );
  vi.stubGlobal("fetch", fetch);
  return {
    ...render(<ReminderList filter={{}} {...(onAct === undefined ? {} : { onAct })} />),
    fetch,
  };
};

it("delegates the explicit completion circle exactly once without previewing or writing in the row", async () => {
  const item = actionRow(["COMPLETE", "UPDATE", "CANCEL", "COMPLETE_AND_PLAN_NEXT"]);
  const onAct = vi.fn();
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  const { fetch } = renderActions(item, onAct);
  const circle = await screen.findByRole("button", { name: /^Complete: Complete reminder/u });
  expect(circle).toHaveAttribute("id", `reminder-${item.reminder.reminderId}-COMPLETE`);
  expect(circle).toHaveAccessibleName("Complete: Complete reminder");
  expect(screen.queryByRole("checkbox")).toBeNull();
  expect(screen.queryByRole("menu")).toBeNull();
  expect(onAct).not.toHaveBeenCalled();
  await userEvent.setup().click(circle);
  expect(onAct).toHaveBeenCalledExactlyOnceWith(
    item,
    "COMPLETE",
    `reminder-${item.reminder.reminderId}-COMPLETE`,
  );
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
  expect(fetch).toHaveBeenCalledOnce();
});

it("keeps only supplied non-completion actions in the menu and forwards the same current row", async () => {
  const item = actionRow(["COMPLETE", "CANCEL", "UPDATE"]);
  const onAct = vi.fn((_row: ReminderReadModel, _kind: string, originId?: string) => originId);
  renderActions(item, onAct);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: /^Complete: Complete reminder/u }));
  const overflow = screen.getByRole("button", { name: /^Available actions/u });
  await user.click(overflow);
  const menu = screen.getByRole("menu", { name: /^Available actions/u });
  expect(
    within(menu)
      .getAllByRole("menuitem")
      .map((entry) => entry.textContent),
  ).toEqual(["Cancel reminder"]);
  expect(
    within(menu).queryByRole("menuitem", { name: /^Complete: Complete reminder/u }),
  ).toBeNull();
  expect(within(menu).queryByRole("menuitem", { name: "Reopen reminder" })).toBeNull();
  await user.keyboard("{Escape}");
  const update = screen.getByRole("button", { name: "Reschedule: Update reminder" });
  await user.click(update);
  expect(onAct).toHaveBeenCalledTimes(2);
  expect(onAct.mock.calls[1]![0]).toBe(onAct.mock.calls[0]![0]);
  expect(onAct.mock.calls[1]![1]).toBe("UPDATE");
  expect(onAct.mock.results[1]!.value).toBe(update.id);
  expect(screen.queryByRole("menu")).toBeNull();
});

it("supports keyboard menu dismissal and focus restoration without selecting an action", async () => {
  const onAct = vi.fn();
  renderActions(actionRow(["UPDATE", "CANCEL"]), onAct);
  const user = userEvent.setup();
  const trigger = await screen.findByRole("button", { name: /^Available actions/u });
  trigger.focus();
  await user.keyboard("{ArrowDown}");
  expect(await screen.findByRole("menu", { name: /^Available actions/u })).toBeVisible();
  await user.keyboard("{Escape}");
  expect(screen.queryByRole("menu")).toBeNull();
  await waitFor(() => {
    expect(trigger).toHaveFocus();
  });
  expect(onAct).not.toHaveBeenCalled();
});

it.each([
  { active: true, state: "PENDING" as const, acts: [] },
  { active: false, state: "PENDING" as const, acts: [] },
  { active: true, state: "COMPLETED" as const, acts: ["REOPEN"] as const },
  { active: true, state: "CANCELLED" as const, acts: ["REOPEN"] as const },
])(
  "never invents completion from state or date: $state active=$active",
  async ({ active, state, acts }) => {
    const onAct = vi.fn();
    const item = {
      ...actionRow(acts),
      active,
      timing: null,
      reminder: { ...actionRow(acts).reminder, state },
    };
    const { container } = renderActions(item, onAct);
    await screen.findByText("Action 7");
    expect(screen.queryByRole("button", { name: /^Complete: Complete reminder/u })).toBeNull();
    expect(container.querySelector(".reminder-timing")).toBeNull();
    if (acts.length > 0) {
      await userEvent.setup().click(screen.getByRole("button", { name: /^Available actions/u }));
      expect(screen.getAllByRole("menuitem")).toHaveLength(1);
      expect(screen.getByRole("menuitem", { name: "Reopen reminder" })).toBeVisible();
    } else {
      expect(screen.queryByRole("button", { name: /^Available actions/u })).toBeNull();
    }
    expect(onAct).not.toHaveBeenCalled();
  },
);

it("renders no action controls without the parent coordinator even when the row supplies acts", async () => {
  renderActions(actionRow(["COMPLETE", "CANCEL"]));
  await screen.findByText("Action 7");
  expect(screen.queryByRole("button", { name: /^Complete: Complete reminder/u })).toBeNull();
  expect(screen.queryByRole("button", { name: /^Available actions/u })).toBeNull();
});
