import type { ComponentProps } from "react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../support/presentation-test-support";
import { TaskInputs } from "../../../src/components/TaskInputs";
import { operationTask } from "../../../src/protocol/taskCatalog";
import { isFormCollection, type FormValue, type FormValues } from "../../../src/protocol/forms";
import { operationAffordance } from "../../support/affordances.fixtures";

type Props = ComponentProps<typeof TaskInputs>;
const target = { kind: "ID" as const, caseId: "10000000-0000-4000-8000-000000000001" };
const correction = (values: FormValues): Props => ({
  task: operationTask("case.correct"),
  binding: { kind: "CASE", target, expectedRevision: 3 },
  current: null,
  values,
  disabled: false,
  errors: {},
  onChange: vi.fn(),
  onFieldsChange: vi.fn(),
});
const selected = (container: HTMLElement) =>
  [
    ...container.querySelectorAll<HTMLInputElement>(
      'input[type="radio"][name="group:decision"]:checked',
    ),
  ].map((input) => input.value);

it("displays the generated missing correction-group default without acquiring authored changes", () => {
  const props = correction({});
  const view = render(<TaskInputs {...props} />);
  expect(selected(view.container)).toEqual(["KEEP"]);
  expect(props.onChange).not.toHaveBeenCalled();
  expect(props.onFieldsChange).not.toHaveBeenCalled();
  expect(props.values).toEqual({});
});
it.each(["", null, "INVALID", 0, false] satisfies ReadonlyArray<FormValue>)(
  "preserves explicit unusable correction choice %s instead of displaying a missing-value default",
  (value) => {
    const props = correction({ "group:decision": value });
    const view = render(<TaskInputs {...props} />);
    expect(selected(view.container)).toEqual([]);
    expect(props.onChange).not.toHaveBeenCalled();
    expect(props.onFieldsChange).not.toHaveBeenCalled();
    expect(props.values).toEqual({ "group:decision": value });
  },
);
it.each(["KEEP", "UPDATE", "CLEAR"] as const)(
  "retains explicit supported correction choice %s",
  (value) => {
    const props = correction({ "group:decision": value });
    const view = render(<TaskInputs {...props} />);
    expect(selected(view.container)).toEqual([value]);
    expect(props.onChange).not.toHaveBeenCalled();
  },
);
it("keeps UPDATE seeds dormant while a group is explicitly selected", () => {
  const props = correction({ "group:decision": "UPDATE" });
  const view = render(
    <TaskInputs
      {...props}
      affordance={operationAffordance(
        "case.correct",
        { kind: "CASE", target, expectedRevision: 3 },
        {
          "field:payableAmount": "900",
          "field:payableCurrency": "EUR",
          "field:paymentDecisionDate": "2026-02-01",
        },
      )}
    />,
  );
  expect(selected(view.container)).toEqual(["UPDATE"]);
  expect(view.container.querySelector('[name="field:payableAmount"]')).toBeNull();
  expect(props.onFieldsChange).not.toHaveBeenCalled();
  expect(props.values).toEqual({ "group:decision": "UPDATE" });
});
it("acquires generated collection-row defaults even when no optional affordance is supplied", async () => {
  const changed = vi.fn<(input: string, value: FormValue) => void>();
  render(
    <TaskInputs
      task={operationTask("people.admit")}
      binding={{ kind: "PEOPLE", expectedRevision: 0 }}
      current={null}
      values={{ peopleGrants: { kind: "COLLECTION", rows: [{ key: "existing", values: {} }] } }}
      disabled={false}
      errors={{}}
      onChange={changed}
      onFieldsChange={vi.fn()}
    />,
  );
  await userEvent.click(screen.getByRole("button", { name: "Add role" }));
  expect(changed).toHaveBeenCalledOnce();
  const [path, acquired] = changed.mock.calls[0]!;
  expect(path).toBe("peopleGrants");
  if (!isFormCollection(acquired)) {
    throw new Error("A structured collection is required");
  }
  expect(acquired.rows).toHaveLength(2);
  expect(acquired.rows[0]).toEqual({ key: "existing", values: {} });
  const added = acquired.rows[1]!;
  expect(typeof added.key).toBe("string");
  expect(added.key).not.toBe("existing");
  expect(added.values).toEqual({ "input:scope": "INSTALLATION" });
});
