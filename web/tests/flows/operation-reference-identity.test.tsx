import { caseAdvice } from "../support/case-advice.fixtures";
import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { OperationEditor } from "../../src/views/OperationEditor";
import { wireResponse } from "../support/contract-corpus.fixtures";
import { previewForRequest } from "../support/preview-request.fixtures";
import { definition, fields, response, runtime } from "../support/service-ui.fixtures";
const current: CurrentCase = {
  caseReference: fields.caseReference,
  revision: 1,
  advice: {
    ...caseAdvice(["CLOSE"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields,
  },
};
const prepared = () =>
  response("operation.preview", "REVIEWED", {
    review: {
      subject: {
        kind: "CASE",
        before: current,
        after: {
          caseReference: fields.caseReference,
          revision: 2,
          state: {
            kind: "ACTIVE",
            fields,
          },
        },
        changes: [],
        context: runtime,
      },
    },
  });
type SentDraft = {
  operationId: string;
  caseReference: string;
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
    .mockImplementationOnce(previewForRequest(prepared()))
    .mockResolvedValueOnce(wireResponse("operation.execute"));
  const committed = vi.fn();
  render(
    <OperationEditor
      token="token"
      definition={definition}
      current={current}
      initialAct="CLOSE"
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />,
  );
  expect(
    screen.queryByLabelText("Handler's case reference", {
      exact: true,
    }),
  ).toBeNull();
  expect(screen.getByText(/Handler's case reference:/u)).toHaveTextContent(fields.caseReference);
  await user.click(getButton("Review changes"));
  await user.click(
    await screen.findByRole("checkbox", {
      name: "I confirm these changes.",
    }),
  );
  await user.click(getButton("Record changes"));
  await user.click(await findButton("Return to case"));
  expect(committed).toHaveBeenCalledOnce();
  const draft = sentDraft(fetch.mock.calls[0]?.[1]?.body);
  expect(draft.caseReference).toBe(fields.caseReference);
  expect(draft.act).toEqual({
    kind: "CLOSE",
  });
});
