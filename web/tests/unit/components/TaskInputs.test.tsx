import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../support/presentation-test-support";
import { TaskInputs } from "../../../src/components/TaskInputs";
import { taskRowInputKey } from "../../../src/operation/taskIssues";
import { operationTask } from "../../../src/protocol/taskCatalog";
import type { CurrentCase } from "../../../src/protocol/types";
import type { FormValues } from "../../../src/protocol/forms";
import type { OperationTaskId } from "@contracts/client";

const operationId = "019aa6a0-0000-7000-8000-000000000001";
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
    binding: { kind: "WORK", operationId, target, workRevision: 2 } as const,
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
    binding: { kind: "CASE", operationId, target, revision: 7 } as const,
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
      binding={{ kind: "AUTHORITY", operationId }}
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
        ? { kind: "AUTHORITY" as const, operationId }
        : id === "CASE:OPEN"
          ? { kind: "NEW_CASE" as const, operationId }
          : task.subject === "NOTE"
            ? { kind: "NOTE" as const, operationId, target }
            : task.subject === "REMINDER"
              ? { kind: "REMINDER" as const, operationId, target }
              : { kind: "CASE" as const, operationId, target, revision: 7 };
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
