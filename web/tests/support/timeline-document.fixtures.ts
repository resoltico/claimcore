import type { HumanReadDocument_caseTimeline, RegionTimelineEntry } from "@contracts/client";
import { workBasis } from "./inputs";

export const openedTimelineEntry: RegionTimelineEntry = {
  key: "1/0",
  when: "2026-11-02T12:00:00.000000Z",
  who: {
    actorId: "10000000-0000-4000-8000-000000000001",
    displayName: { kind: "NAMED", value: "Timeline recorder" },
    principalKind: "HUMAN",
    availability: "AVAILABLE",
  },
  summary: { messageId: "timeline.event.caseOpened", arguments: [] },
  highlights: [],
  details: [],
};

/** Packages already-authored presentation entries, without projecting saved event facts. */
export const timelineDocument = (
  entries: ReadonlyArray<RegionTimelineEntry> = [],
  nextCursor: string | null = null,
  disclosure: HumanReadDocument_caseTimeline["disclosure"] = "FULL",
): HumanReadDocument_caseTimeline => ({
  screen: { regions: [{ role: "ACTIVITY", content: { kind: "TIMELINE", entries } }] },
  context: { kind: "PAGE", tasks: [], nextCursor },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure,
});
