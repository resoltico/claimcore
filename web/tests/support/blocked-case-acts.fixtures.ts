import type { CaseAdvice, CaseActRefusal, CaseView } from "@contracts/client";
import { isCaseAct } from "../../src/operation/metadata";
import { inventory } from "./inventory";
/** Complete transport inputs; policy/UX reason tests provide independent literal diagnoses. */
export const blockedCaseActs = (
  acts: CaseAdvice["acts"],
  state: CaseView["headline"] = "UNDECIDED",
  paused = false,
): ReadonlyArray<CaseActRefusal> =>
  inventory.caseActs.flatMap(({ token }) => {
    if (!isCaseAct(token) || acts.includes(token)) {
      return [];
    }
    const reason: CaseActRefusal["reason"] = paused
      ? { code: "REGISTER_MAINTENANCE", action: "READ_CURRENT" }
      : { code: "NOT_OFFERED", state, action: "REVIEW_LATEST" };
    return [{ act: token, reason }];
  });
