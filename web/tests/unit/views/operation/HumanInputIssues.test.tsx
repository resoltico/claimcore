import { expect, it } from "vitest";
import userEvent from "@testing-library/user-event";
import type { HumanRejection, HumanInputIssue } from "@contracts/client";
import { render, screen } from "../../../support/presentation-test-support";
import { OperationErrors } from "../../../../src/views/operation/OperationErrors";
import { projectTaskIssues, taskRowInputKey } from "../../../../src/operation/taskIssues";
import { operationTask } from "../../../../src/protocol/taskCatalog";

const issue: HumanInputIssue = {
  input: "field:paymentDate",
  row: null,
  message: { messageId: "label.before", arguments: [] },
  related: {
    key: "limit-date",
    labelId: "field.paymentDate.label",
    prominence: "PRIMARY",
    value: { kind: "DATE", value: "2026-11-09" },
  },
};
const rejection = (issues: ReadonlyArray<HumanInputIssue>): HumanRejection => ({
  kind: "INPUT",
  issues,
  advice: {
    action: "CORRECT_INPUT",
    message: {
      messageId: "diagnostic.INVALID_INPUT",
      arguments: [{ key: "issueCount", value: { kind: "COUNT", value: issues.length } }],
    },
  },
});
it("keeps an unbound semantic issue visible with its supplied related value", () => {
  const value = rejection([issue]);
  const projection = projectTaskIssues([issue], operationTask("people.admit"), {});
  expect(projection.unbound).toEqual([issue]);
  const view = render(
    <OperationErrors
      model={{
        notice: { kind: "human", advice: value.advice },
        fieldErrors: projection.fields,
        rejection: value,
      }}
    />,
  );
  expect(screen.getByText("Before")).toBeVisible();
  expect(view.container.querySelector('time[datetime="2026-11-09"]')).toBeVisible();
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});
it("focuses the submitted row key even when its displayed order differs", async () => {
  const row: HumanInputIssue = {
    ...issue,
    input: "input:role",
    row: { collection: "peopleGrants", submittedOrdinal: 1 },
    related: null,
  };
  const value = rejection([row]);
  const projection = projectTaskIssues([row], operationTask("people.admit"), {
    peopleGrants: ["original-a", "original-b"],
  });
  const key = taskRowInputKey("peopleGrants", "original-b", "input:role");
  render(
    <>
      <input aria-label="Moved submitted row" name={key} />
      <OperationErrors
        model={{
          notice: { kind: "human", advice: value.advice },
          fieldErrors: projection.fields,
          rejection: value,
        }}
      />
    </>,
  );
  await userEvent.click(screen.getByRole("link"));
  expect(screen.getByRole("textbox", { name: "Moved submitted row" })).toHaveFocus();
});
