import { caseAdvice } from "./case-advice.fixtures";
import { fillRegistrationInputs } from "./registration-inputs.fixtures";
import { previewForRequest } from "./preview-request.fixtures";
import { getButton, render, screen, waitFor } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase, Diagnostic } from "../src/api/service";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields, response, runtime } from "./service-ui.fixtures";
type SentDraft = {
  operationId: string;
  caseReference: string;
  expectedRevision: number;
  act: {
    kind: string;
    values: Record<string, string>;
  };
};
const sentDraft = (call: number): SentDraft => {
  const body = vi.mocked(globalThis.fetch).mock.calls[call]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected a synthetic act request body.");
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
      subject: {
        kind: "CASE",
        before: null,
        after: {
          caseReference: fields.caseReference,
          revision: 1,
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
const refusedResponse = () =>
  response("operation.execute", "REJECTED", {
    code: "IDEMPOTENCY_CONFLICT",
    action: "STOP_AND_INVESTIGATE",
  });
const validationRefusal: Diagnostic = {
  code: "TEXT_REQUIRED",
  action: "CORRECT_INPUT",
  target: "claimantName",
};
const staleRefusal: Diagnostic = {
  code: "REVISION_CONFLICT",
  action: "READ_CURRENT",
  actualRevision: 2,
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
  fillRegistrationInputs({
    claimantName: "Synthetic A",
  });
  await user.click(getButton("Review changes"));
  await user.click(
    await screen.findByRole("checkbox", {
      name: "I confirm these changes.",
    }),
  );
  await user.click(getButton("Record changes"));
  await screen.findByText("This operation ID belongs to different act content. Do not reuse it.");
  const claimant = screen.getByLabelText("Claimant name", {
    exact: true,
  });
  await user.clear(claimant);
  await user.type(claimant, "Synthetic B");
  await user.click(getButton("Review changes"));
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
    advice: {
      ...caseAdvice(["CLOSE"]),
      history: ["SUMMARY", "FULL"],
    },
    state: {
      kind: "ACTIVE",
      fields,
    },
  };
  vi.mocked(globalThis.fetch)
    .mockRejectedValueOnce(new Error("Synthetic delivery loss"))
    .mockResolvedValueOnce(rejectedResponse(staleRefusal));
  const view = render(editor(current, "CLOSE"));
  await user.click(getButton("Review changes"));
  await screen.findByText(/could not be reached/u);
  view.rerender(
    editor(
      {
        ...current,
        revision: 2,
      },
      "CLOSE",
    ),
  );
  await user.click(getButton("Review changes"));
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
  advice: {
    ...caseAdvice(["CORRECT"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields: {
      ...fields,
      decision: {
        paymentDecisionDate: "2026-09-03",
        payableAmount: "9.99",
        payableCurrency: "EUR",
      },
      payment: {
        paymentDate: "2026-09-04",
      },
    },
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
    act: {
      kind: "CORRECT",
      groups: {
        registration: {
          kind: "REPLACE",
          values: {
            claimantName: "Corrected claimant",
          },
        },
        decision: {
          kind: "KEEP",
        },
        payment: {
          kind: "KEEP",
        },
      },
    },
  });
});
