import { act, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { caseOffers } from "./case-advice.fixtures";
import { current, deferredResponse, editor, previewReply } from "./presentation-state.fixtures";
const canary = "PRIOR_ACTOR_PREFILL_CANARY";
const disclosed = {
  ...current,
  advice: { ...current.advice, acts: caseOffers(["AMEND_REGISTRATION"]) },
  state: { ...current.state, fields: { ...current.state.fields, claimantName: canary } },
};
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("fences prior actor prefills through token change and discard until a new authorized preview", async () => {
  const user = userEvent.setup();
  const view = render(editor({ current: disclosed, initialCommand: "AMEND_REGISTRATION" }));
  expect(screen.getByLabelText("Claimant name", { exact: true })).toHaveValue(canary);
  view.rerender(
    editor({ current: disclosed, initialCommand: "AMEND_REGISTRATION", token: "new-token" }),
  );
  expect(screen.queryByDisplayValue(canary)).toBeNull();
  await user.click(screen.getByRole("button", { name: "Discard and leave" }));
  await user.click(
    within(screen.getByRole("dialog")).getByRole("button", { name: "Discard and leave" }),
  );
  expect(screen.queryByDisplayValue(canary)).toBeNull();
  const pending = deferredResponse();
  vi.mocked(fetch).mockReturnValueOnce(pending.promise);
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  expect(screen.queryByDisplayValue(canary)).toBeNull();
  expect(vi.mocked(fetch).mock.calls[0]?.[1]?.headers).toMatchObject({
    "X-ClaimCore-Antiforgery": "new-token",
  });
  await act(async () => {
    pending.resolve(previewReply());
    await pending.promise;
  });
  expect(await screen.findByRole("dialog", { name: "Review changes" })).toBeVisible();
});
it("requires an explicit warning before discarding a possibly accepted request", async () => {
  vi.mocked(fetch)
    .mockImplementationOnce(() => Promise.resolve(previewReply()))
    .mockRejectedValueOnce(new Error("Synthetic reply loss"));
  const close = vi.fn();
  const user = userEvent.setup();
  render(editor({ onClose: close }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  await screen.findByRole("button", { name: "Send again" });
  await user.click(screen.getByRole("button", { name: "Discard and leave" }));
  const dialog = screen.getByRole("dialog");
  expect(dialog).toHaveTextContent("does not cancel it");
  expect(close).not.toHaveBeenCalled();
  await user.click(within(dialog).getByRole("button", { name: "Discard and leave" }));
  expect(close).toHaveBeenCalledOnce();
});
