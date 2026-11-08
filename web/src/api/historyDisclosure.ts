import type { HistoryDisclosure } from "@contracts/client";

const levels: Readonly<Record<HistoryDisclosure, number>> = { ERASED: 0, SUMMARY: 1, FULL: 2 };
/** A current authorization/lifecycle observation bounds cached presentation, not reply business data. */
export const permitsHistory = (ceiling: HistoryDisclosure, observed: HistoryDisclosure): boolean =>
  levels[observed] <= levels[ceiling];
