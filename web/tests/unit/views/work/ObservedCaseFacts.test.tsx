import userEvent from "@testing-library/user-event";
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
it("renders the review's observed financial values and fact revision independently of work revisions", async () => {
  const view = render(<ObservedCaseFacts parent={parent} />);
  expect(view.container).toHaveTextContent("Others can update them while you review.");
  await userEvent.setup().click(screen.getByText("Observed case facts at review"));
  expect(screen.getByRole("heading", { name: "Financial details" })).toBeVisible();
  expect(screen.getByText("Version 99")).toBeVisible();
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
  expect(view.container).toHaveTextContent("Others can update them while you review.");
});
it("an erased parent never renders a prior financial payload", () => {
  const view = render(<ObservedCaseFacts parent={{ ...parent, state: { kind: "ERASED" } }} />);
  expect(view.container.querySelector(".case-fields")).toBeNull();
  expect(view.container).not.toHaveTextContent("500.00");
});
