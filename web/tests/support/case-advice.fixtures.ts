import { initialMaintenance } from "./inputs";
import type { CaseView, RequestByEndpoint } from "@contracts/client";
type Act = Extract<RequestByEndpoint["operation.execute"], { kind: "CASE" }>["act"]["kind"];
export const caseAdvice = (
  acts: ReadonlyArray<Act>,
  recommended: CaseView["advice"]["recommended"] = null,
): Omit<CaseView["advice"], "history"> => ({
  maintenance: initialMaintenance,
  recommended,
  acts,
  ...(acts.includes("CORRECT")
    ? {
        correction: {
          registration: ["KEEP", "UPDATE"],
          decision: ["KEEP", "UPDATE", "CLEAR"],
          payment: ["KEEP", "REPLACE", "REAFFIRM", "CLEAR"],
        },
      }
    : {}),
});
