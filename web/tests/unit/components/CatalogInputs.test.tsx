import { useState } from "react";
import userEvent from "@testing-library/user-event";
import { within } from "@testing-library/react";
import { initialOperationValues } from "../../../src/operation/operationForm";
import { currentCaseInput } from "../../support/inputs";
import { fields } from "../../support/service-ui.fixtures";
import { caseAdvice } from "../../support/case-advice.fixtures";
import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CatalogInputs } from "../../../src/components/CatalogInputs";
import { operationEntry } from "../../../src/protocol/operationCatalog";
import { coreCatalogs as catalogs } from "../../support/authored-catalogs";

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
  const current = currentCaseInput({
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
      history: "NONE",
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
  const current = currentCaseInput({
    caseReference: "CASE-1",
    revision: 1,
    state: { kind: "ACTIVE", fields },
    advice: { ...caseAdvice([]), history: "NONE" },
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

it("edits admission rows independently with catalog scope and principal admission", async () => {
  const user = userEvent.setup();
  const entry = operationEntry({ subject: "AUTHORITY", kind: "ADMIT" });
  const Wrapper = () => {
    const [values, setValues] = useState(initialOperationValues("AUTHORITY", "ADMIT", null));
    return (
      <CatalogInputs
        entry={entry}
        current={null}
        values={values}
        disabled={false}
        fieldErrors={{}}
        onChange={(path, value) => {
          setValues({ ...values, [path]: value });
        }}
        onFieldsChange={vi.fn()}
      />
    );
  };
  render(<Wrapper />);
  expect(screen.getByRole("button", { name: "Remove role" })).toBeDisabled();
  expect(screen.getByRole("radio", { name: "Owner" })).not.toBeChecked();
  await user.click(screen.getByRole("radio", { name: "Owner" }));
  await user.click(screen.getByRole("button", { name: "Add role" }));
  const groups = screen.getAllByRole("radiogroup", { name: "Role" });
  expect(groups).toHaveLength(2);
  await user.click(within(groups[1]!).getByRole("radio", { name: "Case reader" }));
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeChecked();
  expect(within(groups[1]!).getByRole("radio", { name: "Case reader" })).toBeChecked();
  const row = screen.getByRole("group", { name: "Role 2" });
  await user.click(within(row).getByRole("radio", { name: "One case" }));
  expect(within(row).getByRole("radio", { name: "Owner" })).toBeDisabled();
  expect(within(row).getByRole("textbox", { name: /Case reference/u })).toBeVisible();
  await user.click(screen.getByRole("radio", { name: "Service account" }));
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeDisabled();
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeChecked();
  await user.click(screen.getAllByRole("button", { name: "Remove role" })[0]!);
  expect(screen.getAllByRole("radiogroup", { name: "Role" })).toHaveLength(1);
  expect(screen.getByRole("radio", { name: "Case reader" })).toBeChecked();
});

it("renders and binds optional policy identifiers from the ordinary case catalog", async () => {
  const user = userEvent.setup();
  const Wrapper = () => {
    const [values, setValues] = useState(initialOperationValues("CASE", "OPEN", null));
    return (
      <>
        <CatalogInputs
          entry={operationEntry({ subject: "CASE", kind: "OPEN" })}
          current={null}
          values={values}
          disabled={false}
          fieldErrors={{}}
          onChange={(path, value) => {
            setValues((previous) => ({ ...previous, [path]: value }));
          }}
          onFieldsChange={(changes, omitted = []) => {
            setValues((previous) =>
              Object.fromEntries(
                Object.entries({ ...previous, ...changes }).filter(
                  ([key]) => !omitted.includes(key),
                ),
              ),
            );
          }}
        />
        <output data-testid="identifier-draft">{JSON.stringify(values)}</output>
      </>
    );
  };
  render(<Wrapper />);
  await user.click(screen.getByRole("checkbox", { name: "Record Policy number (optional)" }));
  await user.click(
    screen.getByRole("checkbox", { name: "Record Insurer claim reference (optional)" }),
  );
  await user.type(screen.getByRole("textbox", { name: "Policy number" }), "Policy / 42");
  await user.type(screen.getByRole("textbox", { name: "Insurer claim reference" }), "Insurer / 17");
  expect(JSON.parse(screen.getByTestId("identifier-draft").textContent)).toMatchObject({
    "act.values.policyNumber": "Policy / 42",
    "act.values.insurerClaimReference": "Insurer / 17",
  });
});
