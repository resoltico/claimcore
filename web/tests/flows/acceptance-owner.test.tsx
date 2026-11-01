import { expect, it, vi, afterEach } from "vitest";
import userEvent from "@testing-library/user-event";
import { render } from "../support/presentation-test-support";
import { Dashboard } from "../../src/views/Dashboard";
import { SessionContext } from "../../src/session/SessionContext";
import { context as baseContext } from "../support/service-ui.fixtures";
import { wireReply } from "../support/contract-corpus.fixtures";
import { navigate } from "../../src/routing/navigation";
import { casePath } from "../../src/routing/routes";
import type { SessionState } from "../../src/session/useSession";
import type { CaseReceipt } from "../../src/api/service";
import type { ActorContext } from "@contracts/client";
const audit = vi.hoisted(() => ({
  context: null as ActorContext | null,
  receipt: null as CaseReceipt | null,
  callbacks: [] as ((receipt: CaseReceipt) => void)[],
}));
vi.mock("../../src/hooks/useEndpoint", () => ({
  useEndpoint: () => ({
    loading: false,
    message: null,
    previous: null,
    reload: vi.fn(),
    value: audit.context === null ? null : { outcome: { kind: "SUCCEEDED", data: audit.context } },
  }),
}));
vi.mock("../../src/views/RoutedContent", () => ({
  RoutedContent: ({ committed }: { committed: (receipt: CaseReceipt) => void }) => {
    audit.callbacks.push(committed);
    return (
      <button
        onClick={() => {
          committed(audit.receipt!);
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
  if (example.outcome.kind !== "ACCEPTED" || example.outcome.data.subject.kind !== "CASE") {
    throw new Error("Need the independent accepted-case corpus");
  }
  audit.receipt = { ...example.outcome.data, subject: example.outcome.data.subject };
  audit.context = {
    ...baseContext,
    actorId: audit.receipt.recordedBy,
    views: ["CASES", "OPERATIONS"],
  };
  navigate(casePath(audit.receipt.subject.caseReference), true);
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
    actorId: "90000000-0000-4000-8000-000000000099",
    views: ["OPERATIONS"],
  };
  view.rerender(shown("actor-b-token", 2));
  expect(view.getByRole("heading", { name: "Case detail", level: 1 })).toBeVisible();
  expect(view.container.querySelector(".success-toast")).toBeNull();
});

it("cannot revive prior feedback on actor A to B to A in the same session", async () => {
  const view = await start();
  const original = audit.context!;
  audit.context = { ...original, actorId: "90000000-0000-4000-8000-000000000099" };
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
  audit.context = { ...original, installationId: "90000000-0000-4000-8000-000000000099" };
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
  audit.context = { ...audit.context!, actorId: "90000000-0000-4000-8000-000000000099" };
  view.rerender(shown("actor-b-token", 2));
  const before = location.href;
  late(audit.receipt!);
  expect(view.container.querySelector(".success-toast")).toBeNull();
  expect(location.href).toBe(before);
});
