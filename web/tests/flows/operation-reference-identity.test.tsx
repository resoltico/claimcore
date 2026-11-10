import { caseDocumentInput } from "../support/case-document.fixtures";
import { previewReply } from "../support/presentation-state.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";

import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { OperationEditor } from "../../src/views/OperationEditor";
import { wireResponse } from "../support/reply.fixtures";
import { definition, fields } from "../support/service-ui.fixtures";
const current: CurrentCase = caseDocumentInput({
  tasks: ["CASE:CLOSE"],
  caseId: "20000000-0000-4000-8000-000000000001",
  revision: 1,
  history: "FULL",
});
const prepared = () => previewReply();
type SentDraft = {
  operationId: string;
  target: { kind: "REFERENCE"; caseReference: string };
  act: {
    kind: string;
    values?: Record<string, string>;
  };
};
const sentDraft = (body: unknown): SentDraft => {
  if (typeof body !== "string") {
    throw new Error("Expected a JSON act request body.");
  }
  return JSON.parse(body) as SentDraft;
};
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("keeps existing references immutable and delegates a reviewed commit", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch
    .mockImplementationOnce(() => Promise.resolve(prepared()))
    .mockResolvedValueOnce(wireResponse("operation.execute"));
  const committed = vi.fn();
  render(
    <OperationEditor
      definition={definition}
      current={current}
      initialAct="CLOSE"
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />,
  );
  expect(
    screen.queryByLabelText("Case reference", {
      exact: true,
    }),
  ).toBeNull();
  expect(screen.getAllByText(fields.caseReference).length).toBeGreaterThan(0);
  await user.click(getButton("Review changes"));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await user.click(await findButton("Return to case"));
  expect(committed).toHaveBeenCalledOnce();
  const draft = sentDraft(fetch.mock.calls[0]?.[1]?.body);
  expect(draft.target).toEqual({ kind: "ID", caseId: current.context.caseId });
  expect(draft.act).toEqual({
    kind: "CLOSE",
  });
});
