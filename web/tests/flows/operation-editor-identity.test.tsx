import type { CaseSubmission, CurrentCase } from "../../src/api/service";
import { prefixedInputs } from "../support/submission-inputs";
import {
  caseSubject,
  caseDocumentInput,
  registrationInputs,
  decisionInputs,
  correctionChoices,
} from "../support/case-document.fixtures";
import { operationAffordance } from "../support/affordances.fixtures";
import type { RequestByEndpoint, HumanRejection } from "@contracts/client";

import { requestText, endpointResponse } from "../support/transport-response.fixtures";
import { preview as reviewed } from "../support/operation-hook.fixtures";
import { prepareRequest } from "../../src/protocol/preparedRequest";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { selectFact } from "../support/field-entry";
import { chooseOption } from "../support/choice-entry";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import { getButton, render, screen, waitFor } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";

import { OperationEditor } from "../../src/views/OperationEditor";
import { definition, fields } from "../support/service-ui.fixtures";
type SentDraft = Omit<CaseSubmission, "inputs"> & { inputs: Readonly<Record<string, unknown>> };
const sentDraft = (call: number): SentDraft => {
  const body = vi.mocked(globalThis.fetch).mock.calls[call]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected a synthetic act request body.");
  }
  return JSON.parse(body) as SentDraft;
};
const editor = (
  current: CurrentCase | null,
  initialAct: "case.open" | "case.close" | "case.correct",
) => (
  <OperationEditor
    definition={definition}
    current={current}
    initialAct={initialAct}
    onClose={vi.fn()}
    onCommitted={vi.fn()}
    onMutationLockChange={vi.fn()}
  />
);
const preparedResponse = (_url: RequestInfo | URL, options?: RequestInit): Promise<Response> => {
  const operationPreviewRequest = JSON.parse(
    requestText(options?.body),
  ) as RequestByEndpoint["operation.preview"];
  const reply = reviewed(prepareRequest("operation.execute", operationPreviewRequest));
  if (reply.kind !== "outcome") {
    throw new Error("Current review required");
  }
  return Promise.resolve(endpointResponse("operation.preview", reply.value));
};
const refusedResponse = () =>
  endpointResponse("operation.execute", {
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "REFUSED",
        advice: {
          message: {
            messageId: "diagnostic.IDEMPOTENCY_CONFLICT",
            arguments: [],
          },
          action: "STOP_AND_INVESTIGATE",
        },
      },
    },
  });
const validationRefusal: HumanRejection = {
  kind: "INPUT",
  advice: {
    message: {
      messageId: "diagnostic.INVALID_INPUT",
      arguments: [
        {
          key: "issueCount",
          value: {
            kind: "COUNT",
            value: 1,
          },
        },
      ],
    },
    action: "CORRECT_INPUT",
  },
  issues: [
    {
      input: "field:claimantName",
      row: null,
      related: null,
      message: {
        messageId: "diagnostic.TEXT_REQUIRED",
        arguments: [],
      },
    },
  ],
};
const staleRefusal: HumanRejection = {
  kind: "CONFLICT",
  advice: {
    message: {
      messageId: "diagnostic.REVISION_CONFLICT",
      arguments: [],
    },
    action: "REVIEW_LATEST",
  },
  actualRevision: 2,
  document: {
    screen: {
      regions: [],
    },
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
    basis: {
      kind: "UNAVAILABLE",
    },
    disclosure: "WITHHELD",
  },
};
const rejectedResponse = (rejection: HumanRejection) =>
  endpointResponse("operation.preview", {
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection,
    },
  });
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("allocates a new ID when editing a definitely refused reviewed request", async () => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch)
    .mockImplementationOnce(preparedResponse)
    .mockResolvedValueOnce(refusedResponse())
    .mockResolvedValueOnce(rejectedResponse(validationRefusal));
  render(editor(null, "case.open"));
  fillRegistrationInputs({
    claimantName: "Synthetic A",
  });
  await user.click(getButton("Review changes"));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await screen.findByText(
    "This operation ID was already used for a different change. Use a new ID.",
  );
  selectFact("claimantName");
  const claimant = screen.getByLabelText("Claimant name", {
    exact: true,
  });
  await user.clear(claimant);
  await user.type(claimant, "Synthetic B");
  await user.click(getButton("Review changes"));
  expect(await screen.findByRole("alert")).toHaveTextContent("Fields to check: 1.");
  expect(claimant).toHaveAccessibleDescription(
    "The person or company making the claim. A non-blank value is required.",
  );
  expect(claimant).toHaveAttribute("aria-invalid", "true");
  expect(sentDraft(0).operationId).not.toBe(sentDraft(2).operationId);
  expect(sentDraft(0).inputs["field:claimantName"]).toBe("Synthetic A");
  expect(sentDraft(2).inputs["field:claimantName"]).toBe("Synthetic B");
});
it("re-previews the same authored revision after preview loss despite a changed current observation", async () => {
  const user = userEvent.setup();
  const current: CurrentCase = caseDocumentInput({
    subject: { ...caseSubject, caseId: "20000000-0000-4000-8000-000000000001", revision: 1 },
    actions: [
      operationAffordance(
        "case.close",
        {
          kind: "CASE",
          target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
          expectedRevision: 1,
        },
        {},
        undefined,
        {},
      ),
    ],
    history: "FULL",
  });
  vi.mocked(globalThis.fetch)
    .mockRejectedValueOnce(new Error("Synthetic delivery loss"))
    .mockResolvedValueOnce(rejectedResponse(staleRefusal))
    .mockResolvedValueOnce(
      endpointResponse("case.get", {
        representation: "HUMAN",
        outcome: {
          kind: "SUCCEEDED",
          document: {
            ...current,
            subject: { ...current.subject, revision: 2 },
          },
        },
      }),
    );
  const view = render(editor(current, "case.close"));
  await user.click(getButton("Review changes"));
  await screen.findByText(/could not be reached/u);
  view.rerender(
    editor(
      {
        ...current,
        subject: { ...current.subject, revision: 2 },
      },
      "case.close",
    ),
  );
  await user.click(getButton("Review changes"));
  await waitFor(() => {
    expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(3);
  });
  const { operationId: firstId, ...firstCommand } = sentDraft(0);
  const { operationId: nextId, ...nextCommand } = sentDraft(1);
  expect(nextCommand).toEqual(firstCommand);
  expect(nextId).not.toBe(firstId);
  expect(sentDraft(1).binding.expectedRevision).toBe(1);
  expect(await screen.findByText("This case has changed")).toBeVisible();
});
const paidCase: CurrentCase = caseDocumentInput({
  subject: { ...caseSubject, caseId: "20000000-0000-4000-8000-000000000001", revision: 1 },
  actions: [
    operationAffordance(
      "case.correct",
      {
        kind: "CASE",
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        expectedRevision: 1,
      },
      {
        ...prefixedInputs("field:", fields.registration ?? {}),
        ...prefixedInputs("field:", {
          paymentDecisionDate: "2026-09-03",
          payableAmount: "9.99",
          payableCurrency: "EUR",
        }),
        "group:registration": "KEEP",
        "group:decision": "KEEP",
      },
      [...registrationInputs, ...decisionInputs, "group:registration", "group:decision"],
      {
        "group:registration": correctionChoices.registration,
        "group:decision": correctionChoices.decision,
      },
    ),
  ],
  history: "FULL",
});
it("sends an explicit grouped correction and preserves all non-replaced groups", async () => {
  const user = userEvent.setup();
  const current = paidCase;
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(rejectedResponse(validationRefusal));
  render(editor(current, "case.correct"));
  const registration = document.querySelector<HTMLSelectElement>(
    'fieldset[id="field-group:registration"]',
  );
  if (registration === null) {
    throw new Error("Expected a registration correction selector.");
  }
  await chooseOption(user, registration, "UPDATE");
  selectFact("claimantName");
  const claimant = screen.getByLabelText("Claimant name", {
    exact: true,
  });
  await user.clear(claimant);
  await user.type(claimant, "Corrected claimant");
  await user.click(getButton("Review changes"));
  const body = vi.mocked(globalThis.fetch).mock.calls[0]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected a grouped correction request body.");
  }
  const draft: unknown = JSON.parse(body);
  expect(draft).toMatchObject({
    task: "case.correct",
    inputs: {
      "group:registration": "UPDATE",
      "field:claimantName": "Corrected claimant",
      "group:decision": "KEEP",
    },
  });
});
