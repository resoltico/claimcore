import type { CaseAdvice, CaseActRefusal, CaseView } from "@contracts/client";
const caseActs: CaseAdvice["acts"] = [
  "OPEN",
  "AMEND_REGISTRATION",
  "CORRECT",
  "DECIDE",
  "WITHDRAW_DECISION",
  "RECORD_PAYMENT",
  "REMOVE_PAYMENT",
  "CLOSE",
  "REOPEN",
  "VOID",
  "REINSTATE",
  "ERASE",
];
/** Complete transport inputs; policy/UX reason tests provide independent literal diagnoses. */
export const blockedCaseActs = (
  acts: CaseAdvice["acts"],
  state: CaseView["headline"] = "UNDECIDED",
  paused = false,
): ReadonlyArray<CaseActRefusal> =>
  caseActs.flatMap((act) => {
    if (acts.includes(act)) return [];
    const reason: CaseActRefusal["reason"] = paused
      ? { code: "REGISTER_MAINTENANCE", action: "READ_CURRENT" }
      : { code: "NOT_OFFERED", state, action: "REVIEW_LATEST" };
    return [{ act, reason }];
  });
