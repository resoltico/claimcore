import { createPresenter } from "../src/presentation/presenter";
import { defaults } from "../src/presentation/preferences";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "./presentation-test-support";
import { OperationEditor } from "../src/views/OperationEditor";
import { AuthorityPanel } from "../src/views/AuthorityPanel";
import { productMetadata } from "../src/protocol/productMetadata";
import { isRequest, isResponse } from "../src/protocol/client";
import type { EndpointId, RequestByEndpoint, ResponseByEndpoint } from "@contracts/client";
import type { CurrentCase, CaseReview } from "../src/api/service";
import type { ActKind } from "../src/operation/metadata";
import { caseOffers } from "./case-advice.fixtures";
import { installationOffers } from "./installation-advice.fixtures";
import { wireReply } from "./contract-corpus.fixtures";
import { caseFields } from "./service-foundation.fixtures";
import { fillRegistrationInputs } from "./registration-inputs.fixtures";
afterEach(() => vi.unstubAllGlobals());
type CaseInput = Extract<
  RequestByEndpoint["operation.execute"],
  {
    kind: "CASE";
  }
>;
type AuthorityInput = Extract<
  RequestByEndpoint["operation.execute"],
  {
    kind: "AUTHORITY";
  }
>;
type Dispatch<T> = {
  path: string;
  bytes: string;
  input: T;
};
const { acts, authorityActs } = productMetadata.catalog;
const runtime = {
  productVersion: "0.10.0",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};
const currentFor = (kind: ActKind): CurrentCase => {
  const decided = ["WITHDRAW_DECISION", "RECORD_PAYMENT", "CLEAR_PAYMENT", "CORRECT"].includes(
    kind,
  );
  const paid = kind === "CLEAR_PAYMENT" || kind === "CORRECT";
  return {
    caseReference: caseFields.caseReference,
    revision: 1,
    advice: {
      acts: caseOffers([kind]),
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
const input = (name: string, value: string) => {
  const control = document.querySelector<HTMLInputElement>(
    `input[name="${name}"],input[data-field-name="${name}"]`,
  );
  if (control === null) {
    throw new Error(`Missing catalogue input: ${name}`);
  }
  fireEvent.change(control, {
    target: {
      value,
    },
  });
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
const response = <K extends EndpointId>(endpoint: K, value: ResponseByEndpoint[K]): Response => {
  if (!isResponse(endpoint, value)) {
    throw new Error("Synthetic response violates the actual OpenAPI contract.");
  }
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: {
      "content-type": "application/json",
    },
  });
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
it.each(acts)(
  "renders, reviews and submits real case act $kind through production transport",
  async (act) => {
    expect(acts).toHaveLength(12);
    const calls: Dispatch<CaseInput>[] = [];
    vi.stubGlobal("fetch", caseTransport(calls));
    render(
      <OperationEditor
        token="token"
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
    await user.click(
      screen.getByRole("button", {
        name: "Review changes",
      }),
    );
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
    await user.click(
      screen.getByRole("button", {
        name: "Record changes",
      }),
    );
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
const authorityReceipt = (request: AuthorityInput) => {
  const source = wireReply("operation.execute", "AUTHORITY");
  if (source.outcome.kind !== "ACCEPTED" || source.outcome.data.subject.kind !== "AUTHORITY") {
    throw new Error("Missing frozen authority alternative.");
  }
  return {
    ...source.outcome.data,
    operationId: request.operationId,
    subject: {
      ...source.outcome.data.subject,
      act: request.act.kind,
    },
  };
};
const contextReply = (): Response =>
  response("context", {
    outcome: {
      kind: "SUCCEEDED",
      data: {
        ...runtime,
        capabilities: ["MANAGE_AUTHORITY", "READ_AUTHORITY"],
        views: ["AUTHORITY", "OPERATIONS"],
        offers: installationOffers(["REGISTER", "GRANT", "REVOKE", "ENABLE", "DISABLE"]),
      },
    },
  });
const authorityTransport = (calls: Dispatch<AuthorityInput>[]) =>
  vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    if (path === "/api/context") {
      return Promise.resolve(contextReply());
    }
    if (path === "/api/authority.list") {
      return Promise.resolve(
        response("authority.list", {
          outcome: {
            kind: "SUCCEEDED",
            data: {
              items: [],
              nextCursor: null,
            },
          },
        }),
      );
    }
    if (
      typeof path !== "string" ||
      !["/api/operation.execute", "/api/operation.preview"].includes(path) ||
      typeof init?.body !== "string"
    ) {
      throw new Error("Unexpected authority endpoint.");
    }
    const parsed: unknown = JSON.parse(init.body);
    if (!isRequest("operation.execute", parsed) || parsed.kind !== "AUTHORITY") {
      throw new Error("Production authority form did not produce a complete request.");
    }
    calls.push({
      path,
      bytes: init.body,
      input: parsed,
    });
    if (path === "/api/operation.preview") {
      return Promise.resolve(
        response("operation.preview", {
          operationId: parsed.operationId,
          outcome: {
            kind: "REVIEWED",
            data: {
              review: {
                subject: {
                  kind: "AUTHORITY",
                  target: null,
                  before: {
                    enabled: false,
                    grants: [],
                  },
                  after: {
                    enabled: true,
                    grants: [],
                  },
                },
              },
            },
          },
        }),
      );
    }
    return Promise.resolve(
      response("operation.execute", {
        operationId: parsed.operationId,
        outcome: {
          kind: "ACCEPTED",
          data: authorityReceipt(parsed),
        },
      }),
    );
  });
const authorAuthority = async (kind: AuthorityInput["act"]["kind"]) => {
  await waitFor(() => {
    expect(
      screen
        .getByLabelText("Authority action", {
          exact: true,
        })
        .querySelectorAll("option"),
    ).toHaveLength(5);
  });
  fireEvent.change(
    screen.getByLabelText("Authority action", {
      exact: true,
    }),
    {
      target: {
        value: kind,
      },
    },
  );
  input("target.issuer", "https://issuer.example/");
  input("target.subject", "synthetic-catalogue-human");
  fireEvent.change(document.querySelector('[name="target.kind"]')!, {
    target: {
      value: "HUMAN",
    },
  });
  if (kind === "GRANT" || kind === "REVOKE") {
    fireEvent.change(document.querySelector('[name="act.role"]')!, {
      target: {
        value: "CASE_READER",
      },
    });
  }
};
it.each(authorityActs)(
  "renders, reviews and submits real authority act $kind through production transport",
  async (act) => {
    expect(authorityActs).toHaveLength(5);
    const calls: Dispatch<AuthorityInput>[] = [];
    vi.stubGlobal("fetch", authorityTransport(calls));
    render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
    await authorAuthority(act.kind);
    const user = userEvent.setup();
    await user.click(
      screen.getByRole("button", {
        name: "Review changes",
      }),
    );
    await screen.findByRole("heading", {
      name: "Review changes",
    });
    const before = screen.getByRole("dialog").textContent;
    await user.click(
      screen.getByRole("checkbox", {
        name: "I confirm this exact request.",
      }),
    );
    await user.click(
      screen.getByRole("button", {
        name: "Record changes",
      }),
    );
    await waitFor(() => {
      expect(calls).toHaveLength(2);
      expect(calls[1]!.bytes).toBe(calls[0]!.bytes);
    });
    await screen.findByText(calls[0]!.input.operationId, {
      exact: true,
    });
    expect(before).toContain(calls[0]!.input.operationId);
    expect(calls[0]!.input.act.kind).toBe(act.kind);
    expect(calls[0]!.input.target).toEqual({
      kind: "HUMAN",
      issuer: "https://issuer.example/",
      subject: "synthetic-catalogue-human",
    });
    if (calls[0]!.input.act.kind === "GRANT" || calls[0]!.input.act.kind === "REVOKE") {
      expect(calls[0]!.input.act).toMatchObject({
        role: "CASE_READER",
        scope: {
          kind: "INSTALLATION",
        },
      });
    }
    if (calls[0]!.input.act.kind === "ENABLE") {
      expect(calls[0]!.input.act).toEqual({
        kind: "ENABLE",
      });
    }
  },
);
