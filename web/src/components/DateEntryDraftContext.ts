import { createContext } from "react";
import type { DateEntryDrafts, DateEntryPart } from "../operation/dateEntryDraft";
export const DateEntryDraftContext = createContext<{
  entries: DateEntryDrafts;
  capture: (path: string, canonical: string, parts: ReadonlyArray<DateEntryPart> | null) => void;
} | null>(null);
