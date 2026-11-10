import {
  caseDocumentInput,
  caseIdentityScreen,
  emptyCaseSeeds,
  registrationInputs,
  decisionInputs,
  correctionChoices,
} from "../support/case-document.fixtures";
import { taskDefinitions, type HumanReceiptDocument, type HumanReview } from "@contracts/client";
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
import { isRequest } from "../../src/protocol/client";
import type { ReviewPresentation, RequestByEndpoint } from "@contracts/client";
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
type CaseInput = Extract<
  RequestByEndpoint["operation.execute"],
  {
    kind: "CASE";
  }
>;
const acts = taskDefinitions.flatMap((task) =>
  task.kind === "OPERATION" && task.subject === "CASE" ? [{ kind: task.act }] : [],
);
const currentFor = (kind: ActKind): CurrentCase => {
  const scenarios: Partial<Record<ActKind, Partial<CurrentCase["context"]>>> = {
    AMEND_REGISTRATION: {
      editableInputs: registrationInputs,
      seeds: { ...emptyCaseSeeds, registration: caseFields.registration },
    },
    DECIDE: { editableInputs: decisionInputs },
    RECORD_PAYMENT: { seeds: { ...emptyCaseSeeds, paymentCurrency: "EUR" } },
    CORRECT: {
      editableInputs: [...registrationInputs, ...decisionInputs],
      correction: correctionChoices,
      seeds: {
        ...emptyCaseSeeds,
        registration: caseFields.registration,
        decision: { paymentDecisionDate: "2026-09-03", payableAmount: "5", payableCurrency: "EUR" },
      },
    },
    REMOVE_PAYMENT: {
      payments: [{ paymentId: "10000000-0000-7000-8000-000000000003", canRemove: true }],
    },
  };
  const suppliedScreen =
    kind === "REMOVE_PAYMENT"
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
                    values: [
                      { kind: "MONEY" as const, amount: "5", currency: "EUR", displayScale: 2 },
                    ],
                  },
                ],
              },
            },
          ],
        }
      : caseIdentityScreen;
  return caseDocumentInput({ tasks: [`CASE:${kind}`], ...scenarios[kind] }, suppliedScreen);
};
const authorCase = (kind: ActKind) => {
  switch (kind) {
    case "OPEN":
      fillRegistrationInputs();
      break;
    case "AMEND_REGISTRATION":
      input("claimantName", "Synthetic replacement");
      break;
    case "CORRECT":
      fireEvent.click(document.querySelector('input[name="group:registration"][value="UPDATE"]')!);
      input("claimantName", "Synthetic correction");
      break;
    case "DECIDE":
      for (const [name, value] of Object.entries({
        paymentDecisionDate: "2026-09-03",
        payableAmount: "5.0000",
        payableCurrency: "EUR",
      })) {
        input(name, value);
      }
      break;
    case "RECORD_PAYMENT":
      input("paymentDate", "2026-09-04");
      input("paidAmount", "5");
      break;
    case "REMOVE_PAYMENT":
      break;
    case "VOID":
    case "REINSTATE":
      input("input:reason", "Synthetic reason العربية");
      break;
    default:
      break;
  }
};
const caseReceipt = (request: CaseInput): HumanReceiptDocument => {
  const caseId =
    request.act.kind === "OPEN" ? request.operationId : "20000000-0000-4000-8000-000000000001";
  return {
    screen: { regions: [] },
    context: {
      kind: "RECEIPT",
      target: { kind: "CASE", caseId, revision: request.expectedRevision + 1 },
      refreshCase: caseId,
    },
    basis: { kind: "SNAPSHOT", observation },
    disclosure: "WITHHELD",
  };
};
const presentationChanges = (request: CaseInput): ReviewPresentation["changes"] => {
  if (request.act.kind === "DECIDE") {
    return [
      {
        key: "payable",
        labelId: "field.payableAmount.label",
        before: { kind: "ABSENT", reasonId: "label.notRecorded" },
        after: { kind: "MONEY", amount: "5", currency: "EUR", displayScale: 4 },
      },
    ];
  }
  if (request.act.kind === "CORRECT") {
    return [
      {
        key: "claimantName",
        labelId: "field.claimantName.label",
        before: { kind: "PARTY", value: caseFields.registration.claimantName },
        after: { kind: "PARTY", value: "Synthetic correction" },
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
      screen: { regions: [] },
      context: {
        kind: "REVIEW",
        task: task.id,
        inputs: task.inputs.map((taskInput) => taskInput.id),
        consequence: task.consequence,
        target: {
          kind: "CASE",
          caseId:
            request.act.kind === "OPEN"
              ? request.operationId
              : "20000000-0000-4000-8000-000000000001",
          beforeRevision: request.act.kind === "OPEN" ? null : request.expectedRevision,
        },
        confirmation: { kind: "CASE_REFERENCE", reference: caseFields.caseReference },
      },
      basis: { kind: "SNAPSHOT", observation },
      disclosure: "CURRENT",
    },
    presentation: suppliedPresentation(request),
  };
  return {
    representation: "HUMAN" as const,
    operationId: request.operationId,
    outcome: { kind: "REVIEWED" as const, review },
  };
};
const caseTransport = (calls: Dispatch<CaseInput>[]) =>
  vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    if (typeof path !== "string" || typeof init?.body !== "string") {
      throw new Error("Expected a typed case dispatch.");
    }
    const parsed: unknown = JSON.parse(init.body);
    if (!isRequest("operation.execute", parsed) || parsed.kind !== "CASE") {
      throw new Error("Production editor did not produce a complete case request.");
    }
    calls.push({
      path,
      bytes: init.body,
      input: parsed,
    });
    if (path === "/api/operation.preview") {
      return Promise.resolve(response("operation.preview", caseReview(parsed)));
    }
    if (path !== "/api/operation.execute") {
      throw new Error("Unexpected case endpoint.");
    }
    return Promise.resolve(
      response("operation.execute", {
        representation: "HUMAN",
        operationId: parsed.operationId,
        outcome: {
          kind: "ACCEPTED",
          document: caseReceipt(parsed),
        },
      }),
    );
  });
const assertAuthoredCase = (request: CaseInput) => {
  switch (request.act.kind) {
    case "OPEN":
      expect(request.act.values.claimantName).toBe(caseFields.registration.claimantName);
      break;
    case "AMEND_REGISTRATION":
      expect(request.act.values.claimantName).toBe("Synthetic replacement");
      break;
    case "DECIDE":
      expect(request.act.values.payableAmount).toBe("5.0000");
      break;
    case "RECORD_PAYMENT":
      expect(request.act.values.paymentDate).toBe("2026-09-04");
      break;
    case "REMOVE_PAYMENT":
      expect(request.act.paymentId).toBe("10000000-0000-7000-8000-000000000003");
      break;
    case "CORRECT":
      expect(request.act.groups).toMatchObject({
        registration: {
          kind: "UPDATE",
          values: {
            claimantName: "Synthetic correction",
          },
        },
        decision: {
          kind: "KEEP",
        },
      });
      break;
    case "VOID":
    case "REINSTATE":
      expect(request.act.reason).toBe("Synthetic reason العربية");
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
        current={act.kind === "OPEN" ? null : currentFor(act.kind)}
        initialAct={act.kind}
        {...(act.kind === "REMOVE_PAYMENT"
          ? {
              binding: {
                kind: "CASE" as const,
                target: { kind: "ID" as const, caseId: "20000000-0000-4000-8000-000000000001" },
                revision: 1,
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
    if (act.kind === "DECIDE") {
      expect(dialog).toHaveTextContent("5.0000");
      expect([...dialog.querySelectorAll("bdi")].map((node) => node.textContent)).toContain(
        "5.0000 EUR",
      );
    }
    if (act.kind === "CORRECT") {
      expect(dialog).toHaveTextContent("Synthetic correction");
    }
    await waitForReviewConfirm();
    if (act.kind === "ERASE") {
      await user.type(
        screen.getByRole("textbox", { name: "Case reference (exact)" }),
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
    expect(calls[1]!.input.act.kind).toBe(act.kind);
    expect(calls[1]!.input.expectedRevision).toBe(act.kind === "OPEN" ? 0 : 1);
    assertAuthoredCase(calls[1]!.input);
  },
);
