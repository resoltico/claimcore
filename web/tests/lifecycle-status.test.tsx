import { caseOffers } from "./case-advice.fixtures";
import { it, expect } from "vitest";
import { render, screen } from "./presentation-test-support";
import { LifecycleStatus } from "../src/views/LifecycleStatus";
import { fields } from "./service-ui.fixtures";
it("renders active voided and erased states from the accepted case view", () => {
  const { rerender } = render(
    <LifecycleStatus
      value={{
        caseReference: fields.caseReference,
        revision: 1,
        advice: {
          acts: caseOffers([]),
          history: ["SUMMARY", "FULL"],
        },
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
        advice: {
          acts: caseOffers([]),
          history: ["SUMMARY", "FULL"],
        },
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
