import { caseOffers } from "./case-advice.fixtures";
import { fillRegistrationInputs } from "./registration-inputs.fixtures";
import { previewForRequest } from "./preview-request.fixtures";
import { render, screen, waitFor } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase, Diagnostic } from "../src/api/service";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields, response, runtime } from "./service-ui.fixtures";

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

const editor = (current: CurrentCase | null, initialAct: "OPEN" | "CLOSE" | "CORRECT") => (
  <OperationEditor
    token="synthetic-token"
    definition={definition}
    current={current}
    initialAct={initialAct}
    onClose={vi.fn()}
    onCommitted={vi.fn()}
    onMutationLockChange={vi.fn()}
  />
);

const preparedResponse = () =>
  response("operation.preview", "REVIEWED", {
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
  response("operation.execute", "REJECTED", {
    code: "OPERATION_CONTENT_CONFLICT",
    action: "STOP_AND_INVESTIGATE",
    target: null,
    actualRevision: null,
    limits: {},
  });

const validationRefusal: Diagnostic = {
  code: "INPUT_TEXT_REQUIRED",
  action: "CORRECT_INPUT",
  target: "claimantName",
  actualRevision: null,
  limits: {},
};

const staleRefusal: Diagnostic = {
  code: "CASE_REVISION_CONFLICT",
  action: "READ_CURRENT",
  target: null,
  actualRevision: 2,
  limits: {},
};

const rejectedResponse = (rejection: Diagnostic) =>
  response("operation.preview", "REJECTED", rejection);

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
  await screen.findByText("This operation ID belongs to different act content. Do not reuse it.");
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
    advice: { acts: caseOffers(["CLOSE"]), history: ["SUMMARY", "FULL"] },

    state: { tag: "LIVE", fields, disposition: "ACTIVE" },
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
  caseReference: fields.caseReference,
  revision: 1,
  advice: { acts: caseOffers(["CORRECT"]), history: ["SUMMARY", "FULL"] },

  state: {
    tag: "LIVE",
    fields: {
      ...fields,
      decision: {
        paymentDecisionDate: "2026-09-03",
        payableAmount: "9.99",
        payableCurrency: "EUR",
      },
      payment: { paymentDate: "2026-09-04" },
    },
    disposition: "ACTIVE",
  },
};

it("sends an explicit grouped correction and preserves all non-replaced groups", async () => {
  const user = userEvent.setup();
  const current = paidCase;
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(rejectedResponse(validationRefusal));
  render(editor(current, "CORRECT"));
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
    kind: "CORRECT",
    groups: {
      registration: { mode: "REPLACE", values: { claimantName: "Corrected claimant" } },
      decision: { mode: "KEEP" },
      payment: { mode: "KEEP" },
    },
  });
});
