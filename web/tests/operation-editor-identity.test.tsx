import { fillRegistrationInputs } from "./registration-inputs.fixtures";
import { previewForRequest } from "./preview-request.fixtures";
import { render, screen, waitFor } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase, Rejection } from "../src/api/service";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields, digest, response, runtime } from "./service-ui.fixtures";

type SentDraft = {
  operationId: string;
  caseReference: string;
  expectedRevision: number;
  act: { kind: string; values: Record<string, string> };
};

const sentDraft = (call: number): SentDraft => {
  const body = vi.mocked(globalThis.fetch).mock.calls[call]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected a synthetic command request body.");
  }
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
  response("case.preview", "REVIEWED", {
    requestSha256: digest,
    review: {
      before: null,
      after: {
        caseReference: fields.caseReference,
        revision: 1,
        state: { tag: "LIVE", disposition: "ACTIVE", fields },
      },
      changes: [],
      context: runtime,
    },
  });

const refusedResponse = () =>
  response("case.execute", "REJECTED", {
    diagnostic: { id: "OPERATION_CONTENT_CONFLICT", parameters: {} },
    message: "Synthetic conflict",
    field: null,
    actualRevision: null,
    recommendedAction: "STOP_AND_INVESTIGATE",
  });

const validationRefusal: Rejection = {
  message: "Synthetic validation refusal.",
  diagnostic: { id: "INPUT_TEXT_REQUIRED", parameters: {} },
  field: "claimantName",
  actualRevision: null,
  recommendedAction: "CORRECT_INPUT",
};

const staleRefusal: Rejection = {
  message: "Synthetic stale revision.",
  diagnostic: { id: "CASE_REVISION_CONFLICT", parameters: {} },
  field: null,
  actualRevision: 2,
  recommendedAction: "READ_CURRENT",
};

const rejectedResponse = (rejection: Rejection) => response("case.preview", "REJECTED", rejection);

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("allocates a new ID when editing a definitely refused reviewed request", async () => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch)
    .mockImplementationOnce(previewForRequest(preparedResponse()))
    .mockResolvedValueOnce(refusedResponse())
    .mockResolvedValueOnce(rejectedResponse(validationRefusal));
  render(editor(null, "OPEN"));
  fillRegistrationInputs({ claimantName: "Synthetic A" });
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  await screen.findByText(
    "This operation ID belongs to different command content. Do not reuse it.",
  );
  const claimant = screen.getByLabelText("Claimant name", { exact: true });
  await user.clear(claimant);
  await user.type(claimant, "Synthetic B");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("A non-blank value is required.");
  expect(sentDraft(0).operationId).not.toBe(sentDraft(2).operationId);
  expect(sentDraft(0).act.values["claimantName"]).toBe("Synthetic A");
  expect(sentDraft(2).act.values["claimantName"]).toBe("Synthetic B");
});

it("retries the frozen request after delivery loss despite a changed current revision", async () => {
  const user = userEvent.setup();
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
  vi.mocked(globalThis.fetch)
    .mockRejectedValueOnce(new Error("Synthetic delivery loss"))
    .mockResolvedValueOnce(rejectedResponse(staleRefusal));
  const view = render(editor(current, "CLOSE"));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByText(/could not be reached/u);
  view.rerender(editor({ ...current, revision: 2 }, "CLOSE"));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(2);
  });
  expect(sentDraft(1)).toEqual(sentDraft(0));
  expect(sentDraft(1).expectedRevision).toBe(1);
  expect(
    await screen.findByText("Read the current case before making a changed request."),
  ).toBeVisible();
});

const paidCase: CurrentCase = {
  caseReference: {
    ...fields,
    paymentDecisionDate: "2026-09-03",
    payableAmount: "9.99",
    payableCurrency: "EUR",
    paymentDate: "2026-09-04",
  }.caseReference,
  revision: 1,
  offers: [{ definition: "CORRECT_CASE", caseReference: null, expectedRevision: 1, choices: [] }],
  historyDetails: ["SUMMARY", "FULL"],
  state: {
    tag: "LIVE",
    fields: {
      ...fields,
      paymentDecisionDate: "2026-09-03",
      payableAmount: "9.99",
      payableCurrency: "EUR",
      paymentDate: "2026-09-04",
    },
    disposition: "ACTIVE",
    availableActs: ["CORRECT_CASE"],
  },
};

it("sends an explicit grouped correction and preserves all non-replaced groups", async () => {
  const user = userEvent.setup();
  const current = paidCase;
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(rejectedResponse(validationRefusal));
  render(editor(current, "CORRECT_CASE"));
  const registration = document.querySelector<HTMLSelectElement>("#correction-registration-mode");
  if (registration === null) {
    throw new Error("Expected a registration correction selector.");
  }
  await user.selectOptions(registration, "REPLACE");
  const claimant = screen.getByLabelText("Claimant name", { exact: true });
  await user.clear(claimant);
  await user.type(claimant, "Corrected claimant");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  const body = vi.mocked(globalThis.fetch).mock.calls[0]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected a grouped correction request body.");
  }
  const draft = JSON.parse(body) as {
    act: {
      kind: string;
      groups: {
        registration: { mode: string; values: { claimantName: string } };
        decision: { mode: string };
        payment: { mode: string };
      };
    };
  };
  expect(draft.act).toMatchObject({
    kind: "CORRECT_CASE",
    groups: {
      registration: { mode: "REPLACE", values: { claimantName: "Corrected claimant" } },
      decision: { mode: "KEEP" },
      payment: { mode: "KEEP" },
    },
  });
});
