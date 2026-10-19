import type { CaseView, RequestByEndpoint } from "@contracts/client";
type Act = RequestByEndpoint["case.execute"]["act"]["kind"];
export const caseOffers = (acts: ReadonlyArray<Act>): CaseView["advice"]["acts"] =>
  acts.map((act) =>
    act === "CORRECT_CASE"
      ? {
          act,
          expectedRevision: 1,
          correction: {
            registration: ["KEEP", "REPLACE"],
            decision: ["KEEP", "REPLACE", "CLEAR"],
            payment: ["KEEP", "REPLACE", "CLEAR"],
          },
        }
      : { act, expectedRevision: 1 },
  );
