import type { CaseAdvice, CaseActRefusal, CaseView } from "@contracts/client";
import { isCaseAct } from "../../src/operation/metadata";
import { productMetadata } from "../../src/protocol/productMetadata";
/** Complete transport inputs; policy/UX reason tests provide independent literal diagnoses. */
export const blockedCaseActs = (
  acts: CaseAdvice["acts"],
  state: CaseView["headline"] = "UNDECIDED",
  paused = false,
): ReadonlyArray<CaseActRefusal> =>
  productMetadata.catalog.entries
    .filter((entry) => entry.endpoint === "operation.execute" && entry.subject === "CASE")
    .flatMap(({ kind: token }) => {
      if (!isCaseAct(token) || acts.includes(token)) {
        return [];
      }
      const reason: CaseActRefusal["reason"] = paused
        ? { code: "REGISTER_MAINTENANCE", action: "READ_CURRENT" }
        : { code: "NOT_OFFERED", state, action: "REVIEW_LATEST" };
      return [{ act: token, reason }];
    });
