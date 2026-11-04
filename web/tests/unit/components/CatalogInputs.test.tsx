import { caseViewInput } from "../../support/inputs";
import { fields } from "../../support/service-ui.fixtures";
import { caseAdvice } from "../../support/case-advice.fixtures";
import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CatalogInputs } from "../../../src/components/CatalogInputs";
import { operationEntry } from "../../../src/protocol/operationCatalog";
import { catalogs } from "../../../src/generated/core-catalogs";

it("uses the binding vocabulary to label an option at an unfamiliar catalog path", () => {
  const original = operationEntry({ subject: "AUTHORITY", kind: "GRANT" });
  const role = original.inputs.find(
    (input) => input.kind.kind === "CHOICE" && input.kind.vocabulary === "role",
  )!;
  const input = {
    ...role,
    identity: "unfamiliar.selection",
    path: ["unfamiliar", "selection"],
    branches: [],
  };
  const entry = { ...original, inputs: [input], constants: [] };
  render(
    <CatalogInputs
      entry={entry}
      values={{ "unfamiliar.selection": "CASE_READER" }}
      current={null}
      disabled={false}
      fieldErrors={{}}
      onChange={vi.fn()}
      onFieldsChange={vi.fn()}
    />,
  );
  expect(screen.getByRole("radio", { name: catalogs.en["role.CASE_READER.label"] })).toBeChecked();
});

it("keeps a draft's now-unavailable correction choice visible and selected without offering it", () => {
  const current = caseViewInput({
    caseReference: "CASE-1",
    revision: 3,
    state: {
      kind: "ACTIVE",
      fields: {
        ...fields,
        progress: "DECIDED",
        decision: {
          paymentDecisionDate: "2026-09-03",
          payableAmount: "10",
          payableCurrency: "EUR",
        },
        payment: null,
      },
    },
    advice: {
      ...caseAdvice(["CORRECT"]),
      history: [],
      correction: {
        registration: ["KEEP", "UPDATE"],
        decision: ["KEEP", "UPDATE", "CLEAR"],
        payment: ["KEEP", "REPLACE"],
      },
    },
  });
  const onChange = vi.fn();
  render(
    <CatalogInputs
      entry={operationEntry({ subject: "CASE", kind: "CORRECT" })}
      values={{
        "act.groups.registration.kind": "KEEP",
        "act.groups.decision.kind": "KEEP",
        "act.groups.payment.kind": "CLEAR",
      }}
      current={current}
      disabled={false}
      fieldErrors={{}}
      onChange={onChange}
      onFieldsChange={vi.fn()}
    />,
  );
  const selected = document.querySelector('#correction-payment-mode input[value="CLEAR"]');
  expect(selected).toBeChecked();
  expect(selected).toBeDisabled();
  expect(selected).toHaveAccessibleDescription(/Not available with the current facts/u);
  expect(onChange).not.toHaveBeenCalled();
});
it("keeps a selected reminder identity bound rather than exposing a technical retargeting field", () => {
  const current = caseViewInput({
    caseReference: "CASE-1",
    revision: 1,
    state: { kind: "ACTIVE", fields },
    advice: { ...caseAdvice([]), history: [] },
  });
  const onChange = vi.fn();
  render(
    <CatalogInputs
      entry={operationEntry({ subject: "REMINDER", kind: "REMINDER_UPDATE" })}
      values={{
        "act.reminderId": "30000000-0000-4000-8000-000000000003",
        "act.expectedRevision": "1",
        "act.action": "Retained action",
        "act.dueDate": "2026-11-05",
      }}
      current={current}
      disabled={false}
      fixedTarget
      fieldErrors={{}}
      onChange={onChange}
      onFieldsChange={vi.fn()}
    />,
  );
  expect(document.querySelector('[name="act.reminderId"]')).toBeNull();
  expect(screen.getByRole("textbox", { name: "Action" })).toHaveValue("Retained action");
  expect(onChange).not.toHaveBeenCalled();
});
