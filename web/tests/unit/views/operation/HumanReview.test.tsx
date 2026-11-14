import { operationAffordance } from "../../../support/affordances.fixtures";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { HumanReview, OperationSubmission } from "@contracts/client";
import { render, screen } from "../../../support/presentation-test-support";
import { OperationReview } from "../../../../src/views/operation/OperationReview";
import { prepareRequest } from "../../../../src/protocol/preparedRequest";
const operationId = "019aa6a0-0000-7000-8000-000000000001";
const caseId = "00000000-0000-4000-8000-000000000002";
const review: HumanReview = {
  presentation: {
    changes: [],
    unchanged: [],
  },
  document: {
    screen: {
      regions: [
        {
          role: "FACTS",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "opaque-review",
                labelId: "label.details",
                prominence: "PRIMARY",
                value: {
                  kind: "TEXT",
                  value: "Core reviewed caption Ω",
                },
              },
            ],
          },
        },
        {
          role: "DETAILS",
          titleId: "label.recordDetails",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "operationId",
                labelId: "input.operationId.label",
                prominence: "DETAIL",
                value: {
                  kind: "IDENTIFIER",
                  value: operationId,
                },
              },
            ],
          },
        },
      ],
    },
    subject: {
      kind: "REVIEW",
      confirmation: {
        kind: "CASE_REFERENCE",
        reference: "CORE-REFERENCE",
      },
      consequence: "IRREVERSIBLE",
    },
    actions: [
      operationAffordance(
        "case.erase",
        { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 7 },
        {},
        [],
      ),
    ],
    links: [],
    history: null,
    nextCursor: null,
    basis: {
      kind: "SNAPSHOT",
      observation: {
        observedAt: "2026-11-09T12:00:00.000000Z",
        businessDate: "2026-11-09",
        timeZone: "Etc/UTC",
      },
    },
    disclosure: "CURRENT",
  },
};
const model = (input: OperationSubmission, value: HumanReview) => ({
  prepared: prepareRequest("operation.execute", input),
  review: value,
  phase: "REVIEWING",
  busy: false,
  confirmed: false,
  confirm: vi.fn(),
  execute: vi.fn().mockResolvedValue(undefined),
  returnToEdit: vi.fn(),
});
it("renders Core review content and requires its exact confirmation target while technical IDs stay in Details", async () => {
  const value = model(
    {
      operationId,
      task: "case.erase",
      binding: {
        kind: "CASE",
        target: {
          kind: "ID",
          caseId,
        },
        expectedRevision: 7,
      },
      inputs: {},
    },
    review,
  );
  const view = render(
    <OperationReview model={value}>
      {(parts) =>
        parts === null ? null : (
          <>
            {parts.body}
            {parts.actions}
          </>
        )
      }
    </OperationReview>,
  );
  expect(screen.getByText("Core reviewed caption Ω")).toBeVisible();
  expect(screen.getByText(operationId)).not.toBeVisible();
  const submit = view.container.querySelector<HTMLButtonElement>("[data-confirm-operation]")!;
  expect(submit).toBeDisabled();
  await userEvent.type(screen.getByRole("textbox"), "WRONG-REFERENCE");
  expect(submit).toBeDisabled();
  await userEvent.clear(screen.getByRole("textbox"));
  await userEvent.type(screen.getByRole("textbox"), "CORE-REFERENCE");
  await userEvent.click(submit);
  expect(value.confirm).toHaveBeenLastCalledWith(true);
  expect(value.execute).toHaveBeenCalledTimes(1);
});
it("resets consent when a new Core review supplies a different target", async () => {
  const value = model(
    {
      operationId,
      task: "case.erase",
      binding: {
        kind: "CASE",
        target: {
          kind: "ID",
          caseId,
        },
        expectedRevision: 7,
      },
      inputs: {},
    },
    review,
  );
  const view = render(
    <OperationReview model={value}>
      {(parts) =>
        parts === null ? null : (
          <>
            {parts.body}
            {parts.actions}
          </>
        )
      }
    </OperationReview>,
  );
  await userEvent.type(screen.getByRole("textbox"), "CORE-REFERENCE");
  const next: HumanReview = {
    ...review,
    document: {
      ...review.document,
      subject: {
        ...review.document.subject,
        confirmation: {
          kind: "CASE_REFERENCE",
          reference: "OTHER-REFERENCE",
        },
      },
    },
  };
  view.rerender(
    <OperationReview
      model={{
        ...value,
        review: next,
      }}
    >
      {(parts) =>
        parts === null ? null : (
          <>
            {parts.body}
            {parts.actions}
          </>
        )
      }
    </OperationReview>,
  );
  expect(screen.getByRole("textbox")).toHaveValue("");
  expect(view.container.querySelector("[data-confirm-operation]")).toBeDisabled();
  expect(value.execute).not.toHaveBeenCalled();
});
it("records a routine review with one consent action", async () => {
  const routine: HumanReview = {
    ...review,
    document: {
      ...review.document,
      subject: { ...review.document.subject, consequence: "ROUTINE" },
      actions: [
        operationAffordance(
          "case.close",
          {
            kind: "CASE",
            target: { kind: "REFERENCE", caseReference: "CASE-1" },
            expectedRevision: 1,
          },
          {},
          [],
        ),
      ],
    },
  };
  const value = model(
    {
      operationId,
      task: "case.close",
      binding: {
        kind: "CASE",
        target: {
          kind: "ID",
          caseId,
        },
        expectedRevision: 7,
      },
      inputs: {},
    },
    routine,
  );
  const view = render(
    <OperationReview model={value}>
      {(parts) =>
        parts === null ? null : (
          <>
            {parts.body}
            {parts.actions}
          </>
        )
      }
    </OperationReview>,
  );
  expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
  await userEvent.click(
    view.container.querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  expect(value.confirm).toHaveBeenCalledExactlyOnceWith(true);
  expect(value.execute).toHaveBeenCalledTimes(1);
});
