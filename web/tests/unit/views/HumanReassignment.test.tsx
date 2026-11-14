import {
  type RequestByEndpoint,
  type HumanReadDocument_case_get,
  type HumanReview,
  type HumanReceiptDocument,
} from "@contracts/client";
import { requestAddress, requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { CaseReassignment } from "../../../src/views/worklist/CaseReassignment";
const caseId = "00000000-0000-4000-8000-000000000001";
const replacementActorId = "00000000-0000-4000-8000-000000000003";
const actorId = "00000000-0000-4000-8000-000000000002";
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const current: HumanReadDocument_case_get = {
  screen: {
    regions: [],
  },
  context: {
    kind: "CASE",
    caseId,
    revision: 7,
    workRevision: 12,
    history: "NONE",
    tasks: ["work.update"],
    editableInputs: ["field:responsibleActor"],
    correction: null,
    seeds: {
      registration: null,
      decision: null,
      work: {
        responsible: actorId,
        summary: null,
      },
      paymentCurrency: null,
    },
    notes: [],
    payments: [],
    nextActions: [],
  },
  basis,
  disclosure: "CURRENT",
};
const review: HumanReview = {
  presentation: {
    changes: [],
    unchanged: [],
  },
  document: {
    screen: {
      regions: [],
    },
    context: {
      kind: "REVIEW",
      task: "work.update",
      target: {
        kind: "WORK",
        caseId,
        beforeRevision: 12,
      },
      inputs: [
        "input:operationId",
        "field:caseReference",
        "input:expectedWorkRevision",
        "field:responsibleActor",
        "field:handlingSummary",
      ],
      consequence: "ROUTINE",
      confirmation: {
        kind: "CASE_REFERENCE",
        reference: "CORE-CASE",
      },
    },
    basis,
    disclosure: "CURRENT",
  },
};
const receipt: HumanReceiptDocument = {
  screen: {
    regions: [],
  },
  context: {
    kind: "RECEIPT",
    recordedFields: [],
    target: {
      kind: "WORK",
      caseId,
      revision: 13,
    },
    caseLink: caseId,
    refreshCase: caseId,
  },
  basis,
  disclosure: "CURRENT",
};
const response = (outcome: unknown, operationId?: string) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      ...(operationId === undefined
        ? {}
        : {
            operationId,
          }),
      outcome,
    }),
    {
      status: 200,
      headers: {
        "Content-Type": "application/json",
      },
    },
  );
afterEach(() => vi.unstubAllGlobals());
it("keeps the selected revision until explicit rebase and retains the new basis through editing and fresh consent", async () => {
  const previews: {
    bytes: string;
    operationId: string;
  }[] = [];
  const executions: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((url, options) =>
      Promise.try(() => {
        if (requestAddress(url).endsWith("case.get")) {
          return response({
            kind: "SUCCEEDED",
            document: current,
          });
        }
        if (requestAddress(url).endsWith("case.assignees")) {
          return response({
            kind: "SUCCEEDED",
            document: {
              screen: {
                regions: [
                  {
                    role: "RESULTS",
                    content: {
                      kind: "TABLE",
                      columns: [
                        {
                          key: "actor",
                          labelId: "label.details",
                          prominence: "PRIMARY",
                          isMoney: false,
                        },
                      ],
                      rows: [
                        {
                          key: actorId,
                          values: [
                            {
                              kind: "TEXT",
                              value: "Core assignee Ω",
                            },
                          ],
                        },
                        {
                          key: replacementActorId,
                          values: [
                            {
                              kind: "TEXT",
                              value: "Core replacement assignee Σ",
                            },
                          ],
                        },
                      ],
                    },
                  },
                ],
              },
              context: {
                kind: "ASSIGNEES",
                actorIds: [actorId, replacementActorId],
                nextCursor: null,
              },
              basis,
              disclosure: "CURRENT",
            },
          });
        }
        const bytes = requestText(options?.body);
        const command: unknown = JSON.parse(bytes);
        if (requestAddress(url).endsWith("preview")) {
          const operationExecuteRequest = command as RequestByEndpoint["operation.execute"];
          previews.push({
            bytes,
            operationId: operationExecuteRequest.operationId,
          });
          return previews.length === 1
            ? response(
                {
                  kind: "REJECTED",
                  rejection: {
                    kind: "CONFLICT",
                    actualRevision: 12,
                    advice: {
                      message: {
                        messageId: "ui.conflict.title",
                        arguments: [],
                      },
                      action: "REVIEW_LATEST",
                    },
                    document: {
                      screen: {
                        regions: [],
                      },
                      context: {
                        kind: "EMPTY",
                      },
                      basis: {
                        kind: "UNAVAILABLE",
                      },
                      disclosure: "WITHHELD",
                    },
                  },
                },
                operationExecuteRequest.operationId,
              )
            : response(
                {
                  kind: "REVIEWED",
                  review,
                },
                operationExecuteRequest.operationId,
              );
        }
        executions.push(bytes);
        const operationExecuteRequest = command as RequestByEndpoint["operation.execute"];
        return response(
          {
            kind: "ACCEPTED",
            document: receipt,
          },
          operationExecuteRequest.operationId,
        );
      }),
    ),
  );
  const recorded = vi.fn();
  render(
    <CaseReassignment
      selection={{
        row: {
          caseId,
          assignment: {
            revision: 3,
            responsibleSeed: actorId,
          },
        },
        readId: "selection",
      }}
      disclosureAvailable
      close={vi.fn()}
      recorded={recorded}
      onLockChange={vi.fn()}
      refused={vi.fn()}
    />,
  );
  expect(
    await screen.findByRole("radio", {
      name: "Core assignee Ω",
    }),
  ).toBeChecked();
  await userEvent.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  const rebase = await screen.findByRole("button", {
    name: /latest/iu,
  });
  await waitFor(() => {
    expect(rebase).toBeEnabled();
  });
  expect(JSON.parse(previews[0]!.bytes)).toMatchObject({
    task: "work.update",
    binding: {
      kind: "WORK",
      target: {
        kind: "ID",
        caseId,
      },
      expectedRevision: 3,
    },
    inputs: { "field:responsibleActor": actorId },
  });
  expect(executions).toEqual([]);
  await userEvent.click(rebase);
  await waitFor(() => {
    expect(screen.getByRole("dialog").querySelector("[data-confirm-operation]")).not.toBeNull();
  });
  expect(previews).toHaveLength(2);
  expect(previews[1]!.operationId).not.toBe(previews[0]!.operationId);
  expect(JSON.parse(previews[1]!.bytes)).toMatchObject({
    task: "work.update",
    binding: {
      kind: "WORK",
      target: {
        kind: "ID",
        caseId,
      },
      expectedRevision: 12,
    },
    inputs: { "field:responsibleActor": actorId },
  });
  expect(executions).toEqual([]);
  await userEvent.click(
    screen.getByRole("button", {
      name: "Return to editing",
    }),
  );
  await userEvent.click(
    await screen.findByRole("radio", {
      name: "Core replacement assignee Σ",
    }),
  );
  await userEvent.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await waitFor(() => {
    expect(previews).toHaveLength(3);
  });
  expect(JSON.parse(previews[2]!.bytes)).toMatchObject({
    expectedRevision: 12,
    target: {
      kind: "ID",
      caseId,
    },
    act: {
      values: {
        responsibleActor: {
          kind: "SET",
          value: replacementActorId,
        },
      },
    },
  });
  expect(previews[2]!.operationId).not.toBe(previews[1]!.operationId);
  expect(executions).toEqual([]);
  await userEvent.click(
    screen.getByRole("dialog").querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await waitFor(() => {
    expect(recorded).toHaveBeenCalledExactlyOnceWith(receipt, previews[2]!.operationId);
  });
  expect(executions).toEqual([previews[2]!.bytes]);
});
