import { allActions } from "../../src/operation/affordances";
import { prefixedInputs } from "../support/submission-inputs";
import { operationAffordance } from "../support/affordances.fixtures";
import {
  caseDocumentInput,
  caseIdentityScreen,
  correctionChoices,
} from "../support/case-document.fixtures";
import {
  taskDefinitions,
  type HumanReceiptDocument,
  type HumanReview,
  type ReviewPresentation,
  type RequestByEndpoint,
} from "@contracts/client";
import { taskForCommand } from "../../src/protocol/taskCatalog";
import { fireEvent } from "@testing-library/react";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { createPresenter } from "../../src/presentation/presenter";
import { defaults } from "../../src/presentation/preferences";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { getButton, render, screen } from "../support/presentation-test-support";
import { OperationEditor } from "../../src/views/OperationEditor";
import { productMetadata } from "../../src/protocol/productMetadata";
import type { CurrentCase } from "../../src/api/service";
import type { ActKind } from "../../src/operation/metadata";
import { caseFields } from "../support/service-foundation.fixtures";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import { runtime, input, response, type Dispatch } from "../support/catalog-transport.fixtures";
afterEach(() => vi.unstubAllGlobals());
const observation = {
  observedAt: runtime.observedAt,
  businessDate: runtime.businessDate,
  timeZone: runtime.timeZone,
};
type CaseInput = Extract<RequestByEndpoint["operation.execute"], { binding: { kind: "CASE" } }>;
const acts = taskDefinitions.flatMap((task) =>
  task.kind === "OPERATION" && task.subject === "CASE"
    ? [
        {
          kind: task.act,
        },
      ]
    : [],
);
const currentFor = (kind: ActKind): CurrentCase => {
  const seedsByAct: Partial<Record<ActKind, Record<string, unknown>>> = {
    "case.correct": {
      ...prefixedInputs("field:", caseFields.registration),
      "group:registration": "KEEP",
      "group:decision": "KEEP",
      "field:paymentDecisionDate": "2026-09-03",
      "field:payableAmount": "5",
      "field:payableCurrency": "EUR",
    },
    "case.amendRegistration": prefixedInputs("field:", caseFields.registration),
    "case.recordPayment": { "field:paidCurrency": "EUR" },
  };
  const action = operationAffordance(
    kind,
    {
      kind: "CASE",
      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
      expectedRevision: 1,
      ...(kind === "case.removePayment"
        ? { paymentId: "10000000-0000-7000-8000-000000000003" }
        : {}),
    },
    seedsByAct[kind] ?? {},
    undefined,
    kind === "case.correct"
      ? {
          "group:registration": correctionChoices.registration,
          "group:decision": correctionChoices.decision,
        }
      : {},
  );
  const suppliedScreen =
    kind === "case.removePayment"
      ? {
          regions: [
            ...caseIdentityScreen.regions,
            {
              role: "PAYMENTS" as const,
              content: {
                kind: "TABLE" as const,
                columns: [
                  {
                    key: "paidAmount",
                    labelId: "field.paidAmount.label",
                    prominence: "PRIMARY" as const,
                    isMoney: true,
                  },
                ],
                rows: [
                  {
                    key: "10000000-0000-7000-8000-000000000003",
                    actions: [action],
                    values: [
                      {
                        kind: "MONEY" as const,
                        amount: "5",
                        currency: "EUR",
                        displayScale: 2,
                      },
                    ],
                  },
                ],
              },
            },
          ],
        }
      : caseIdentityScreen;
  return caseDocumentInput(
    { actions: kind === "case.removePayment" ? [] : [action] },
    suppliedScreen,
  );
};
const authorCase = (kind: ActKind) => {
  switch (kind) {
    case "case.open":
      fillRegistrationInputs();
      break;
    case "case.amendRegistration":
      input("claimantName", "Synthetic replacement");
      break;
    case "case.correct":
      fireEvent.click(document.querySelector('input[name="group:registration"][value="UPDATE"]')!);
      input("claimantName", "Synthetic correction");
      break;
    case "case.decide":
      for (const [name, value] of Object.entries({
        paymentDecisionDate: "2026-09-03",
        payableAmount: "5.0000",
        payableCurrency: "EUR",
      })) {
        input(name, value);
      }
      break;
    case "case.recordPayment":
      input("paymentDate", "2026-09-04");
      input("paidAmount", "5");
      break;
    case "case.removePayment":
      break;
    case "case.void":
    case "case.reinstate":
      input("input:reason", "Synthetic reason العربية");
      break;
    default:
      break;
  }
};
const caseReceipt = (request: CaseInput): HumanReceiptDocument => {
  const caseId =
    request.task === "case.open" ? request.operationId : "20000000-0000-4000-8000-000000000001";
  return {
    screen: {
      regions: [],
    },
    subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
    actions: [],
    links: [
      {
        rel: "OPEN_CASE",
        labelId: "field.caseReference.label",
        link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
      },
      {
        rel: "FOLLOW_UP",
        labelId: "field.caseReference.label",
        link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
      },
    ],
    history: null,
    nextCursor: null,
    basis: {
      kind: "SNAPSHOT",
      observation,
    },
    disclosure: "WITHHELD",
  };
};
const presentationChanges = (request: CaseInput): ReviewPresentation["changes"] => {
  if (request.task === "case.decide") {
    return [
      {
        key: "payable",
        labelId: "field.payableAmount.label",
        before: {
          kind: "ABSENT",
          reasonId: "label.notRecorded",
        },
        after: {
          kind: "MONEY",
          amount: "5",
          currency: "EUR",
          displayScale: 4,
        },
      },
    ];
  }
  if (request.task === "case.correct") {
    return [
      {
        key: "claimantName",
        labelId: "field.claimantName.label",
        before: {
          kind: "PARTY",
          value: caseFields.registration.claimantName,
        },
        after: {
          kind: "PARTY",
          value: "Synthetic correction",
        },
      },
    ];
  }
  return [];
};
const suppliedPresentation = (request: CaseInput): ReviewPresentation => ({
  changes: presentationChanges(request),
  unchanged: [],
});
const caseReview = (request: CaseInput) => {
  const task = taskForCommand(request);
  const review: HumanReview = {
    document: {
      screen: {
        regions: [],
      },
      subject: {
        kind: "REVIEW",
        confirmation: {
          kind: "CASE_REFERENCE",
          reference: caseFields.caseReference,
        },
        consequence: task.consequence,
      },
      actions: [
        operationAffordance(
          task.id,
          {
            kind: "CASE",
            target: {
              kind: "ID",
              caseId:
                request.task === "case.open"
                  ? request.operationId
                  : "20000000-0000-4000-8000-000000000001",
            },
            expectedRevision: request.binding.expectedRevision,
          },
          {},
          [],
        ),
      ],
      links: [],
      history: null,
      nextCursor: null,
      basis: {
        kind: "SNAPSHOT",
        observation,
      },
      disclosure: "CURRENT",
    },
    presentation: suppliedPresentation(request),
  };
  return {
    representation: "HUMAN" as const,
    operationId: request.operationId,
    outcome: {
      kind: "REVIEWED" as const,
      review,
    },
  };
};
const caseTransport = (calls: Dispatch<CaseInput>[]) =>
  vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    if (typeof path !== "string" || typeof init?.body !== "string") {
      throw new Error("Expected a typed case dispatch.");
    }
    const operationExecuteRequest = JSON.parse(init.body) as CaseInput;
    if (operationExecuteRequest.binding.kind !== "CASE") {
      throw new Error("Production editor did not produce a complete case request.");
    }
    calls.push({
      path,
      bytes: init.body,
      input: operationExecuteRequest,
    });
    if (path === "/api/operation.preview") {
      return Promise.resolve(response("operation.preview", caseReview(operationExecuteRequest)));
    }
    if (path !== "/api/operation.execute") {
      throw new Error("Unexpected case endpoint.");
    }
    return Promise.resolve(
      response("operation.execute", {
        representation: "HUMAN",
        operationId: operationExecuteRequest.operationId,
        outcome: {
          kind: "ACCEPTED",
          document: caseReceipt(operationExecuteRequest),
        },
      }),
    );
  });
const assertAuthoredCase = (request: CaseInput) => {
  switch (request.task) {
    case "case.open":
      expect(request.binding.target).toEqual({
        kind: "REFERENCE",
        caseReference: caseFields.caseReference,
      });
      expect(request.inputs["field:claimantName"]).toBe(caseFields.registration.claimantName);
      break;
    case "case.amendRegistration":
      expect(request.inputs["field:claimantName"]).toBe("Synthetic replacement");
      break;
    case "case.decide":
      expect(request.inputs["field:payableAmount"]).toBe("5.0000");
      break;
    case "case.recordPayment":
      expect(request.inputs["field:paymentDate"]).toBe("2026-09-04");
      break;
    case "case.removePayment":
      expect(request.binding.paymentId).toBe("10000000-0000-7000-8000-000000000003");
      break;
    case "case.correct":
      expect(request.inputs).toMatchObject({
        "group:registration": "UPDATE",
        "field:claimantName": "Synthetic correction",
        "group:decision": "KEEP",
      });
      break;
    case "case.void":
    case "case.reinstate":
      expect(request.inputs["input:reason"]).toBe("Synthetic reason العربية");
      break;
    default:
      break;
  }
};
const assertInputMetadata = () => {
  expect(
    productMetadata.catalog.inputFields.some(({ name }) => name === "target" || name === "act"),
  ).toBe(false);
};
it.each(acts)(
  "renders, reviews and submits real case act $kind through production transport",
  async (act) => {
    expect(acts).toHaveLength(
      taskDefinitions.filter((task) => task.kind === "OPERATION" && task.subject === "CASE").length,
    );
    assertInputMetadata();
    const calls: Dispatch<CaseInput>[] = [];
    vi.stubGlobal("fetch", caseTransport(calls));
    render(
      <OperationEditor
        definition={productMetadata}
        current={act.kind === "case.open" ? null : currentFor(act.kind)}
        initialAct={act.kind}
        {...(act.kind === "case.removePayment"
          ? {
              affordance: allActions(currentFor(act.kind)).find(
                (action) => action.task === "case.removePayment",
              )!,
              binding: {
                kind: "CASE" as const,
                target: {
                  kind: "ID" as const,
                  caseId: "20000000-0000-4000-8000-000000000001",
                },
                expectedRevision: 1,
                paymentId: "10000000-0000-7000-8000-000000000003",
              },
            }
          : {})}
        onClose={vi.fn()}
        onCommitted={vi.fn()}
        onMutationLockChange={vi.fn()}
      />,
    );
    expect(
      screen.getByRole("heading", {
        name: createPresenter(defaults).actLabel(act.kind),
      }),
    ).toBeVisible();
    authorCase(act.kind);
    const user = userEvent.setup();
    await user.click(getButton("Review changes"));
    const dialog = await screen.findByRole("dialog");
    if (act.kind === "case.decide") {
      expect(dialog).toHaveTextContent("5.0000");
      expect([...dialog.querySelectorAll("bdi")].map((node) => node.textContent)).toContain(
        "5.0000 EUR",
      );
    }
    if (act.kind === "case.correct") {
      expect(dialog).toHaveTextContent("Synthetic correction");
    }
    await waitForReviewConfirm();
    if (act.kind === "case.erase") {
      await user.type(
        screen.getByRole("textbox", {
          name: "Case reference (exact)",
        }),
        caseFields.caseReference,
      );
    }
    await user.click(reviewConfirm());
    await screen.findByRole("heading", {
      name: "Recorded change",
    });
    expect(calls.map((call) => call.path)).toEqual([
      "/api/operation.preview",
      "/api/operation.execute",
    ]);
    expect(calls[1]!.bytes).toBe(calls[0]!.bytes);
    expect(calls[1]!.input.task).toBe(act.kind);
    expect(calls[1]!.input.binding.expectedRevision).toBe(act.kind === "case.open" ? 0 : 1);
    assertAuthoredCase(calls[1]!.input);
  },
);
