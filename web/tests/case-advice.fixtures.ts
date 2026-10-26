import type { CaseView, RequestByEndpoint } from "@contracts/client";
type Act = Extract<RequestByEndpoint["operation.execute"], { kind: "CASE" }>["act"]["kind"];
export const caseOffers = (acts: ReadonlyArray<Act>): CaseView["advice"]["acts"] =>
  acts.map((act) =>
    act === "CORRECT"
      ? {
          kind: act,
          expectedRevision: 1,
          correction: {
            registration: ["KEEP", "REPLACE"],
            decision: ["KEEP", "REPLACE", "CLEAR"],
            payment: ["KEEP", "REPLACE", "CLEAR"],
          },
        }
      : { kind: "ACT", act, expectedRevision: 1 },
  );
