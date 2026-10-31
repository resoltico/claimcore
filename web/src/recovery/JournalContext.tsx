import { createContext, useContext } from "react";
import type { RecoveryJournal } from "../protocol/recoveryJournal";
import { browserJournal } from "./indexedJournal";
export const JournalContext = createContext<RecoveryJournal>(browserJournal);
export const useRecoveryJournal = () => useContext(JournalContext);
