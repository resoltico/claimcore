import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { render, screen, TestSession, waitFor } from "../support/presentation-test-support";
import { current } from "../support/operation-form.fixtures";
import { definition, response } from "../support/service-ui.fixtures";
import { CaseDetail } from "../../src/views/CaseDetail";

const viewport = vi.hoisted(() => ({ wide: false }));
vi.mock("../../src/hooks/useWide", () => ({ useWide: () => viewport.wide }));
beforeEach(() => {
  viewport.wide = false;
});
const readable = {
  ...current,
  advice: { ...current.advice, history: "NONE" },
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
  expect(screen.getByRole("dialog", { name: "Update case work" })).toHaveFocus();
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
  expect(document.querySelector('.record-view [data-entry="claimantName"]')).toBeNull();
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
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  await user.click(await screen.findByRole("button", { name: "Discard and leave" }));
  expect(await screen.findByRole("button", { name: "Update case work" })).toHaveFocus();
});

it("keeps one draft owner across compact and desktop panel layouts without sending a mutation", async () => {
  const fetch = vi.fn().mockResolvedValue(response("case.get", "SUCCEEDED", readable));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(detail("same-owner"));
  await screen.findByRole("heading", { name: "Next actions" });
  expect(
    [...view.container.querySelector(".case-workspace-compact")!.children].map(
      (node) => node.className,
    ),
  ).toEqual(["case-reminders", "record-view", "case-work"]);
  expect(view.container.querySelectorAll(".case-workspace .record-view")).toHaveLength(1);
  await user.click(screen.getByRole("button", { name: "Update case work" }));
  await user.click(
    document.querySelector<HTMLInputElement>('input[data-selects-fields~="handlingSummary"]')!,
  );
  const field = screen.getByRole("textbox", { name: "Handling summary" });
  await user.type(field, "Saved across layout — ثانٍ");
  viewport.wide = true;
  view.rerender(detail("same-owner"));
  expect(screen.getByRole("textbox", { name: "Handling summary" })).toBe(field);
  expect(field).toHaveValue("Saved across layout — ثانٍ");
  viewport.wide = false;
  view.rerender(detail("same-owner"));
  expect(screen.getByRole("textbox", { name: "Handling summary" })).toBe(field);
  expect(view.container.querySelectorAll(".case-workspace .record-view")).toHaveLength(1);
  expect(fetch.mock.calls.every(([url]) => String(url) === "/api/case.get")).toBe(true);
});
