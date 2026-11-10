import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen, TestSession, waitFor } from "../support/presentation-test-support";
import { caseViewInput, currentCaseInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";
import { definition, fields } from "../support/service-ui.fixtures";
import { CaseDetail } from "../../src/views/CaseDetail";

const facts = caseViewInput({
  caseReference: fields.caseReference,
  revision: 4,
  state: { kind: "ACTIVE", fields },
  advice: { ...caseAdvice([]), history: "NONE" },
});
const readable = currentCaseInput({
  ...facts,
  work: { ...facts.work, canUpdateSummary: true, canAssign: true, canCreateReminder: true },
});
const response = () =>
  endpointResponse("case.get", {
    representation: "HUMAN",
    outcome: { kind: "SUCCEEDED", document: readable },
  });
const detail = (credential: string) => (
  <TestSession credential={credential}>
    <CaseDetail
      target={{ kind: "ID", caseId: readable.context.caseId }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  </TestSession>
);
afterEach(() => vi.unstubAllGlobals());
it("masks the prior session work draft and waits for a fresh read before offering new work", async () => {
  let release!: (value: Response) => void;
  const pending = new Promise<Response>((resolve) => {
    release = resolve;
  });
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response())
    .mockImplementationOnce(() => pending)
    .mockResolvedValue(response());
  vi.stubGlobal("fetch", fetch);
  const view = render(detail("before"));
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Edit summary" }));
  expect(screen.getByRole("dialog", { name: "Update case work" })).toHaveFocus();
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
    release(response());
    await pending;
  });
  expect(screen.queryByDisplayValue("private work draft\nثانٍ")).toBeNull();
  await user.click(await screen.findByRole("button", { name: "Edit summary" }));
  const fresh = await screen.findByRole("textbox", { name: "Handling summary" });
  expect(fresh).toHaveValue("");
  await user.type(fresh, "New session draft");
  expect(fetch.mock.calls.every(([url]) => String(url) === "/api/case.get")).toBe(true);
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  await user.click(await screen.findByRole("button", { name: "Discard and leave" }));
  expect(await screen.findByRole("button", { name: "Edit summary" })).toHaveFocus();
});

it("keeps the same draft owner through workspace rerenders without sending a mutation", async () => {
  const fetch = vi.fn().mockResolvedValue(response());
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(detail("same-owner"));
  await screen.findByRole("heading", { name: "Next actions" });
  const workspace = view.container.querySelector(".case-workspace");
  expect(view.container.querySelectorAll(".case-workspace .case-fact-regions")).toHaveLength(1);
  await user.click(screen.getByRole("button", { name: "Edit summary" }));
  const field = screen.getByRole("textbox", { name: "Handling summary" });
  await user.type(field, "Saved across layout — ثانٍ");
  view.rerender(detail("same-owner"));
  expect(screen.getByRole("textbox", { name: "Handling summary" })).toBe(field);
  expect(field).toHaveValue("Saved across layout — ثانٍ");
  view.rerender(detail("same-owner"));
  expect(screen.getByRole("textbox", { name: "Handling summary" })).toBe(field);
  expect(view.container.querySelector(".case-workspace")).toBe(workspace);
  expect(fetch.mock.calls.every(([url]) => String(url) === "/api/case.get")).toBe(true);
});
