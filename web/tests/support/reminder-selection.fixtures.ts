import type { RegionRow, Reminder } from "@contracts/client";
import { humanReadReply } from "./human-read-reply.fixtures";
import { workBasis } from "./inputs";
const row = (key: string, caption: string, state: Reminder["state"]): RegionRow => ({
  key,
  values: [
    { kind: "TEXT", value: caption },
    { kind: "CODE", vocabulary: "reminderState", token: state },
  ],
});
// Authored response facts; tests choose each reply explicitly rather than implementing Core filtering.
export const pendingOpen = row("pending-open", "Pending open reminder", "PENDING");
export const completedOpen = row("completed-open", "Completed open reminder", "COMPLETED");
export const cancelledOpen = row("cancelled-open", "Cancelled open reminder", "CANCELLED");
export const completedLaterOpen = row(
  "completed-later-open",
  "Another completed open reminder",
  "COMPLETED",
);
export const completedClosed = row("completed-closed", "Completed closed reminder", "COMPLETED");
export const selectionPage = (
  rows: ReadonlyArray<RegionRow>,
  nextCursor: string | null = null,
) => ({
  kind: "outcome" as const,
  status: 200,
  value: humanReadReply("reminder.list", {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "action",
                labelId: "field.reminderAction.label",
                prominence: "PRIMARY",
                joins: null,
                isMoney: false,
              },
              {
                key: "state",
                labelId: "input.reminderState.label",
                prominence: "SECONDARY",
                joins: null,
                isMoney: false,
              },
            ],
            rows,
          },
        },
      ],
    },
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor,
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure: "CURRENT",
  }),
});
