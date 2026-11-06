import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { OperationReview as Review } from "@contracts/client";
import { render, screen } from "../support/presentation-test-support";
import { reviewConfirm } from "../support/review-controls";
import { preparedFor, current } from "../support/operation-form.fixtures";
import { workBasis } from "../support/inputs";
import { isResponse } from "../../src/protocol/client";
import { OperationReview } from "../../src/views/operation/OperationReview";

const reviewed = (labelId: string): Review => {
  const snapshot = {
    caseId: current.caseId,
    caseReference: current.caseReference,
    revision: current.revision,
    state: current.state,
  };
  return {
    subject: {
      kind: "CASE",
      before: snapshot,
      after: { ...snapshot, revision: snapshot.revision + 1 },
      context: { ...workBasis, productVersion: "synthetic" },
    },
    presentation: {
      changes: [
        {
          key: "status",
          labelId,
          before: { kind: "CODE", vocabulary: "status", token: "OPENED" },
          after: { kind: "CODE", vocabulary: "status", token: "CLOSED" },
        },
      ],
      unchanged: [],
    },
  };
};
it.each([
  ["field.status.label", true],
  ["field.missing.label", false],
  ["diagnostic.TOO_LONG", false],
] as const)(
  "requires complete registered presentation before confirming %s",
  async (labelId, complete) => {
    const prepared = preparedFor("CLOSE");
    const original = prepared.bytes;
    const review = reviewed(labelId);
    expect(
      isResponse("operation.preview", {
        operationId: prepared.input.operationId,
        outcome: { kind: "REVIEWED", data: { review } },
      }),
    ).toBe(true);
    const execute = vi.fn().mockResolvedValue(undefined);
    const confirm = vi.fn();
    const returnToEdit = vi.fn();
    render(
      <OperationReview
        model={{
          prepared,
          review,
          phase: "REVIEWING",
          busy: false,
          confirmed: false,
          confirm,
          execute,
          returnToEdit,
        }}
      />,
    );
    const user = userEvent.setup();
    if (complete) {
      expect(reviewConfirm()).not.toBeDisabled();
      await user.click(reviewConfirm());
      expect(execute).toHaveBeenCalledOnce();
      expect(confirm).toHaveBeenCalledWith(true);
    } else {
      expect(screen.getByRole("alert")).toBeVisible();
      expect(reviewConfirm()).toBeDisabled();
      await user.click(reviewConfirm());
      expect(execute).not.toHaveBeenCalled();
      expect(confirm).not.toHaveBeenCalled();
      await user.click(screen.getByRole("button", { name: "Return to editing" }));
      expect(returnToEdit).toHaveBeenCalledOnce();
    }
    expect(prepared.bytes).toBe(original);
  },
);
