import type { HumanReadDocument_workViews, PresentedValue } from "@contracts/client";
import type { CaseCounts, ReminderCounts } from "../../src/views/WorkspaceViews";
import { workBasis } from "./inputs";

type CaseViewFixture = {
  key: string;
  view: HumanReadDocument_workViews["context"]["cases"][number]["view"];
  label: PresentedValue;
  count: PresentedValue;
};
type ReminderViewFixture = Omit<CaseViewFixture, "view"> & {
  view: HumanReadDocument_workViews["context"]["reminders"][number]["view"];
};
const cases: ReadonlyArray<CaseViewFixture> = [
  {
    key: "attention",
    view: "NEEDS_ATTENTION",
    label: { kind: "MESSAGE", messageId: "view.NEEDS_ATTENTION.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "mine",
    view: "MINE",
    label: { kind: "MESSAGE", messageId: "view.MINE.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "overdue",
    view: "OVERDUE",
    label: { kind: "MESSAGE", messageId: "view.OVERDUE.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "today",
    view: "DUE_TODAY",
    label: { kind: "MESSAGE", messageId: "view.DUE_TODAY.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "no-next",
    view: "NO_NEXT_ACTION",
    label: { kind: "MESSAGE", messageId: "view.NO_NEXT_ACTION.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "unassigned",
    view: "UNASSIGNED",
    label: { kind: "MESSAGE", messageId: "view.UNASSIGNED.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "undecided",
    view: "UNDECIDED",
    label: { kind: "MESSAGE", messageId: "view.UNDECIDED.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "unpaid",
    view: "DECIDED_UNPAID",
    label: { kind: "MESSAGE", messageId: "view.DECIDED_UNPAID.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "closed",
    view: "CLOSED",
    label: { kind: "MESSAGE", messageId: "view.CLOSED.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "voided",
    view: "VOIDED",
    label: { kind: "MESSAGE", messageId: "view.VOIDED.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "all",
    view: "ALL",
    label: { kind: "MESSAGE", messageId: "view.ALL.label" },
    count: { kind: "COUNT", value: 0 },
  },
];
const reminders: ReadonlyArray<ReminderViewFixture> = [
  {
    key: "pending-reminders",
    view: "PENDING",
    label: { kind: "MESSAGE", messageId: "view.PENDING.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "my-reminders",
    view: "MINE",
    label: { kind: "MESSAGE", messageId: "view.MINE.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "overdue-reminders",
    view: "OVERDUE",
    label: { kind: "MESSAGE", messageId: "view.OVERDUE.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "today-reminders",
    view: "TODAY",
    label: { kind: "MESSAGE", messageId: "view.TODAY.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "upcoming-reminders",
    view: "UPCOMING",
    label: { kind: "MESSAGE", messageId: "view.UPCOMING.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "completed-reminders",
    view: "COMPLETED",
    label: { kind: "MESSAGE", messageId: "view.COMPLETED.label" },
    count: { kind: "COUNT", value: 0 },
  },
  {
    key: "cancelled-reminders",
    view: "CANCELLED",
    label: { kind: "MESSAGE", messageId: "view.CANCELLED.label" },
    count: { kind: "COUNT", value: 0 },
  },
];
const caseViews: CaseCounts = {
  basis: workBasis,
  navigation: { kind: "COUNT", value: 0 },
  items: cases.map(({ view, label, count }) => ({ view, label, count })),
};
const reminderViews: ReminderCounts = {
  basis: workBasis,
  navigation: { kind: "COUNT", value: 0 },
  items: reminders.map(({ view, label, count }) => ({ view, label, count })),
};
export const testWorkspaceViews = {
  cases: { data: caseViews, loading: false, message: null },
  reminders: { data: reminderViews, loading: false, message: null },
};

/** Packages authored presentation cells and independent navigation handles; no DATA projection. */
export const workViewsDocument = (
  caseItems: ReadonlyArray<CaseViewFixture> = cases,
  reminderItems: ReadonlyArray<ReminderViewFixture> = reminders,
): HumanReadDocument_workViews => ({
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            { key: "view", labelId: "label.views", prominence: "PRIMARY", isMoney: false },
            { key: "count", labelId: "label.total", prominence: "SECONDARY", isMoney: false },
          ],
          rows: [...caseItems, ...reminderItems].map(({ key, label, count }) => ({
            key,
            values: [label, count],
          })),
        },
      },
    ],
  },
  context: {
    kind: "WORK_VIEWS",
    cases: caseItems.map(({ key, view }) => ({ key, view })),
    reminders: reminderItems.map(({ key, view }) => ({ key, view })),
  },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
});
