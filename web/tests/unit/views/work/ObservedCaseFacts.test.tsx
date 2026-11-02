import { expect, it } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { fields } from "../../../support/service-ui.fixtures";
import { ObservedCaseFacts } from "../../../../src/views/work/ObservedCaseFacts";
import type { CaseSnapshot } from "@contracts/client";
const parent: CaseSnapshot = {
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: "PRESERVED-1",
  revision: 99,
  state: {
    kind: "ACTIVE",
    fields: {
      ...fields,
      caseReference: "PRESERVED-1",
      status: "CLOSED",
      progress: "PAID",
      registration: {
        ...fields.registration,
        claimedAmount: "500",
        incidentDescription: "NARRATIVE_NOT_FINANCIAL_CONTEXT",
      },
      decision: { paymentDecisionDate: "2026-11-01", payableAmount: "200", payableCurrency: "EUR" },
      payment: { paymentDate: "2026-11-02" },
    },
  },
};
it("renders the review's observed financial values and fact revision independently of work revisions", () => {
  const view = render(<ObservedCaseFacts parent={parent} />);
  expect(
    screen.getByRole("heading", { name: "Financial facts observed for this review" }),
  ).toBeVisible();
  expect(screen.getByText("Revision 99")).toBeVisible();
  expect(view.container.querySelector('[data-field-name="claimedAmount"]')).toHaveTextContent(
    "500.00",
  );
  expect(view.container.querySelector('[data-field-name="payableAmount"]')).toHaveTextContent(
    "200.00",
  );
  expect(view.container.querySelector('[data-field-name="paymentDate"]')).toHaveTextContent(
    "02/11/2026",
  );
  expect(view.container).not.toHaveTextContent("NARRATIVE_NOT_FINANCIAL_CONTEXT");
  expect(view.container).toHaveTextContent(
    "other users can change them independently before recording",
  );
});
it("an erased parent never renders a prior financial payload", () => {
  const view = render(<ObservedCaseFacts parent={{ ...parent, state: { kind: "ERASED" } }} />);
  expect(view.container.querySelector(".case-fields")).toBeNull();
  expect(view.container).not.toHaveTextContent("500.00");
});
