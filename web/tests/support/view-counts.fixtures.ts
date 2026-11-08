import type { WorkViews } from "@contracts/client";
import type { CaseCounts, ReminderCounts } from "../../src/views/WorkspaceViews";
import { workBasis } from "./inputs";
const caseViewsFixture: CaseCounts = {
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
const reminderViewsFixture: ReminderCounts = {
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

export const workViewsFixture: WorkViews = {
  basis: workBasis,
  caseViews: caseViewsFixture.items,
  reminderViews: reminderViewsFixture.items,
};
