import { expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { current } from "../support/presentation-state.fixtures";
import { blockedCaseActs } from "../support/blocked-case-acts.fixtures";
import { RoutedContent } from "../../src/views/RoutedContent";
import { service } from "../../src/api/service";
import type { CaseView, Diagnostic } from "@contracts/client";
const mount = (value: CaseView, act: string) => {
  const get = mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: value } },
  });
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  const view = render(
    <RoutedContent
      route={{
        screen: "caseAct",
        target: { kind: "REFERENCE", caseReference: value.caseReference },
        act,
      }}
      setLocked={vi.fn()}
      committed={vi.fn()}
      refresh={0}
      caseNavigation={{ selectCase: vi.fn(), openCase: vi.fn(), backToCases: vi.fn() }}
    />,
  );
  return { view, get, preview, execute };
};
it("keeps the workspace and renders the actual blocked state reason and recommended alternative", async () => {
  const closed: CaseView = {
    ...current,
    headline: "CLOSED",
    advice: {
      ...current.advice,
      acts: ["REOPEN"],
      recommended: "REOPEN",
      history: [],
      blocked: blockedCaseActs(["REOPEN"], "CLOSED"),
    },
    state: { ...current.state, fields: { ...current.state.fields, status: "CLOSED" } },
  };
  const { view, get, preview, execute } = mount(closed, "CLOSE");
  expect(
    await screen.findByRole("heading", {
      name: current.state.fields.registration.claimantName,
      level: 1,
    }),
  ).toBeVisible();
  expect(screen.getByRole("button", { name: "Back to cases" })).toBeVisible();
  expect(view.container.querySelector(".case-action-refusal")).toHaveTextContent("Closed");
  expect(view.container.querySelector(".case-action-refusal button")).toHaveTextContent("Reopen");
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  expect(get).toHaveBeenCalledOnce();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
it("renders a provided authorization reason without inventing a state explanation", async () => {
  const reason: Diagnostic = {
    code: "NOT_AUTHORIZED",
    required: "DECIDE",
    action: "ASK_FOR_ACCESS",
  };
  const value: CaseView = {
    ...current,
    advice: {
      ...current.advice,
      acts: [],
      recommended: null,
      history: [],
      blocked: blockedCaseActs([]).map((item) =>
        item.act === "DECIDE" ? { act: item.act, reason } : item,
      ),
    },
  };
  const { view, preview, execute } = mount(value, "DECIDE");
  await screen.findByRole("heading", {
    level: 1,
    name: current.state.fields.registration.claimantName,
  });
  const callout = view.container.querySelector(".case-action-refusal")!;
  expect(callout).toHaveTextContent("Case handler");
  expect(callout).not.toHaveTextContent("Undecided");
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
