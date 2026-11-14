import { useState } from "react";
import { within } from "@testing-library/react";
import { initialOperationValues } from "../../../src/operation/operationForm";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../support/presentation-test-support";
import { TaskInputs } from "../../../src/components/TaskInputs";
import { taskRowInputKey } from "../../../src/operation/taskIssues";
import { operationTask } from "../../../src/protocol/taskCatalog";
import type { CurrentCase } from "../../../src/protocol/types";
import type { FormValues } from "../../../src/protocol/forms";
import type { OperationTaskId, TaskInputId } from "@contracts/client";

const target = { kind: "ID", caseId: "00000000-0000-4000-8000-000000000002" } as const;
const current: CurrentCase = {
  screen: { regions: [] },
  context: {
    kind: "CASE",
    caseId: target.caseId,
    revision: 7,
    workRevision: 2,
    history: "NONE",
    tasks: ["work.update"],
    editableInputs: ["field:handlingSummary"],
    correction: null,
    seeds: {
      paymentCurrency: null,
      registration: null,
      decision: null,
      work: { summary: "Permitted input seed", responsible: null },
    },
    notes: [],
    payments: [],
    nextActions: [],
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};

it("renders only work inputs permitted by Core and preserves explicit clear", async () => {
  const fields = vi.fn();
  const change = vi.fn();
  const props = {
    task: operationTask("work.update"),
    binding: { kind: "WORK", target, expectedRevision: 2 } as const,
    current,
    disabled: false,
    errors: {},
    onChange: change,
    onFieldsChange: fields,
  };
  const view = render(<TaskInputs {...props} values={{}} />);
  expect(screen.queryByText("Responsible account action")).not.toBeInTheDocument();
  const boxes = screen.getAllByRole("checkbox");
  expect(boxes).toHaveLength(1);
  await userEvent.click(boxes[0]!);
  expect(fields).toHaveBeenCalledWith({ "field:handlingSummary": "Permitted input seed" }, []);
  view.rerender(
    <TaskInputs {...props} values={{ "field:handlingSummary": "Permitted input seed" }} />,
  );
  const clear = screen.getByRole("checkbox", { name: /clear/iu });
  await userEvent.click(clear);
  expect(change).toHaveBeenCalledWith("field:handlingSummary", null);
  expect(screen.queryByRole("radio")).not.toBeInTheDocument();
});

it("consumes the current correction choices instead of offering the declaration's full static set", () => {
  const correction: CurrentCase = {
    ...current,
    context: {
      ...current.context,
      tasks: ["case.correct"],
      editableInputs: [],
      correction: { registration: ["KEEP"], decision: ["KEEP"] },
      seeds: { paymentCurrency: null, registration: null, decision: null, work: null },
    },
  };
  const props = {
    task: operationTask("case.correct"),
    binding: { kind: "CASE", target, expectedRevision: 7 } as const,
    current: correction,
    values: {},
    disabled: false,
    errors: {},
    onChange: vi.fn(),
    onFieldsChange: vi.fn(),
  };
  const view = render(<TaskInputs {...props} />);
  expect(screen.getAllByRole("radio")).toHaveLength(2);
  view.rerender(
    <TaskInputs
      {...props}
      current={{
        ...correction,
        context: {
          ...correction.context,
          correction: { registration: ["KEEP", "UPDATE"], decision: ["KEEP"] },
        },
      }}
    />,
  );
  expect(screen.getAllByRole("radio")).toHaveLength(3);
});

it("binds collection controls and errors to row keys and consumes Domain role admission metadata", () => {
  const task = operationTask("people.admit");
  const errors = {
    [taskRowInputKey("peopleGrants", "row-a", "input:role")]: {
      kind: "local",
      reason: "invalidInput",
    } as const,
  };
  render(
    <TaskInputs
      task={task}
      binding={{ kind: "PEOPLE" }}
      current={null}
      values={{
        "input:principal.kind": "SERVICE",
        peopleGrants: {
          kind: "COLLECTION",
          rows: [{ key: "row-a", values: { "input:scope": "INSTALLATION" } }],
        },
      }}
      disabled={false}
      errors={errors}
      onChange={vi.fn()}
      onFieldsChange={vi.fn()}
    />,
  );
  const owner = screen.getByRole("radio", { name: "Owner" });
  const auditor = screen.getByRole("radio", { name: "Auditor" });
  expect(owner).toBeDisabled();
  expect(auditor).toBeEnabled();
  expect(screen.getByRole("alert")).toBeVisible();
});

const representativeBinding = (
  task: ReturnType<typeof operationTask>,
  id: OperationTaskId,
): Parameters<typeof TaskInputs>[0]["binding"] => {
  if (task.subject === "PEOPLE") {
    return { kind: "PEOPLE" as const };
  }
  if (id === "case.open") {
    return { kind: "CASE", target: { kind: "REFERENCE", caseReference: "" }, expectedRevision: 0 };
  }
  if (task.subject === "NOTE") {
    return { kind: "NOTE" as const, target };
  }
  if (task.subject === "REMINDER") {
    return { kind: "REMINDER" as const, target };
  }
  return { kind: "CASE" as const, target, expectedRevision: 7 };
};
it("renders representative control shapes across the declared subjects", () => {
  const operations: ReadonlyArray<OperationTaskId> = [
    "case.open",
    "case.decide",
    "case.recordPayment",
    "note.add",
    "reminder.create",
    "people.setProfile",
  ];
  for (const id of operations) {
    const task = operationTask(id);
    const binding = representativeBinding(task, id);
    const view = render(
      <TaskInputs
        task={task}
        binding={binding}
        current={null}
        values={{}}
        disabled={false}
        errors={{}}
        onChange={vi.fn()}
        onFieldsChange={vi.fn()}
      />,
    );
    expect(view.container.querySelector("input,textarea,button")).not.toBeNull();
    view.unmount();
  }
});

it("preserves a selected correction choice when current Core choices stop offering it", () => {
  const change = vi.fn();
  render(
    <TaskInputs
      task={operationTask("case.correct")}
      binding={{ kind: "CASE", target, expectedRevision: 7 }}
      current={{
        ...current,
        context: {
          ...current.context,
          correction: { registration: ["KEEP", "UPDATE"], decision: ["KEEP", "UPDATE"] },
        },
      }}
      values={{ "group:registration": "KEEP", "group:decision": "CLEAR" }}
      disabled={false}
      errors={{}}
      onChange={change}
      onFieldsChange={vi.fn()}
    />,
  );
  const selected = screen.getByRole("radio", { name: "Clear recorded value" });
  expect(selected).toBeChecked();
  expect(selected).toBeDisabled();
  expect(selected).toHaveAccessibleDescription(/Not available with the current facts/u);
  expect(change).not.toHaveBeenCalled();
});

it("keeps reminder identity and revision bound while editing only authored action and date", () => {
  const change = vi.fn();
  const view = render(
    <TaskInputs
      task={operationTask("reminder.update")}
      binding={{
        kind: "REMINDER",
        target,
        reminderId: "30000000-0000-4000-8000-000000000003",
        expectedRevision: 1,
      }}
      current={current}
      values={{ "input:reminderAction": "Retained action", "input:reminderDueDate": "2026-11-05" }}
      disabled={false}
      errors={{}}
      onChange={change}
      onFieldsChange={vi.fn()}
    />,
  );
  expect(view.container.querySelector('[name="input:reminderId"]')).toBeNull();
  expect(view.container.querySelector('[name="input:expectedReminderRevision"]')).toBeNull();
  expect(screen.getByRole("textbox", { name: "Action" })).toHaveValue("Retained action");
  expect(change).not.toHaveBeenCalled();
});

const AuthoredInputs = ({ id }: { id: "case.open" | "people.admit" }) => {
  const [values, setValues] = useState<FormValues>(() =>
    initialOperationValues(
      id === "case.open" ? "CASE" : "PEOPLE",
      id === "case.open" ? "case.open" : "people.admit",
      null,
    ),
  );
  return (
    <>
      <TaskInputs
        task={operationTask(id)}
        binding={
          id === "case.open"
            ? {
                kind: "CASE",
                target: { kind: "REFERENCE", caseReference: "" },
                expectedRevision: 0,
              }
            : { kind: "PEOPLE" }
        }
        current={null}
        values={values}
        disabled={false}
        errors={{}}
        onChange={(path, value) => {
          setValues((before) => ({ ...before, [path]: value }));
        }}
        onFieldsChange={(changes, omitted = []) => {
          setValues((before) =>
            Object.fromEntries(
              Object.entries({ ...before, ...changes }).filter(([key]) => !omitted.includes(key)),
            ),
          );
        }}
      />
      <output data-testid="authored-inputs">{JSON.stringify(values)}</output>
    </>
  );
};

it("edits admission rows independently while principal and scope restrict role choices", async () => {
  const user = userEvent.setup();
  render(<AuthoredInputs id="people.admit" />);
  expect(screen.getByRole("button", { name: "Remove role" })).toBeDisabled();
  await user.click(screen.getByRole("radio", { name: "Owner" }));
  await user.click(screen.getByRole("button", { name: "Add role" }));
  const groups = screen.getAllByRole("group", { name: "Role" });
  expect(groups).toHaveLength(2);
  await user.click(within(groups[1]!).getByRole("radio", { name: "Case reader" }));
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeChecked();
  expect(within(groups[1]!).getByRole("radio", { name: "Case reader" })).toBeChecked();
  const row = screen.getByRole("group", { name: "Role 2" });
  await user.click(within(row).getByRole("radio", { name: "One case" }));
  expect(within(row).getByRole("radio", { name: "Owner" })).toBeDisabled();
  expect(within(row).getByRole("textbox", { name: /Case reference/u })).toBeVisible();
  await user.type(within(row).getByRole("textbox", { name: /Case reference/u }), "SCOPED-CASE");
  await user.click(within(row).getByRole("radio", { name: "All cases" }));
  const authored = JSON.parse(screen.getByTestId("authored-inputs").textContent ?? "null") as {
    peopleGrants: { rows: { values: Record<string, unknown> }[] };
  };
  expect(authored.peopleGrants.rows[1]!.values).not.toHaveProperty("field:caseReference");
  expect(within(row).queryByRole("textbox", { name: /Case reference/u })).toBeNull();

  await user.click(screen.getByRole("radio", { name: "Service account" }));
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeDisabled();
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeChecked();
  await user.click(screen.getAllByRole("button", { name: "Remove role" })[0]!);
  expect(screen.getAllByRole("group", { name: "Role" })).toHaveLength(1);
  expect(screen.getByRole("radio", { name: "Case reader" })).toBeChecked();
});

it("edits plain optional open-case fields and omits a cleared blank input", async () => {
  const user = userEvent.setup();
  render(<AuthoredInputs id="case.open" />);
  expect(screen.queryByRole("checkbox", { name: /Record Policy number/u })).toBeNull();
  await user.type(screen.getByRole("textbox", { name: "Policy number" }), "Policy / 42");
  await user.type(screen.getByRole("textbox", { name: "Insurer claim reference" }), "Insurer / 17");
  expect(JSON.parse(screen.getByTestId("authored-inputs").textContent ?? "null")).toMatchObject({
    "field:policyNumber": "Policy / 42",
    "field:insurerClaimReference": "Insurer / 17",
  });
  await user.clear(screen.getByRole("textbox", { name: /Policy number/u }));
  expect(JSON.parse(screen.getByRole("status").textContent ?? "null")).not.toHaveProperty(
    "field:policyNumber",
  );
});

it.each([
  {
    task: "case.amendRegistration",
    currency: "claimedCurrency",
    inputs: ["field:claimedAmount", "field:claimedCurrency"],
    values: {},
    correction: null,
    expected: { "field:claimedAmount": "", "field:claimedCurrency": "" },
  },
  {
    task: "case.correct",
    currency: "payableCurrency",
    inputs: ["field:payableAmount", "field:payableCurrency"],
    values: { "group:registration": "KEEP", "group:decision": "UPDATE" },
    correction: { registration: ["KEEP", "UPDATE"], decision: ["KEEP", "UPDATE", "CLEAR"] },
    expected: { "field:payableAmount": "", "field:payableCurrency": "" },
  },
] satisfies ReadonlyArray<{
  task: OperationTaskId;
  currency: string;
  inputs: TaskInputId[];
  values: FormValues;
  correction: CurrentCase["context"]["correction"];
  expected: FormValues;
}>)(
  "selects $currency only through its one atomic money pair",
  async ({ task, currency, inputs, values, correction, expected }) => {
    const fields = vi.fn();
    const view = render(
      <TaskInputs
        task={operationTask(task)}
        binding={{ kind: "CASE", target, expectedRevision: 7 }}
        current={{
          ...current,
          context: { ...current.context, tasks: [task], editableInputs: inputs, correction },
        }}
        values={values}
        disabled={false}
        errors={{}}
        onChange={vi.fn()}
        onFieldsChange={fields}
      />,
    );
    const selections = view.container.querySelectorAll<HTMLInputElement>(
      `input[data-selects-fields~="${currency}"]`,
    );
    expect(selections).toHaveLength(1);
    await userEvent.click(selections[0]!);
    expect(fields).toHaveBeenCalledExactlyOnceWith(expected, []);
  },
);
