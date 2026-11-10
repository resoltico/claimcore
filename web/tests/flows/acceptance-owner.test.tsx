import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { beforeEach, expect, it, vi, afterEach } from "vitest";
import userEvent from "@testing-library/user-event";
import { render } from "../support/presentation-test-support";
import { Dashboard } from "../../src/views/Dashboard";
import { SessionContext } from "../../src/session/SessionContext";
import { activeRequester } from "../support/human-requester.fixtures";
import type { ActiveRequester } from "../../src/hooks/useRequesterContext";
import { isCaseReceipt } from "../../src/protocol/types";
import { wireReply } from "../support/reply.fixtures";
import { navigate } from "../../src/routing/navigation";
import { casePath } from "../../src/routing/routes";
import type { SessionState } from "../../src/session/useSession";
import type { CaseOperationReceipt } from "../../src/api/service";
beforeEach(() => {
  stubViewCounts();
});
const audit = vi.hoisted(() => ({
  context: null as ActiveRequester | null,
  refused: false,
  receipt: null as CaseOperationReceipt | null,
  callbacks: [] as ((receipt: CaseOperationReceipt, operationId: string) => void)[],
}));
vi.mock("../../src/hooks/useEndpoint", () => ({
  useEndpoint: (endpoint: string) => ({
    loading: false,
    result: audit.refused
      ? {
          kind: "hostFailure",
          status: 401,
          failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
        }
      : null,
    message: null,
    previous: null,
    reload: vi.fn(),
    value:
      endpoint !== "context" || audit.context === null
        ? null
        : {
            representation: "HUMAN",
            outcome: { kind: "SUCCEEDED", document: audit.context },
          },
  }),
}));
vi.mock("../../src/views/RoutedContent", () => ({
  RoutedContent: ({
    committed,
  }: {
    committed: (receipt: CaseOperationReceipt, operationId: string) => void;
  }) => {
    audit.callbacks.push(committed);
    return (
      <button
        onClick={() => {
          committed(audit.receipt!, "10000000-0000-7000-8000-000000000001");
        }}
      >
        Simulate admitted acceptance
      </button>
    );
  },
}));
afterEach(() => {
  vi.clearAllMocks();
  audit.callbacks = [];
  audit.refused = false;
});
const sessionView = (state: SessionState) => (
  <SessionContext
    value={{ state, logout: () => Promise.resolve(), refresh: () => Promise.resolve() }}
  >
    <Dashboard />
  </SessionContext>
);
const shown = (token: string, epoch: number) =>
  sessionView({ kind: "authenticated", token, epoch });
const start = async () => {
  const example = wireReply("operation.execute");
  if (example.outcome.kind !== "ACCEPTED" || !isCaseReceipt(example.outcome.document)) {
    throw new Error("Need an accepted HUMAN case receipt");
  }
  audit.receipt = example.outcome.document;
  audit.context = {
    ...activeRequester,
    context: { ...activeRequester.context, views: ["CASES", "OPERATIONS"] },
  };
  navigate(casePath(audit.receipt.context.target.caseId), true);
  const view = render(shown("actor-a-token", 1));
  await userEvent.click(await view.findByRole("button", { name: "Simulate admitted acceptance" }));
  expect(view.container.querySelector(".success-toast")).not.toBeNull();
  return view;
};
it("control: equivalent requester refresh can retain own acceptance feedback", async () => {
  const view = await start();
  audit.context = { ...audit.context! };
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).not.toBeNull();
});
it("a new actor/session cannot inherit the previous actor acceptance feedback", async () => {
  const view = await start();
  audit.context = {
    ...audit.context!,
    context: {
      ...audit.context!.context,
      actorId: "90000000-0000-4000-8000-000000000099",
      views: ["OPERATIONS"],
    },
  };
  view.rerender(shown("actor-b-token", 2));
  expect(
    view.getByRole("heading", {
      name: "Case detail",
      level: 1,
    }),
  ).toBeVisible();
  expect(view.container.querySelector(".success-toast")).toBeNull();
});

it("cannot revive prior feedback on actor A to B to A in the same session", async () => {
  const view = await start();
  const original = audit.context!;
  audit.context = {
    ...original,
    context: { ...original.context, actorId: "90000000-0000-4000-8000-000000000099" },
  };
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).toBeNull();
  audit.context = original;
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).toBeNull();
});
it("masks acceptance during unverified context and after installation replacement", async () => {
  const view = await start();
  const original = audit.context!;
  audit.context = null;
  view.rerender(shown("renewed-token", 2));
  expect(view.container.querySelector(".success-toast")).toBeNull();
  audit.context = {
    ...original,
    context: { ...original.context, installationId: "90000000-0000-4000-8000-000000000099" },
  };
  view.rerender(shown("renewed-token", 2));
  expect(view.container.querySelector(".success-toast")).toBeNull();
});
it.each(["anonymous", "failure"] as const)("does not revive acceptance after %s", async (kind) => {
  const view = await start();
  const state: SessionState =
    kind === "anonymous"
      ? { kind, token: null, message: null, epoch: 2 }
      : { kind, message: { kind: "local", reason: "unreachable" }, epoch: 2 };
  view.rerender(sessionView(state));
  expect(view.container.querySelector(".success-toast")).toBeNull();
  view.rerender(shown("actor-a-token", 3));
  expect(view.container.querySelector(".success-toast")).toBeNull();
});
it("ignores a late acceptance callback after an owner transition", async () => {
  const view = await start();
  const late = audit.callbacks.at(-1)!;
  audit.context = {
    ...audit.context!,
    context: { ...audit.context!.context, actorId: "90000000-0000-4000-8000-000000000099" },
  };
  view.rerender(shown("actor-b-token", 2));
  const before = location.href;
  late(audit.receipt!, "10000000-0000-7000-8000-000000000001");
  expect(view.container.querySelector(".success-toast")).toBeNull();
  expect(location.href).toBe(before);
});

it("masks own acceptance through a read invalidation and restores identity feedback after fresh authorization", async () => {
  const view = await start();
  const original = audit.context!;
  audit.context = null;
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).toBeNull();
  audit.context = { ...original };
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).not.toBeNull();
});

it("does not restore prior acceptance after known case access is removed and later returns", async () => {
  const view = await start();
  const original = audit.context!;
  audit.context = { ...original, context: { ...original.context, views: ["OPERATIONS"] } };
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).toBeNull();
  audit.context = original;
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).toBeNull();
});

it("does not restore prior acceptance after a definite session refusal and fresh successful context", async () => {
  const view = await start();
  const original = audit.context!;
  audit.context = null;
  audit.refused = true;
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).toBeNull();
  audit.refused = false;
  audit.context = original;
  view.rerender(shown("actor-a-token", 1));
  expect(view.container.querySelector(".success-toast")).toBeNull();
});
