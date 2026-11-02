import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen, TestSession, waitFor } from "../support/presentation-test-support";
import { current } from "../support/operation-form.fixtures";
import { definition, response } from "../support/service-ui.fixtures";
import { CaseDetail } from "../../src/views/CaseDetail";

const readable = {
  ...current,
  advice: { ...current.advice, history: [] },
  work: { ...current.work, canUpdateSummary: true, canAssign: true, canCreateReminder: true },
};
const detail = (credential: string) => (
  <TestSession credential={credential}>
    <CaseDetail
      target={{ kind: "ID", caseId: current.caseId }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  </TestSession>
);
afterEach(() => vi.unstubAllGlobals());
it("retains a work draft through same-owner session refresh while masking the unverified read panes", async () => {
  let release!: (value: Response) => void;
  const pending = new Promise<Response>((resolve) => {
    release = resolve;
  });
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response("case.get", "SUCCEEDED", readable))
    .mockImplementationOnce(() => pending)
    .mockResolvedValue(response("case.get", "SUCCEEDED", readable));
  vi.stubGlobal("fetch", fetch);
  const view = render(detail("before"));
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Update case work" }));
  await user.click(
    document.querySelector<HTMLInputElement>('input[data-selects-fields~="handlingSummary"]')!,
  );
  const field = screen.getByRole("textbox", { name: "Handling summary" });
  await user.type(field, "private work draft{Enter}ثانٍ");
  expect(field).toHaveValue("private work draft\nثانٍ");
  view.rerender(detail("after"));
  await waitFor(() => {
    expect(screen.queryByRole("textbox", { name: "Handling summary" })).toBeNull();
  });
  expect(document.querySelector(".case-fields")).toBeNull();
  expect(document.querySelector(".case-work")).toBeNull();
  await act(async () => {
    release(response("case.get", "SUCCEEDED", readable));
    await pending;
  });
  await user.click(await screen.findByRole("button", { name: "Reload current case" }));
  expect(await screen.findByRole("textbox", { name: "Handling summary" })).toHaveValue(
    "private work draft\nثانٍ",
  );
  expect(fetch.mock.calls.every(([url]) => String(url) === "/api/case.get")).toBe(true);
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  await user.click(await screen.findByRole("button", { name: "Discard and leave" }));
  expect(await screen.findByRole("button", { name: "Update case work" })).toHaveFocus();
});
