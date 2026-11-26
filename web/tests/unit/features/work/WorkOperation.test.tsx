import { cloneElement } from "react";
import { render, cleanup } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { WorkOperation } from "../../../../src/features/work/WorkOperation";
import { operationTask } from "../../../../src/protocol/taskCatalog";
import { operationAffordance } from "../../../support/affordances.fixtures";
import { current } from "../../../support/presentation-state.fixtures";
import type { FormValues } from "../../../../src/protocol/forms";
import {
  initialAffordanceValues,
  type OperationAffordance,
} from "../../../../src/operation/affordances";

vi.mock("../../../../src/features/case/InlineNoteDraft", () => ({
  useInlineNoteDraft: () => ({ bind: () => undefined }),
}));
const state = vi.hoisted(() => ({
  preview: vi.fn(),
  options: vi.fn<(options: { prefill?: FormValues }) => void>(),
  ownerVerified: true,
  disclosureCurrent: true,
}));
vi.mock("../../../../src/operation/useOperation", () => ({
  useOperation: (options: { affordance: OperationAffordance; prefill: FormValues }) => {
    state.options(options);
    return {
      currentCase: null,
      values: { ...initialAffordanceValues(options.affordance), ...options.prefill },
      task: operationTask("reminder.update"),
      affordance: options.affordance,
      ownerVerified: state.ownerVerified,
      disclosureCurrent: state.disclosureCurrent,
      receipt: null,
      prepared: null,
      preview: state.preview,
    };
  },
}));
vi.mock("../../../../src/session/SessionContext", () => ({ useCredential: () => "credential" }));
vi.mock("../../../../src/hooks/EndpointProvider", () => ({ useEndpointCache: () => null }));
vi.mock("../../../../src/hooks/useOperationNavigation", () => ({
  useOperationNavigation: () => ({ close: vi.fn(), committed: vi.fn() }),
}));
vi.mock("../../../../src/features/operation/OperationPanel", () => ({
  OperationPanel: () => null,
  OperationDiscard: () => null,
}));
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  state.ownerVerified = true;
  state.disclosureCurrent = true;
});
const binding = {
  kind: "REMINDER",
  target: { kind: "REFERENCE", caseReference: "CASE-1" },
  reminderId: "30000000-0000-4000-8000-000000000003",
  expectedRevision: 3,
} as const;
const show = (
  seeds: Record<string, string>,
  editable: ReadonlyArray<"input:reminderAction" | "input:reminderDueDate"> = [
    "input:reminderAction",
    "input:reminderDueDate",
  ],
  prefill?: FormValues,
) => {
  const element = (
    <WorkOperation
      current={current}
      subject="REMINDER"
      kind="reminder.update"
      affordance={operationAffordance("reminder.update", binding, seeds, editable)}
      disclosureAvailable
      onClose={vi.fn()}
      onRecorded={vi.fn()}
      onLockChange={vi.fn()}
      onDisclosureRefused={vi.fn()}
      {...(prefill === undefined ? {} : { prefill })}
    />
  );
  const view = render(element);
  return {
    ...view,
    renderAgain: () => {
      view.rerender(cloneElement(element));
    },
  };
};
it("keeps editable Core seeds in the editor until explicit review", () => {
  const view = show({
    "input:reminderAction": "Call claimant",
    "input:reminderDueDate": "2026-11-20",
  });
  expect(state.preview).not.toHaveBeenCalled();
  expect(state.options.mock.calls[0]![0].prefill).toBeUndefined();
  view.renderAgain();
  expect(state.preview).not.toHaveBeenCalled();
});
it("does not preview with a missing authored input", () => {
  show({ "input:reminderAction": "Call claimant" });
  expect(state.preview).not.toHaveBeenCalled();
});
it("keeps other editable seeds open when only one inline value was authored", () => {
  show({ "input:reminderAction": "Call claimant" }, undefined, {
    "input:reminderDueDate": "2026-11-21",
  });
  expect(state.preview).not.toHaveBeenCalled();
});
it("retains fixed seeded action preview", () => {
  show({ "input:reminderAction": "Call claimant", "input:reminderDueDate": "2026-11-20" }, []);
  expect(state.preview).toHaveBeenCalledOnce();
});
it("does not preview while ownership or disclosure is unverified", () => {
  state.ownerVerified = false;
  state.disclosureCurrent = false;
  show({ "input:reminderAction": "Call claimant", "input:reminderDueDate": "2026-11-20" });
  expect(state.preview).not.toHaveBeenCalled();
});
