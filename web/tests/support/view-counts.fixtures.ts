import type { CaseViews, ReminderViews } from "@contracts/client";
import { workBasis } from "./inputs";
export const caseViewsFixture: CaseViews = {
  basis: workBasis,
  items: [
    { view: "NEEDS_ATTENTION", count: 0 },
    { view: "MINE", count: 0 },
    { view: "OVERDUE", count: 0 },
    { view: "DUE_TODAY", count: 0 },
    { view: "NO_NEXT_ACTION", count: 0 },
    { view: "UNASSIGNED", count: 0 },
    { view: "UNDECIDED", count: 0 },
    { view: "DECIDED_UNPAID", count: 0 },
    { view: "CLOSED", count: 0 },
    { view: "VOIDED", count: 0 },
    { view: "ALL", count: 0 },
  ],
};
export const reminderViewsFixture: ReminderViews = {
  basis: workBasis,
  items: [
    { view: "PENDING", count: 0 },
    { view: "MINE", count: 0 },
    { view: "OVERDUE", count: 0 },
    { view: "TODAY", count: 0 },
    { view: "UPCOMING", count: 0 },
    { view: "COMPLETED", count: 0 },
    { view: "CANCELLED", count: 0 },
  ],
};
export const testWorkspaceViews = {
  cases: { data: caseViewsFixture, loading: false, message: null },
  reminders: { data: reminderViewsFixture, loading: false, message: null },
};
