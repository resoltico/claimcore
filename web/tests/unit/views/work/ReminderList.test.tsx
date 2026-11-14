import { operationAffordance } from "../../../support/affordances.fixtures";
import { within } from "@testing-library/react";
import { service } from "../../../../src/api/service";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { humanReadHttpReply as response } from "../../../support/human-read-reply.fixtures";

import { ReminderList } from "../../../../src/views/work/ReminderList";
import { preferenceKey } from "../../../../src/presentation/preferences";
import type {
  HumanReadDocument_reminder_list,
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
type AuthoredReminder = { row: RegionRow };
const row = (
  index: number,
  timing: PresentedValue,
  state: "PENDING" | "COMPLETED" | "CANCELLED" = "PENDING",
): AuthoredReminder => ({
  row: {
    key: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    link: {
      endpoint: "case.get",
      input: { target: { kind: "ID", caseId: "00000000-0000-4000-8000-000000000099" } },
    },
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
): HumanReadDocument_reminder_list => ({
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "reference",
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
  subject: { kind: "LIST" },
  actions: [],
  links: [],
  history: null,
  nextCursor,
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
        if (!scoped) {
          expect(
            within(item as HTMLElement).getByRole("link", { name: "TIMING-CASE" }),
          ).toHaveAttribute("href", "/cases/id/00000000-0000-4000-8000-000000000099");
        }
      }
      expect(view.container.querySelector("[data-region=DETAILS]")).toHaveTextContent("03/11/2026");
      expect(view.container.querySelector("[data-region=DETAILS]")).toHaveTextContent("Etc/UTC");
      expect(view.container.querySelectorAll(".reminder-list a")).toHaveLength(3);
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

const actionRow = (): AuthoredReminder => {
  const item = row(7, { kind: "CODE", vocabulary: "reminderDue", token: "OVERDUE" });
  return {
    row: {
      ...item.row,
      actions: [
        operationAffordance("reminder.complete", {
          kind: "REMINDER",
          target: { kind: "ID", caseId: "00000000-0000-4000-8000-000000000099" },
          reminderId: "00000000-0000-4000-8000-000000000007",
          expectedRevision: 1,
        }),
      ],
    },
  };
};
it("delegates the supplied completion control without previewing or writing in the row", async () => {
  const item = actionRow();
  const onAct = vi.fn();
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  vi.stubGlobal(
    "fetch",
    vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(response("reminder.list", documentFor([item]))),
  );
  render(<ReminderList filter={{}} onAct={onAct} />);
  await userEvent.click(await screen.findByRole("button", { name: "Complete reminder" }));
  expect(onAct).toHaveBeenCalledExactlyOnceWith(item.row.actions![0], expect.any(String));
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
it("renders no actionable control without a coordinator", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(response("reminder.list", documentFor([actionRow()]))),
  );
  render(<ReminderList filter={{}} />);
  await screen.findByText("Action 7");
  expect(screen.queryByRole("button", { name: "Complete reminder" })).toBeNull();
});
