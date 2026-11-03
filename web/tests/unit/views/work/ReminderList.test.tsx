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
  recordedBy: "00000000-0000-4000-8000-000000000098",
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
  ["lv", ["Nokavēts", "Šodien", "Gaidāms"], "Gaidošs"],
  ["ar", ["متأخر", "اليوم", "قادم"], "معلق"],
] as const) {
  for (const scoped of [false, true]) {
    it(`renders server timing and independent reminder state in ${language}, case scope ${scoped}`, async () => {
      vi.useFakeTimers({ toFake: ["Date"] });
      vi.setSystemTime(new Date("2031-06-15T12:00:00Z"));
      localStorage.setItem(
        preferenceKey,
        JSON.stringify({ version: 2, language, displayLocale: "en-GB", theme: "SYSTEM" }),
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
      const items = view.container.querySelectorAll(".reminder-list li");
      expect(items).toHaveLength(3);
      for (const [index, item] of [...items].entries()) {
        expect(item.querySelector(".reminder-timing")).toHaveTextContent(labels[index]!);
        expect(item).toHaveTextContent(pending);
        expect(item.querySelector(".observation-basis")).toHaveTextContent("03/11/2026");
        expect(item.querySelector(".observation-basis")).toHaveTextContent("Etc/UTC");
      }
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
  expect(view.container.querySelectorAll(".reminder-list li")).toHaveLength(4);
  expect(view.container.querySelectorAll(".reminder-timing")).toHaveLength(0);
  expect(screen.getByText("Completed", { exact: false })).toBeVisible();
  expect(screen.getByText("Cancelled", { exact: false })).toBeVisible();
  const rendered = view.container.querySelectorAll(".reminder-list li");
  expect(rendered[2]).toHaveTextContent(
    "Work is inactive while the case is closed or voided. Pending dates are retained.",
  );
  expect(rendered[3]).toHaveTextContent(
    "Work is inactive while the case is closed or voided. Pending dates are retained.",
  );
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
  const items = view.container.querySelectorAll(".reminder-list li");
  expect(items).toHaveLength(2);
  expect(items[0]).toHaveTextContent("Today");
  expect(items[0]).toHaveTextContent("03/11/2026");
  expect(items[0]).toHaveTextContent("Etc/UTC");
  expect(items[0]).not.toHaveTextContent("Europe/Riga");
  expect(items[1]).toHaveTextContent("Overdue");
  expect(items[1]).toHaveTextContent("04/11/2026");
  expect(items[1]).toHaveTextContent("Europe/Riga");
});
