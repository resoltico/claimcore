import type { CaseView, RequestByEndpoint } from "@contracts/client";
type Act = Extract<RequestByEndpoint["operation.execute"], { kind: "CASE" }>["act"]["kind"];
export const caseAdvice = (acts: ReadonlyArray<Act>): Omit<CaseView["advice"], "history"> => ({
  acts,
  ...(acts.includes("CORRECT")
    ? {
        correction: {
          registration: ["KEEP", "REPLACE"],
          decision: ["KEEP", "REPLACE", "CLEAR"],
          payment: ["KEEP", "REPLACE", "CLEAR"],
        },
      }
    : {}),
});
