import type { CaseTimelineEntry, CaseTimelinePage } from "@contracts/client";
import { actorRefInput, workBasis } from "./inputs";
export const timelineEntry = (
  value: Partial<Extract<CaseTimelineEntry, { kind: "CASE_FACT" }>> = {},
): CaseTimelineEntry => ({
  kind: "CASE_FACT",
  operationId: "10000000-0000-7000-8000-000000000001",
  position: { sequence: 1, entryHash: "a".repeat(64) },
  recordedAt: "2026-11-02T12:00:00.000000Z",
  recordedBy: actorRefInput({ displayName: { kind: "NAMED", value: "Timeline recorder" } }),
  revision: 1,
  act: "OPEN",
  changes: {
    kind: "DISCLOSED",
    items: [],
    overview: {
      event: { kind: "CASE_OPENED", messageId: "timeline.event.caseOpened" },
      highlights: [],
    },
  },
  ...value,
});
export const timelinePage = (
  items: ReadonlyArray<CaseTimelineEntry> = [],
  nextCursor: string | null = null,
): CaseTimelinePage => ({
  basis: workBasis,
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: "CASE-1",
  items,
  nextCursor,
});
