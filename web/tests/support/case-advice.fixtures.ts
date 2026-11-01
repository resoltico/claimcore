import type { CaseView, RequestByEndpoint } from "@contracts/client";
type Act = Extract<RequestByEndpoint["operation.execute"], { kind: "CASE" }>["act"]["kind"];
export const caseAdvice = (acts: ReadonlyArray<Act>): Omit<CaseView["advice"], "history"> => ({
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
