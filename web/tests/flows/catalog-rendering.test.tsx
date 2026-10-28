import { inventory } from "../support/inventory";
import { createPresenter } from "../../src/presentation/presenter";
import { defaults } from "../../src/presentation/preferences";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { getButton, fireEvent, render, screen } from "../support/presentation-test-support";
import { OperationEditor } from "../../src/views/OperationEditor";
import { productMetadata } from "../../src/protocol/productMetadata";
import { isRequest } from "../../src/protocol/client";
import type { RequestByEndpoint } from "@contracts/client";
import type { CurrentCase, CaseReview } from "../../src/api/service";
import type { ActKind } from "../../src/operation/metadata";
import { caseAdvice } from "../support/case-advice.fixtures";
import { wireReply } from "../support/contract-corpus.fixtures";
import { caseFields } from "../support/service-foundation.fixtures";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import { runtime, input, response, type Dispatch } from "../support/catalog-transport.fixtures";
afterEach(() => vi.unstubAllGlobals());
type CaseInput = Extract<
  RequestByEndpoint["operation.execute"],
  {
    kind: "CASE";
  }
>;
const acts = productMetadata.catalog.entries
  .filter((entry) => entry.endpoint === "operation.execute" && entry.subject === "CASE")
  .map((entry) => entry as typeof entry & { kind: ActKind });
const currentFor = (kind: ActKind): CurrentCase => {
  const decided = ["WITHDRAW_DECISION", "RECORD_PAYMENT", "CLEAR_PAYMENT", "CORRECT"].includes(
    kind,
  );
  const paid = kind === "CLEAR_PAYMENT" || kind === "CORRECT";
  return {
    caseReference: caseFields.caseReference,
    revision: 1,
    advice: {
      ...caseAdvice([kind]),
      history: ["SUMMARY", "FULL"],
    },
    state: {
      kind: kind === "REINSTATE" ? "VOIDED" : "ACTIVE",
      fields: {
        ...caseFields,
        status: kind === "REOPEN" ? "CLOSED" : "OPENED",
        decision: decided
          ? {
              paymentDecisionDate: "2026-09-03",
              payableAmount: "5",
              payableCurrency: "EUR",
            }
          : null,
        payment: paid
          ? {
              paymentDate: "2026-09-04",
            }
          : null,
      },
    },
  };
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
      fireEvent.change(document.querySelector('[name="act.groups.registration.kind"]')!, {
        target: {
          value: "REPLACE",
        },
      });
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
      break;
    case "VOID":
    case "REINSTATE":
      input("act.reason", "Synthetic reason العربية");
      break;
    default:
      break;
  }
};
const caseReceipt = (request: CaseInput) => {
  const source = wireReply("operation.execute");
  if (source.outcome.kind !== "ACCEPTED" || source.outcome.data.subject.kind !== "CASE") {
    throw new Error("Missing frozen receipt alternative.");
  }
  return {
    ...source.outcome.data,
    operationId: request.operationId,
    subject: {
      kind: "CASE" as const,
      act: request.act.kind,
      caseReference: request.caseReference,
      revision: request.expectedRevision + 1,
      snapshot: {
        kind: "WITHHELD" as const,
      },
    },
  };
};
const canonicalChanges = (request: CaseInput): CaseReview["changes"] =>
  request.act.kind === "DECIDE"
    ? [
        {
          field: "payableAmount",
          before: null,
          after: "5",
        },
      ]
    : [];
const caseReview = (request: CaseInput) => ({
  operationId: request.operationId,
  outcome: {
    kind: "REVIEWED" as const,
    data: {
      review: {
        subject: {
          kind: "CASE" as const,
          before: null,
          after: {
            caseReference: request.caseReference,
            revision: request.expectedRevision + 1,
            state: {
              kind: "ACTIVE" as const,
              fields: {
                ...caseFields,
                caseReference: request.caseReference,
              },
            },
          },
          changes: canonicalChanges(request),
          context: runtime,
        },
      },
    },
  },
});
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
        operationId: parsed.operationId,
        outcome: {
          kind: "ACCEPTED",
          data: caseReceipt(parsed),
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
    case "CORRECT":
      expect(request.act.groups).toMatchObject({
        registration: {
          kind: "REPLACE",
          values: {
            claimantName: "Synthetic correction",
          },
        },
        decision: {
          kind: "KEEP",
        },
        payment: {
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
    expect(acts).toHaveLength(inventory.caseActs.length);
    assertInputMetadata();
    const calls: Dispatch<CaseInput>[] = [];
    vi.stubGlobal("fetch", caseTransport(calls));
    render(
      <OperationEditor
        definition={productMetadata}
        current={act.kind === "OPEN" ? null : currentFor(act.kind)}
        initialAct={act.kind}
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
      expect([...dialog.querySelectorAll("bdi")].map((node) => node.textContent)).toContain("5");
    }
    if (act.kind === "CORRECT") {
      expect(dialog).toHaveTextContent("Synthetic correction");
    }
    await user.click(
      screen.getByRole("checkbox", {
        name: "I confirm these changes.",
      }),
    );
    await user.click(getButton("Record changes"));
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
