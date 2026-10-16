import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { OperationEditor } from "../src/views/OperationEditor";
import { generatedResponse } from "./contract-corpus.fixtures";
import { previewForRequest } from "./preview-request.fixtures";
import { definition, fields, digest, response, runtime } from "./service-ui.fixtures";

const current: CurrentCase = {
  caseReference: fields.caseReference,
  revision: 1,
  offers: ["CLOSE"].map((identifier) => ({
    definition: identifier,
    caseReference: null,
    expectedRevision: null,
    choices: [],
  })),
  historyDetails: ["SUMMARY", "FULL"],
  state: { tag: "LIVE", fields, disposition: "ACTIVE", availableActs: ["CLOSE"] },
};

const prepared = () =>
  response("case.preview", "REVIEWED", {
    requestSha256: digest,
    review: {
      before: current,
      after: {
        caseReference: fields.caseReference,
        revision: 2,
        state: { tag: "LIVE", disposition: "ACTIVE", fields },
      },
      changes: [],
      context: runtime,
    },
  });

type SentDraft = {
  operationId: string;
  caseReference: string;
  act: { kind: string; values?: Record<string, string> };
};

const sentDraft = (body: unknown): SentDraft => {
  if (typeof body !== "string") {
    throw new Error("Expected a JSON command request body.");
  }
  return JSON.parse(body) as SentDraft;
};

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("keeps existing references immutable and delegates a reviewed commit", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch
    .mockImplementationOnce(previewForRequest(prepared()))
    .mockResolvedValueOnce(generatedResponse("case.execute"));
  const committed = vi.fn();
  render(
    <OperationEditor
      token="token"
      definition={definition}
      current={current}
      initialCommand="CLOSE"
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />,
  );
  expect(screen.queryByLabelText("Handler's case reference", { exact: true })).toBeNull();
  expect(screen.getByText(/Handler's case reference:/u)).toHaveTextContent(fields.caseReference);
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  await user.click(await screen.findByRole("button", { name: "Return to case" }));
  expect(committed).toHaveBeenCalledOnce();
  const draft = sentDraft(fetch.mock.calls[0]?.[1]?.body);
  expect(draft.caseReference).toBe(fields.caseReference);
  expect(draft.act).toEqual({ kind: "CLOSE" });
});
