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
        offers: [].map((identifier) => ({
          definition: identifier,
          caseReference: null,
          expectedRevision: null,
          choices: [],
        })),
        historyDetails: ["SUMMARY", "FULL"],
        state: { tag: "LIVE", fields, disposition: "ACTIVE", availableActs: [] },
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Active");
  rerender(
    <LifecycleStatus
      value={{
        caseReference: fields.caseReference,
        revision: 2,
        offers: [].map((identifier) => ({
          definition: identifier,
          caseReference: null,
          expectedRevision: null,
          choices: [],
        })),
        historyDetails: ["SUMMARY", "FULL"],
        state: { tag: "LIVE", fields, disposition: "VOIDED", availableActs: [] },
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Voided");
  rerender(
    <LifecycleStatus
      value={{
        state: { tag: "ERASED" },
        caseReference: "CASE-1",
        revision: 3,
        offers: [],
        historyDetails: ["SUMMARY"],
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Erased");
});
