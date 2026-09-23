import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { CaseDetail } from "../src/views/CaseDetail";
import { OperationLookup } from "../src/views/OperationLookup";
import { definition, fields, operationId, response } from "./v2-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("keeps missing case lookup and non-full history entries out of case presentation", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.get", "SUCCEEDED", { tag: "NOT_FOUND", caseReference: "MISSING" }),
  );
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", {
      tag: "FOUND",
      entries: [{ tag: "SUMMARY" }],
      nextCursor: null,
    }),
  );
  render(
    <CaseDetail
      token="token"
      caseReference="MISSING"
      reloadSignal={0}
      definition={definition.definition}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  expect(await screen.findByRole("heading", { name: "Accepted history" })).toBeVisible();
  expect(await screen.findByText("Case was not found.")).toBeVisible();
  expect(screen.queryByRole("heading", { name: "Available commands" })).toBeNull();
});

it("keeps a failed history page request-local without erasing current case data", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.get", "SUCCEEDED", {
      tag: "FOUND",
      current: { case: { fields, revision: "1" }, availableCommands: [] },
    }),
  );
  fetch.mockResolvedValueOnce(
    new Response("invalid", { headers: { "content-type": "application/json" } }),
  );
  render(
    <CaseDetail
      token="token"
      caseReference="CASE-1"
      reloadSignal={0}
      definition={definition.definition}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  expect(await screen.findByText("CASE-1")).toBeVisible();
  expect(await screen.findByRole("alert")).toBeVisible();
});

it("treats a typed missing history as an empty read rather than a transport error", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.get", "SUCCEEDED", {
      tag: "FOUND",
      current: { case: { fields, revision: "1" }, availableCommands: [] },
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", { tag: "NOT_FOUND", caseReference: "CASE-1" }),
  );
  render(
    <CaseDetail
      token="token"
      caseReference="CASE-1"
      reloadSignal={0}
      definition={definition.definition}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  expect(await screen.findByRole("region", { name: "Case fields for current case" })).toBeVisible();
  expect(screen.getByRole("heading", { name: "Accepted history" })).toBeVisible();
  expect(screen.queryByRole("alert")).toBeNull();
});

it("reports absent and malformed operation observations without leaving stale receipts", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("operation.observe", "SUCCEEDED", { tag: "NOT_FOUND", operationId }),
  );
  render(<OperationLookup token="token" definition={definition} />);
  expect(screen.getByRole("button", { name: "Observe operation" })).toBeDisabled();
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(screen.getByRole("button", { name: "Observe operation" }));
  expect(await screen.findByText("Operation was not observed.")).toBeVisible();
  expect(screen.queryByRole("alert")).toBeNull();
  expect(screen.queryByText(/Accepted operation/u)).toBeNull();
});

it("reports a found observation without a receipt as a protocol failure", async () => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("operation.observe", "SUCCEEDED", { tag: "FOUND" }),
  );
  render(<OperationLookup token="token" definition={definition} />);
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(screen.getByRole("button", { name: "Observe operation" }));
  expect(await screen.findByRole("alert")).toBeVisible();
});
