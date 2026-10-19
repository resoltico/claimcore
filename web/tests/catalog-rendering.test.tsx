import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "./presentation-test-support";
import { OperationEditor } from "../src/views/OperationEditor";
import { AuthorityPanel } from "../src/views/AuthorityPanel";
import { productMetadata } from "../src/protocol/productMetadata";
import { isRequest, isResponse } from "../src/protocol/client";
import type { EndpointId, RequestByEndpoint, ResponseByEndpoint } from "@contracts/client";
import type { CurrentCase, Review } from "../src/api/service";
import type { CommandKind } from "../src/interaction/metadata";
import { caseOffers } from "./case-advice.fixtures";
import { installationOffers } from "./installation-advice.fixtures";
import { generatedWebValue } from "./contract-corpus.fixtures";
import { caseFields } from "./service-foundation.fixtures";
import { fillRegistrationInputs } from "./registration-inputs.fixtures";

afterEach(() => vi.unstubAllGlobals());
type CaseInput = RequestByEndpoint["case.execute"];
type AuthorityInput = RequestByEndpoint["authority.execute"];
type Dispatch<T> = { path: string; bytes: string; input: T };
const { commands, authorityCommands } = productMetadata.descriptors;
const runtime = {
  productVersion: "0.10.0",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};

const currentFor = (kind: CommandKind): CurrentCase => {
  const decided = ["WITHDRAW_DECISION", "RECORD_PAYMENT", "CLEAR_PAYMENT", "CORRECT_CASE"].includes(
    kind,
  );
  const paid = kind === "CLEAR_PAYMENT" || kind === "CORRECT_CASE";
  return {
    caseReference: caseFields.caseReference,
    revision: 1,
    advice: { acts: caseOffers([kind]), history: ["SUMMARY", "FULL"] },
    state: {
      tag: "LIVE",
      disposition: kind === "REINSTATE" ? "VOIDED" : "ACTIVE",
      fields: {
        ...caseFields,
        status: kind === "REOPEN" ? "CLOSED" : "OPENED",
        paymentDecisionDate: decided ? "2026-09-03" : null,
        payableAmount: decided ? "5" : null,
        payableCurrency: decided ? "EUR" : null,
        paymentDate: paid ? "2026-09-04" : null,
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
  fireEvent.change(control, { target: { value } });
};
const authorCase = (kind: CommandKind) => {
  switch (kind) {
    case "OPEN":
      fillRegistrationInputs();
      break;
    case "AMEND_REGISTRATION":
      input("claimantName", "Synthetic replacement");
      break;
    case "CORRECT_CASE":
      fireEvent.change(document.querySelector("#correction-registration-mode")!, {
        target: { value: "REPLACE" },
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
      input("reason", "Synthetic reason العربية");
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
    headers: { "content-type": "application/json" },
  });
};
const caseReceipt = (request: CaseInput) => {
  const source = generatedWebValue("case.execute");
  if (source.outcome.tag !== "ACCEPTED") {
    throw new Error("Missing frozen receipt alternative.");
  }
  return {
    ...source.outcome.data,
    operationId: request.operationId,
    act: request.act.kind,
    caseReference: request.caseReference,
    revision: request.expectedRevision + 1,
    snapshot: { tag: "WITHHELD" as const },
  };
};
const canonicalChanges = (request: CaseInput): Review["changes"] =>
  request.act.kind === "DECIDE" ? [{ field: "payableAmount", before: null, after: "5" }] : [];
const caseReview = (request: CaseInput) => ({
  endpoint: "case.preview" as const,
  operationId: request.operationId,
  outcome: {
    tag: "REVIEWED" as const,
    data: {
      review: {
        before: null,
        after: {
          caseReference: request.caseReference,
          revision: request.expectedRevision + 1,
          state: {
            tag: "LIVE" as const,
            disposition: "ACTIVE" as const,
            fields: { ...caseFields, caseReference: request.caseReference },
          },
        },
        changes: canonicalChanges(request),
        context: runtime,
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
    if (!isRequest("case.execute", parsed)) {
      throw new Error("Production editor did not produce a complete case request.");
    }
    calls.push({ path, bytes: init.body, input: parsed });
    if (path === "/api/case.preview") {
      return Promise.resolve(response("case.preview", caseReview(parsed)));
    }
    if (path !== "/api/case.execute") {
      throw new Error("Unexpected case endpoint.");
    }
    return Promise.resolve(
      response("case.execute", {
        endpoint: "case.execute",
        operationId: parsed.operationId,
        outcome: { tag: "ACCEPTED", data: caseReceipt(parsed) },
      }),
    );
  });
const assertAuthoredCase = (request: CaseInput) => {
  switch (request.act.kind) {
    case "OPEN":
      expect(request.act.values.claimantName).toBe(caseFields.claimantName);
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
    case "CORRECT_CASE":
      expect(request.act.groups).toMatchObject({
        registration: {
          mode: "REPLACE",
          values: { claimantName: "Synthetic correction" },
        },
        decision: { mode: "KEEP" },
        payment: { mode: "KEEP" },
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
it.each(commands)(
  "renders, reviews and submits real case act $kind through production transport",
  async (command) => {
    expect(commands).toHaveLength(12);
    const calls: Dispatch<CaseInput>[] = [];
    vi.stubGlobal("fetch", caseTransport(calls));
    render(
      <OperationEditor
        token="token"
        definition={productMetadata}
        current={command.kind === "OPEN" ? null : currentFor(command.kind)}
        initialCommand={command.kind}
        onClose={vi.fn()}
        onCommitted={vi.fn()}
        onMutationLockChange={vi.fn()}
      />,
    );
    expect(screen.getByRole("heading", { name: command.label })).toBeVisible();
    authorCase(command.kind);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    const dialog = await screen.findByRole("dialog");
    if (command.kind === "DECIDE") {
      expect(dialog).toHaveTextContent("5.0000");
      expect([...dialog.querySelectorAll("bdi")].map((node) => node.textContent)).toContain("5");
    }
    if (command.kind === "CORRECT_CASE") {
      expect(dialog).toHaveTextContent("Synthetic correction");
    }
    await user.click(screen.getByRole("checkbox", { name: "I confirm these changes." }));
    await user.click(screen.getByRole("button", { name: "Record changes" }));
    await screen.findByRole("heading", { name: "Recorded change" });
    expect(calls.map((call) => call.path)).toEqual(["/api/case.preview", "/api/case.execute"]);
    expect(calls[1]!.bytes).toBe(calls[0]!.bytes);
    expect(calls[1]!.input.act.kind).toBe(command.kind);
    expect(calls[1]!.input.expectedRevision).toBe(command.kind === "OPEN" ? 0 : 1);
    assertAuthoredCase(calls[1]!.input);
  },
);
const authorityReceipt = (request: AuthorityInput) => {
  const source = generatedWebValue("authority.execute");
  if (source.outcome.tag !== "ACCEPTED") {
    throw new Error("Missing frozen authority alternative.");
  }
  return {
    ...source.outcome.data,
    operationId: request.operationId,
    act: request.act.kind,
  };
};
const contextReply = (): Response =>
  response("context", {
    endpoint: "context",
    outcome: {
      tag: "SUCCEEDED",
      data: {
        ...runtime,
        capabilities: ["MANAGE_AUTHORITY", "READ_AUTHORITY"],
        views: ["AUTHORITY", "OPERATIONS"],
        offers: installationOffers(["REGISTER_ACTOR", "SET_GRANT", "SET_ENABLED"]),
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
          endpoint: "authority.list",
          outcome: { tag: "SUCCEEDED", data: { items: [], nextCursor: null } },
        }),
      );
    }
    if (path !== "/api/authority.execute" || typeof init?.body !== "string") {
      throw new Error("Unexpected authority endpoint.");
    }
    const parsed: unknown = JSON.parse(init.body);
    if (!isRequest("authority.execute", parsed)) {
      throw new Error("Production authority form did not produce a complete request.");
    }
    calls.push({ path, bytes: init.body, input: parsed });
    return Promise.resolve(
      response("authority.execute", {
        endpoint: "authority.execute",
        operationId: parsed.operationId,
        outcome: { tag: "ACCEPTED", data: authorityReceipt(parsed) },
      }),
    );
  });
const authorAuthority = async (kind: AuthorityInput["act"]["kind"]) => {
  await waitFor(() => {
    expect(
      screen.getByLabelText("Case action", { exact: true }).querySelectorAll("option"),
    ).toHaveLength(3);
  });
  fireEvent.change(screen.getByLabelText("Case action", { exact: true }), {
    target: { value: kind },
  });
  input("target.issuer", "https://issuer.example/");
  input("target.subject", "synthetic-catalogue-human");
  fireEvent.change(document.querySelector('[name="target.kind"]')!, {
    target: { value: "HUMAN" },
  });
  if (kind === "SET_GRANT") {
    fireEvent.change(document.querySelector('[name="act.role"]')!, {
      target: { value: "CASE_READER" },
    });
    fireEvent.click(document.querySelector('[name="act.active"]')!);
  }
  if (kind === "SET_ENABLED") {
    fireEvent.click(document.querySelector('[name="act.enabled"]')!);
  }
};
it.each(authorityCommands)(
  "renders, reviews and submits real authority act $kind through production transport",
  async (command) => {
    expect(authorityCommands).toHaveLength(3);
    const calls: Dispatch<AuthorityInput>[] = [];
    vi.stubGlobal("fetch", authorityTransport(calls));
    render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
    await authorAuthority(command.kind);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    await screen.findByRole("heading", { name: "Review exact request" });
    const before = document.querySelector(".authority-panel")!.textContent;
    await user.click(screen.getByRole("checkbox", { name: "I confirm this exact request." }));
    await user.click(screen.getByRole("button", { name: "Submit operation" }));
    await waitFor(() => {
      expect(calls).toHaveLength(1);
    });
    await screen.findByText(`accepted · ${calls[0]!.input.operationId}`);
    expect(before).toContain(calls[0]!.input.operationId);
    expect(calls[0]!.input.act.kind).toBe(command.kind);
    expect(calls[0]!.input.target).toEqual({
      kind: "HUMAN",
      issuer: "https://issuer.example/",
      subject: "synthetic-catalogue-human",
    });
    if (calls[0]!.input.act.kind === "SET_GRANT") {
      expect(calls[0]!.input.act).toMatchObject({
        role: "CASE_READER",
        active: true,
        scope: { kind: "INSTALLATION" },
      });
    }
    if (calls[0]!.input.act.kind === "SET_ENABLED") {
      expect(calls[0]!.input.act.enabled).toBe(true);
    }
  },
);
