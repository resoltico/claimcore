import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { selectFact } from "../support/field-entry";
import { fields } from "../support/service-ui.fixtures";
import { currentCaseInput } from "../support/inputs";
import { within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { render, screen, TestSession } from "../support/presentation-test-support";
import { caseAdvice } from "../support/case-advice.fixtures";
import { current, editor, previewReply } from "../support/presentation-state.fixtures";
const canary = "PRIOR_ACTOR_PREFILL_CANARY";
const disclosed = currentCaseInput({
  caseId: current.context.caseId,
  caseReference: fields.caseReference,
  revision: 1,
  advice: { ...caseAdvice(["AMEND_REGISTRATION"]), history: "FULL" },
  state: {
    kind: "ACTIVE",
    fields: { ...fields, registration: { ...fields.registration, claimantName: canary } },
  },
});
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("closes prior actor prefills synchronously on token change without reusing the old observation", () => {
  const view = render(
    <TestSession credential="token">
      {editor({ current: disclosed, initialAct: "AMEND_REGISTRATION" })}
    </TestSession>,
  );
  selectFact("claimantName");
  expect(screen.getByLabelText("Claimant name", { exact: true })).toHaveValue(canary);
  view.rerender(
    <TestSession credential="new-token">
      {editor({ current: disclosed, initialAct: "AMEND_REGISTRATION" })}
    </TestSession>,
  );
  expect(screen.queryByDisplayValue(canary)).toBeNull();
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(fetch).not.toHaveBeenCalled();
});
it("requires an explicit warning before discarding a possibly accepted request", async () => {
  vi.mocked(fetch)
    .mockImplementationOnce(() => Promise.resolve(previewReply()))
    .mockRejectedValueOnce(new Error("Synthetic reply loss"));
  const close = vi.fn();
  const user = userEvent.setup();
  render(editor({ onClose: close }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  const dialog = screen.getByRole("dialog");
  expect(dialog).toHaveTextContent("may already be recorded");
  expect(close).not.toHaveBeenCalled();
  await user.click(within(dialog).getByRole("button", { name: "Discard and leave" }));
  expect(close).toHaveBeenCalledOnce();
});
