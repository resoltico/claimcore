import { type RequestByEndpoint } from "@contracts/client";
import { fields } from "../support/service-ui.fixtures";
import { caseDocumentInput } from "../support/case-document.fixtures";
import { requestText, endpointResponse } from "../support/transport-response.fixtures";
import { reviewConfirm } from "../support/review-controls";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { getButton, render, screen } from "../support/presentation-test-support";
import { editor } from "../support/presentation-state.fixtures";
import { workBasis } from "../support/inputs";
import { taskForCommand } from "../../src/protocol/taskCatalog";
const stateLabels = {
  ACTIVE: "Active",
  VOIDED: "Voided",
  ERASED: "Erased",
};
const stubReview = (
  act: "case.void" | "case.reinstate" | "case.erase",
  before: "ACTIVE" | "VOIDED",
  after: "ACTIVE" | "VOIDED" | "ERASED",
) => {
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation((_url, options) => {
      const operationPreviewRequest = JSON.parse(
        requestText(options?.body),
      ) as RequestByEndpoint["operation.preview"];
      if (operationPreviewRequest.binding.kind !== "CASE" || operationPreviewRequest.task !== act) {
        throw new Error("Typed stewardship command required");
      }
      const task = taskForCommand(operationPreviewRequest);
      return Promise.resolve(
        endpointResponse("operation.preview", {
          representation: "HUMAN",
          outcome: {
            kind: "REVIEWED",
            review: {
              document: {
                screen: {
                  regions: [
                    {
                      role: "FACTS",
                      content: {
                        kind: "ENTRIES",
                        entries: [
                          ...("input:reason" in operationPreviewRequest.inputs
                            ? [
                                {
                                  key: "reason",
                                  labelId: "field.reason.label",
                                  prominence: "PRIMARY",
                                  value: {
                                    kind: "TEXT",
                                    value: operationPreviewRequest.inputs["input:reason"],
                                  },
                                },
                              ]
                            : []),
                          ...(act === "case.erase"
                            ? [
                                {
                                  key: "meaning",
                                  labelId: "label.details",
                                  prominence: "PRIMARY",
                                  value: {
                                    kind: "MESSAGE",
                                    messageId: "task.case.erase.meaning",
                                  },
                                },
                              ]
                            : []),
                        ],
                      },
                    },
                  ],
                },
                context: {
                  kind: "REVIEW",
                  task: task.id,
                  inputs: task.inputs.map((taskInput) => taskInput.id),
                  target: {
                    kind: "CASE",
                    caseId: "20000000-0000-4000-8000-000000000001",
                    beforeRevision: operationPreviewRequest.binding.expectedRevision,
                  },
                  consequence: task.consequence,
                  confirmation: {
                    kind: "CASE_REFERENCE",
                    reference: fields.caseReference,
                  },
                },
                basis: {
                  kind: "SNAPSHOT",
                  observation: workBasis,
                },
                disclosure: "CURRENT",
              },
              presentation: {
                changes: [
                  {
                    key: "recordState",
                    labelId: "input.lifecycle.label",
                    before: {
                      kind: "CODE",
                      vocabulary: "lifecycle",
                      token: before,
                    },
                    after: {
                      kind: "CODE",
                      vocabulary: "lifecycle",
                      token: after,
                    },
                  },
                ],
                unchanged: [],
              },
            },
          },
        }),
      );
    }),
  );
};
it.each([
  {
    act: "case.void",
    label: "Void case record",
    before: "ACTIVE",
    after: "VOIDED",
    reason: "Exact العربية 1.0000",
  },
  {
    act: "case.reinstate",
    label: "Reinstate case record",
    before: "VOIDED",
    after: "ACTIVE",
    reason: "Exact reinstatement",
  },
  {
    act: "case.erase",
    label: "Erase case",
    before: "ACTIVE",
    after: "ERASED",
    reason: null,
  },
] as const)(
  "$act review includes prepared intent and authoritative record transition outside business fields",
  async ({ act, label, before, after, reason }) => {
    stubReview(act, before, after);
    render(
      editor({
        initialAct: act,
        current: caseDocumentInput({
          tasks: [act],
        }),
      }),
    );
    const user = userEvent.setup();
    if (reason !== null) {
      await user.type(
        screen.getByRole("textbox", {
          name: "Reason",
        }),
        reason,
      );
    }
    if (act === "case.erase") {
      expect(screen.getByRole("dialog")).toHaveTextContent(
        "Copies held elsewhere are not affected.",
      );
    }
    await user.click(getButton("Review changes"));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent(label);
    if (reason !== null) {
      expect(dialog).toHaveTextContent(reason);
    }
    expect(dialog.querySelector(".review-before")).toHaveTextContent(stateLabels[before]);
    expect(dialog.querySelector(".review-after")).toHaveTextContent(stateLabels[after]);
    if (act === "case.erase") {
      expect(dialog).toHaveTextContent("Copies held elsewhere are not affected.");
    }
    if (act === "case.erase") {
      expect(reviewConfirm()).toBeDisabled();
      const confirmation = screen.getByRole("textbox", {
        name: "Case reference (exact)",
      });
      await user.type(confirmation, fields.caseReference.toLowerCase());
      expect(reviewConfirm()).toBeDisabled();
      await user.clear(confirmation);
      await user.type(confirmation, fields.caseReference);
    }
    expect(reviewConfirm()).not.toBeDisabled();
    expect(screen.queryByRole("checkbox")).toBeNull();
    expect(vi.mocked(fetch)).toHaveBeenCalledOnce();
  },
);
