import { within, waitFor } from "@testing-library/react";
import { service } from "../../../../src/api/service";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { humanReadHttpReply as response } from "../../../support/human-read-reply.fixtures";
import type {
  ReminderActHandler,
  ReminderControl,
} from "../../../../src/views/work/ReminderActions";
import { ReminderList } from "../../../../src/views/work/ReminderList";
import { preferenceKey } from "../../../../src/presentation/preferences";
import type {
  HumanReadDocument_reminderList,
  RegionRow,
  PresentedValue,
  WorkDateBasis,
  Region,
} from "@contracts/client";

const basis = {
  observedAt: "2026-11-03T08:00:00.000000Z",
  businessDate: "2026-11-03",
  timeZone: "Etc/UTC",
};
type AuthoredReminder = { control: ReminderControl; row: RegionRow };
const row = (
  index: number,
  timing: PresentedValue,
  state: "PENDING" | "COMPLETED" | "CANCELLED" = "PENDING",
): AuthoredReminder => ({
  control: {
    reminderId: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    caseId: "00000000-0000-4000-8000-000000000099",
    revision: 1,
    tasks: [],
    actionSeed: null,
    dueDateSeed: null,
  },
  row: {
    key: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    values: [
      { kind: "REFERENCE", value: "TIMING-CASE" },
      { kind: "TEXT", value: `Action ${index}` },
      { kind: "DATE", value: "2026-11-01" },
      { kind: "CODE", vocabulary: "reminderState", token: state },
      timing,
    ],
  },
});
const rows = [
  row(1, { kind: "CODE", vocabulary: "reminderDue", token: "OVERDUE" }),
  row(2, { kind: "CODE", vocabulary: "reminderDue", token: "TODAY" }),
  row(3, { kind: "CODE", vocabulary: "reminderDue", token: "UPCOMING" }),
];
const observation = (date: string, zone: string): Region => ({
  role: "DETAILS",
  content: {
    kind: "ENTRIES",
    entries: [
      {
        key: "date",
        labelId: "label.businessDate",
        prominence: "DETAIL",
        value: { kind: "DATE", value: date },
      },
      {
        key: "zone",
        labelId: "label.timeZone",
        prominence: "DETAIL",
        value: { kind: "TEXT", value: zone },
      },
    ],
  },
});
const documentFor = (
  items: ReadonlyArray<AuthoredReminder>,
  nextCursor: string | null = null,
  observed: WorkDateBasis = basis,
  details: Region = observation("2026-11-03", "Etc/UTC"),
): HumanReadDocument_reminderList => ({
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "caseReference",
              labelId: "field.caseReference.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
            {
              key: "action",
              labelId: "field.reminderAction.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
            {
              key: "due",
              labelId: "field.reminderDueDate.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
            {
              key: "state",
              labelId: "input.reminderState.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
            {
              key: "timing",
              labelId: "input.reminderDue.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: items.map((item) => item.row),
        },
      },
      details,
    ],
  },
  context: { kind: "REMINDERS", reminders: items.map((item) => item.control), nextCursor },
  basis: { kind: "SNAPSHOT", observation: observed },
  disclosure: "CURRENT",
});
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
        vi.fn().mockResolvedValue(response("reminder.list", documentFor(rows))),
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
    row(1, { kind: "ABSENT", reasonId: "label.none" }, "COMPLETED"),
    row(2, { kind: "ABSENT", reasonId: "label.none" }, "CANCELLED"),
    row(3, { kind: "CODE", vocabulary: "due", token: "INACTIVE" }),
    row(4, { kind: "CODE", vocabulary: "due", token: "INACTIVE" }),
  ];
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response("reminder.list", documentFor(items))));
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
      .mockResolvedValueOnce(response("reminder.list", documentFor([rows[1]!], "next-page")))
      .mockResolvedValueOnce(
        response(
          "reminder.list",
          documentFor(
            [row(4, { kind: "CODE", vocabulary: "reminderDue", token: "OVERDUE" })],
            null,
            {
              observedAt: "2026-11-03T22:01:00.000000Z",
              businessDate: "2026-11-04",
              timeZone: "Europe/Riga",
            },
            observation("2026-11-04", "Europe/Riga"),
          ),
        ),
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

const actionRow = (
  tasks: ReminderControl["tasks"],
  seeds: Pick<ReminderControl, "actionSeed" | "dueDateSeed"> = {
    actionSeed: null,
    dueDateSeed: null,
  },
): AuthoredReminder => ({
  ...row(7, { kind: "CODE", vocabulary: "reminderDue", token: "OVERDUE" }),
  control: {
    reminderId: "00000000-0000-4000-8000-000000000007",
    caseId: "00000000-0000-4000-8000-000000000099",
    revision: 1,
    tasks,
    ...seeds,
  },
});
const renderActions = (item: AuthoredReminder, onAct?: ReminderActHandler) => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValue(response("reminder.list", documentFor([item])));
  vi.stubGlobal("fetch", fetch);
  return {
    ...render(<ReminderList filter={{}} {...(onAct === undefined ? {} : { onAct })} />),
    fetch,
  };
};

it("delegates the explicit completion circle exactly once without previewing or writing in the row", async () => {
  const item = actionRow(
    [
      "REMINDER:REMINDER_COMPLETE",
      "REMINDER:REMINDER_UPDATE",
      "REMINDER:REMINDER_CANCEL",
      "REMINDER:REMINDER_COMPLETE_AND_PLAN_NEXT",
    ],
    { actionSeed: "Action 7", dueDateSeed: "2026-11-01" },
  );
  const onAct = vi.fn();
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  const { fetch } = renderActions(item, onAct);
  const circle = await screen.findByRole("button", { name: "Complete reminder" });
  expect(circle).toHaveAttribute(
    "id",
    `reminder-${item.control.reminderId}-REMINDER:REMINDER_COMPLETE`,
  );
  expect(circle).toHaveAccessibleName("Complete reminder");
  expect(screen.queryByRole("checkbox")).toBeNull();
  expect(screen.queryByRole("menu")).toBeNull();
  expect(onAct).not.toHaveBeenCalled();
  await userEvent.setup().click(circle);
  expect(onAct).toHaveBeenCalledExactlyOnceWith(
    {
      reminderId: item.control.reminderId,
      caseId: item.control.caseId,
      revision: 1,
      tasks: [
        "REMINDER:REMINDER_COMPLETE",
        "REMINDER:REMINDER_UPDATE",
        "REMINDER:REMINDER_CANCEL",
        "REMINDER:REMINDER_COMPLETE_AND_PLAN_NEXT",
      ],
      actionSeed: "Action 7",
      dueDateSeed: "2026-11-01",
    },
    "REMINDER:REMINDER_COMPLETE",
    `reminder-${item.control.reminderId}-REMINDER:REMINDER_COMPLETE`,
  );
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
  expect(fetch).toHaveBeenCalledOnce();
});

it("keeps only supplied non-completion actions in the menu and forwards the same current row", async () => {
  const item = actionRow([
    "REMINDER:REMINDER_COMPLETE",
    "REMINDER:REMINDER_CANCEL",
    "REMINDER:REMINDER_UPDATE",
  ]);
  const onAct = vi.fn((_row: ReminderControl, _kind: string, originId?: string) => originId);
  renderActions(item, onAct);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Complete reminder" }));
  const overflow = screen.getByRole("button", { name: /^Available actions/u });
  await user.click(overflow);
  const menu = screen.getByRole("menu", { name: /^Available actions/u });
  expect(
    within(menu)
      .getAllByRole("menuitem")
      .map((entry) => entry.textContent),
  ).toEqual(["Cancel reminder"]);
  expect(within(menu).queryByRole("menuitem", { name: "Complete reminder" })).toBeNull();
  expect(within(menu).queryByRole("menuitem", { name: "Reopen reminder" })).toBeNull();
  await user.keyboard("{Escape}");
  const update = screen.getByRole("button", { name: "Update reminder" });
  await user.click(update);
  expect(onAct).toHaveBeenCalledTimes(2);
  expect(onAct.mock.calls[1]![0]).toBe(onAct.mock.calls[0]![0]);
  expect(onAct.mock.calls[1]![1]).toBe("REMINDER:REMINDER_UPDATE");
  expect(onAct.mock.results[1]!.value).toBe(update.id);
  expect(screen.queryByRole("menu")).toBeNull();
});

it("supports keyboard menu dismissal and focus restoration without selecting an action", async () => {
  const onAct = vi.fn();
  renderActions(actionRow(["REMINDER:REMINDER_UPDATE", "REMINDER:REMINDER_CANCEL"]), onAct);
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
  {
    caption: "pending",
    timing: { kind: "ABSENT", reasonId: "label.none" },
    state: "PENDING",
    tasks: [],
  },
  {
    caption: "inactive",
    timing: { kind: "CODE", vocabulary: "due", token: "INACTIVE" },
    state: "PENDING",
    tasks: [],
  },
  {
    caption: "completed",
    timing: { kind: "ABSENT", reasonId: "label.none" },
    state: "COMPLETED",
    tasks: ["REMINDER:REMINDER_REOPEN"],
  },
  {
    caption: "cancelled",
    timing: { kind: "ABSENT", reasonId: "label.none" },
    state: "CANCELLED",
    tasks: ["REMINDER:REMINDER_REOPEN"],
  },
] satisfies ReadonlyArray<{
  caption: string;
  timing: PresentedValue;
  state: "PENDING" | "COMPLETED" | "CANCELLED";
  tasks: ReminderControl["tasks"];
}>)(
  "never invents completion from a supplied $caption observation",
  async ({ timing, state, tasks }) => {
    const onAct = vi.fn();
    const item = { ...row(7, timing, state), control: actionRow(tasks).control };
    const { container } = renderActions(item, onAct);
    await screen.findByText("Action 7");
    expect(screen.queryByRole("button", { name: "Complete reminder" })).toBeNull();
    expect(container.querySelector(".reminder-timing")).toBeNull();
    if (tasks.length > 0) {
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
  renderActions(actionRow(["REMINDER:REMINDER_COMPLETE", "REMINDER:REMINDER_CANCEL"]));
  await screen.findByText("Action 7");
  expect(screen.queryByRole("button", { name: "Complete reminder" })).toBeNull();
  expect(screen.queryByRole("button", { name: /^Available actions/u })).toBeNull();
});
