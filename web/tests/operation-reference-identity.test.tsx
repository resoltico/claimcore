import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/v2";
import { OperationEditor } from "../src/views/OperationEditor";
import { generatedResponse } from "./contract-corpus.fixtures";
import { definition, fields, preparation, response } from "./v2-ui.fixtures";

const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

const prepared = () =>
  response("command.prepare", "PREPARED", {
    details: preparation,
    review: {
      before: current.case,
      proposed: { fields, revision: "2" },
      changes: [],
      context: definition.runtime,
      advisory: true,
    },
  });

type SentDraft = {
  operationId: string;
  caseReference: string;
  command: { values: Record<string, string> };
};

const sentDraft = (body: unknown): SentDraft => {
  if (typeof body !== "string") throw new Error("Expected a JSON command request body.");
  return JSON.parse(body) as SentDraft;
};

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("rotates the retained operation ID when only an OPEN reference is changed", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(prepared()).mockResolvedValueOnce(prepared());
  render(
    <OperationEditor
      token="token"
      definition={definition}
      current={null}
      initialCommand="OPEN"
      onClose={vi.fn()}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
  const reference = screen.getByLabelText("Handler's case reference", { exact: true });
  expect(screen.getAllByLabelText("Handler's case reference", { exact: true })).toHaveLength(1);
  await user.type(reference, "NEW-1");
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await user.click(await screen.findByRole("button", { name: "Keep for Recovery" }));
  await user.clear(reference);
  await user.type(reference, "NEW-2");
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await screen.findByRole("dialog", { name: "Review prepared operation" });
  const first = sentDraft(fetch.mock.calls[0]?.[1]?.body);
  const second = sentDraft(fetch.mock.calls[1]?.[1]?.body);
  expect(first.caseReference).toBe("NEW-1");
  expect(first.command.values).not.toHaveProperty("caseReference");
  expect(second.caseReference).toBe("NEW-2");
  expect(second.command.values).not.toHaveProperty("caseReference");
  expect(second.operationId).not.toBe(first.operationId);
});

it("keeps existing references immutable and delegates a reviewed commit", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch
    .mockResolvedValueOnce(prepared())
    .mockResolvedValueOnce(generatedResponse("command.execute"));
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
  expect(screen.getByText(/Case reference:/u)).toHaveTextContent(fields.caseReference);
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await user.click(
    await screen.findByRole("checkbox", { name: "I will submit this exact prepared request." }),
  );
  await user.click(screen.getByRole("button", { name: "Submit exact request" }));
  await user.click(await screen.findByRole("button", { name: "Return to case" }));
  expect(committed).toHaveBeenCalledOnce();
  const draft = sentDraft(fetch.mock.calls[0]?.[1]?.body);
  expect(draft.caseReference).toBe(fields.caseReference);
  expect(draft.command.values).not.toHaveProperty("caseReference");
});
