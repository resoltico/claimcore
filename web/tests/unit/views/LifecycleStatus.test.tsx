import { caseViewInput } from "../../support/inputs";
import { caseAdvice } from "../../support/case-advice.fixtures";
import { it, expect } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { LifecycleStatus } from "../../../src/views/LifecycleStatus";
import { fields } from "../../support/service-ui.fixtures";
const fullHistory = { ...caseAdvice([]), history: ["SUMMARY", "FULL"] as const };
it("renders stewardship meanings for voided and erased accepted states", () => {
  const { rerender } = render(
    <LifecycleStatus
      value={caseViewInput({
        caseReference: fields.caseReference,
        revision: 1,
        advice: fullHistory,
        state: {
          kind: "ACTIVE",
          fields,
        },
      })}
    />,
  );
  expect(screen.queryByRole("status")).toBeNull();
  rerender(
    <LifecycleStatus
      value={caseViewInput({
        caseReference: fields.caseReference,
        revision: 2,
        advice: fullHistory,
        state: {
          kind: "VOIDED",
          fields,
        },
      })}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Voided");
  rerender(
    <LifecycleStatus
      value={caseViewInput({
        state: {
          kind: "ERASED",
        },
        caseReference: "CASE-1",
        revision: 3,
        advice: {
          ...caseAdvice([]),
          history: ["SUMMARY"],
        },
      })}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Erased");
});
