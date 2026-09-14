import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/v2";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields, preparation, response } from "./v2-ui.fixtures";

type SentDraft = {
  operationId: string;
  caseReference: string;
  expectedRevision: string;
  command: { kind: string; values: Record<string, string> };
};

const sentDraft = (call: number): SentDraft => {
  const body = vi.mocked(globalThis.fetch).mock.calls[call]?.[1]?.body;
  if (typeof body !== "string") throw new Error("Expected a synthetic command request body.");
  return JSON.parse(body) as SentDraft;
};

const editor = (current: CurrentCase | null, initialCommand: "OPEN" | "CLOSE" | "CORRECT_CASE") => (
  <OperationEditor
    token="synthetic-token"
    definition={definition}
    current={current}
    initialCommand={initialCommand}
    onClose={vi.fn()}
    onCommitted={vi.fn()}
    onMutationLockChange={vi.fn()}
  />
);

const preparedResponse = () =>
  response("command.prepare", "PREPARED", {
    details: preparation,
    review: {
      before: null,
      proposed: { fields, revision: "1" },
      changes: [],
      context: definition.runtime,
      advisory: true,
    },
  });

const refusedResponse = () =>
  response("command.execute", "REFUSED_BEFORE_ATTEMPT", {
    preparation: null,
    rejection: {
      code: "RECOVERY_ACTION_UNAVAILABLE",
      message: "The exact preparation was refused.",
      recommendedAction: "READ_CURRENT",
    },
  });

const rejectedResponse = (code: string, message: string, revision: string | null = null) =>
  response("command.prepare", "REJECTED", {
    operationId: preparation.summary.operationId,
    rejection: {
      code,
      message,
      field: null,
      actualRevision: revision,
      recommendedAction: revision === null ? "CORRECT_INPUT" : "READ_CURRENT",
    },
  });

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("allocates a new ID when editing a definitely refused prepared request", async () => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch)
    .mockResolvedValueOnce(preparedResponse())
    .mockResolvedValueOnce(refusedResponse())
    .mockResolvedValueOnce(rejectedResponse("INVALID_INPUT", "Synthetic validation refusal."));
  render(editor(null, "OPEN"));
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await user.click(
    await screen.findByRole("checkbox", { name: "I will submit this exact prepared request." }),
  );
  await user.click(screen.getByRole("button", { name: "Submit exact request" }));
  await screen.findByText("The exact preparation was refused.");
  const claimant = screen.getByLabelText("Claimant name", { exact: true });
  await user.type(claimant, "Synthetic B");
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await screen.findByText("Synthetic validation refusal.");
  expect(sentDraft(0).operationId).not.toBe(sentDraft(2).operationId);
  expect(sentDraft(0).command.values["claimantName"]).toBe("");
  expect(sentDraft(2).command.values["claimantName"]).toBe("Synthetic B");
});

it("retries the frozen request after delivery loss despite a changed current revision", async () => {
  const user = userEvent.setup();
  const current: CurrentCase = {
    case: { fields, revision: "1" },
    availableCommands: ["CLOSE"],
  };
  vi.mocked(globalThis.fetch)
    .mockRejectedValueOnce(new Error("Synthetic delivery loss"))
    .mockResolvedValueOnce(rejectedResponse("VERSION_CONFLICT", "Synthetic stale revision.", "2"));
  const view = render(editor(current, "CLOSE"));
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await screen.findByText(/Inspect Recovery before retrying this exact operation/u);
  view.rerender(editor({ ...current, case: { ...current.case, revision: "2" } }, "CLOSE"));
  await user.click(screen.getByRole("button", { name: "Retry exact prepare" }));
  await waitFor(() => expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(2));
  expect(sentDraft(1)).toEqual(sentDraft(0));
  expect(sentDraft(1).expectedRevision).toBe("1");
});

it("sends an explicit grouped correction and preserves all non-replaced groups", async () => {
  const user = userEvent.setup();
  const current: CurrentCase = {
    case: {
      fields: {
        ...fields,
        paymentDecisionDate: "2026-09-03",
        payableAmount: "9.99",
        payableCurrency: "EUR",
        paymentDate: "2026-09-04",
      },
      revision: "1",
    },
    availableCommands: ["CORRECT_CASE"],
  };
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    rejectedResponse("INVALID_INPUT", "Synthetic validation refusal."),
  );
  render(editor(current, "CORRECT_CASE"));
  const registration = document.querySelector<HTMLSelectElement>("#correction-registration-mode");
  if (registration === null) throw new Error("Expected a registration correction selector.");
  await user.selectOptions(registration, "REPLACE");
  const claimant = screen.getByLabelText("Claimant name", { exact: true });
  await user.clear(claimant);
  await user.type(claimant, "Corrected claimant");
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  const body = vi.mocked(globalThis.fetch).mock.calls[0]?.[1]?.body;
  if (typeof body !== "string") throw new Error("Expected a grouped correction request body.");
  const draft = JSON.parse(body) as {
    command: {
      kind: string;
      groups: {
        registration: { mode: string; values: { claimantName: string } };
        decision: { mode: string };
        payment: { mode: string };
      };
    };
  };
  expect(draft.command).toMatchObject({
    kind: "CORRECT_CASE",
    groups: {
      registration: { mode: "REPLACE", values: { claimantName: "Corrected claimant" } },
      decision: { mode: "KEEP" },
      payment: { mode: "KEEP" },
    },
  });
});
