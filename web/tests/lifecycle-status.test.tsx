import { caseOffers } from "./case-advice.fixtures";
import { it, expect } from "vitest";
import { render, screen } from "./presentation-test-support";
import { LifecycleStatus } from "../src/views/LifecycleStatus";
import { fields } from "./service-ui.fixtures";
const fullHistory = { acts: caseOffers([]), history: ["SUMMARY", "FULL"] as const };
it("renders active voided and erased states from the accepted case view", () => {
  const { rerender } = render(
    <LifecycleStatus
      value={{
        caseReference: fields.caseReference,
        revision: 1,
        advice: fullHistory,
        state: {
          kind: "ACTIVE",
          fields,
        },
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Active");
  rerender(
    <LifecycleStatus
      value={{
        caseReference: fields.caseReference,
        revision: 2,
        advice: fullHistory,
        state: {
          kind: "VOIDED",
          fields,
        },
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Voided");
  rerender(
    <LifecycleStatus
      value={{
        state: {
          kind: "ERASED",
        },
        caseReference: "CASE-1",
        revision: 3,
        advice: {
          acts: caseOffers([]),
          history: ["SUMMARY"],
        },
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Erased");
});
