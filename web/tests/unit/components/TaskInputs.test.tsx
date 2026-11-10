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
import type { OperationTaskId } from "@contracts/client";

const target = { kind: "ID", caseId: "00000000-0000-4000-8000-000000000002" } as const;
const current: CurrentCase = {
  screen: { regions: [] },
  context: {
    kind: "CASE",
    caseId: target.caseId,
    revision: 7,
    workRevision: 2,
    history: "NONE",
    tasks: ["WORK:UPDATE_WORK"],
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
    task: operationTask("WORK:UPDATE_WORK"),
    binding: { kind: "WORK", target, workRevision: 2 } as const,
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
  const clear = screen.getByRole("checkbox", { name: /clear/i });
  await userEvent.click(clear);
  expect(change).toHaveBeenCalledWith("field:handlingSummary", null);
  expect(screen.queryByRole("radio")).not.toBeInTheDocument();
});

it("consumes the current correction choices instead of offering the declaration's full static set", () => {
  const correction: CurrentCase = {
    ...current,
    context: {
      ...current.context,
      tasks: ["CASE:CORRECT"],
      editableInputs: [],
      correction: { registration: ["KEEP"], decision: ["KEEP"] },
      seeds: { paymentCurrency: null, registration: null, decision: null, work: null },
    },
  };
  const props = {
    task: operationTask("CASE:CORRECT"),
    binding: { kind: "CASE", target, revision: 7 } as const,
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
  const task = operationTask("AUTHORITY:ADMIT");
  const errors = {
    [taskRowInputKey("authorityGrants", "row-a", "input:role")]: {
      kind: "local",
      reason: "invalidInput",
    } as const,
  };
  render(
    <TaskInputs
      task={task}
      binding={{ kind: "AUTHORITY" }}
      current={null}
      values={{
        "input:principal.kind": "SERVICE",
        authorityGrants: {
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

it("renders representative control shapes across the declared subjects", () => {
  const operations: ReadonlyArray<OperationTaskId> = [
    "CASE:OPEN",
    "CASE:DECIDE",
    "CASE:RECORD_PAYMENT",
    "NOTE:ADD_NOTE",
    "REMINDER:REMINDER_CREATE",
    "AUTHORITY:SET_PROFILE",
  ];
  for (const id of operations) {
    const task = operationTask(id);
    const binding =
      task.subject === "AUTHORITY"
        ? { kind: "AUTHORITY" as const }
        : id === "CASE:OPEN"
          ? { kind: "NEW_CASE" as const }
          : task.subject === "NOTE"
            ? { kind: "NOTE" as const, target }
            : task.subject === "REMINDER"
              ? { kind: "REMINDER" as const, target }
              : { kind: "CASE" as const, target, revision: 7 };
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
      task={operationTask("CASE:CORRECT")}
      binding={{ kind: "CASE", target, revision: 7 }}
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
      task={operationTask("REMINDER:REMINDER_UPDATE")}
      binding={{
        kind: "REMINDER",

        target,
        reminder: { reminderId: "30000000-0000-4000-8000-000000000003", revision: 1 },
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

const AuthoredInputs = ({ id }: { id: "CASE:OPEN" | "AUTHORITY:ADMIT" }) => {
  const [values, setValues] = useState<FormValues>(() =>
    initialOperationValues(
      id === "CASE:OPEN" ? "CASE" : "AUTHORITY",
      id === "CASE:OPEN" ? "OPEN" : "ADMIT",
      null,
    ),
  );
  return (
    <>
      <TaskInputs
        task={operationTask(id)}
        binding={id === "CASE:OPEN" ? { kind: "NEW_CASE" } : { kind: "AUTHORITY" }}
        current={null}
        values={values}
        disabled={false}
        errors={{}}
        onChange={(path, value) => setValues((before) => ({ ...before, [path]: value }))}
        onFieldsChange={(changes, omitted = []) =>
          setValues((before) =>
            Object.fromEntries(
              Object.entries({ ...before, ...changes }).filter(([key]) => !omitted.includes(key)),
            ),
          )
        }
      />
      <output data-testid="authored-inputs">{JSON.stringify(values)}</output>
    </>
  );
};

it("edits admission rows independently while principal and scope restrict role choices", async () => {
  const user = userEvent.setup();
  render(<AuthoredInputs id="AUTHORITY:ADMIT" />);
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
  await user.click(screen.getByRole("radio", { name: "Service account" }));
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeDisabled();
  expect(within(groups[0]!).getByRole("radio", { name: "Owner" })).toBeChecked();
  await user.click(screen.getAllByRole("button", { name: "Remove role" })[0]!);
  expect(screen.getAllByRole("group", { name: "Role" })).toHaveLength(1);
  expect(screen.getByRole("radio", { name: "Case reader" })).toBeChecked();
});

it("selects and edits optional policy identifiers using semantic input identities", async () => {
  const user = userEvent.setup();
  render(<AuthoredInputs id="CASE:OPEN" />);
  await user.click(screen.getByRole("checkbox", { name: "Record Policy number (optional)" }));
  await user.click(
    screen.getByRole("checkbox", { name: "Record Insurer claim reference (optional)" }),
  );
  await user.type(screen.getByRole("textbox", { name: "Policy number" }), "Policy / 42");
  await user.type(screen.getByRole("textbox", { name: "Insurer claim reference" }), "Insurer / 17");
  expect(JSON.parse(screen.getByTestId("authored-inputs").textContent ?? "null")).toMatchObject({
    "field:policyNumber": "Policy / 42",
    "field:insurerClaimReference": "Insurer / 17",
  });
});
