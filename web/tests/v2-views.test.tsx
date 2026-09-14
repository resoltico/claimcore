import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/v2";
import { CaseDetail } from "../src/views/CaseDetail";
import { CaseList } from "../src/views/CaseList";
import { Dashboard } from "../src/views/Dashboard";
import { generatedResponse, generatedWebValue } from "./contract-corpus.fixtures";
import { definition, fields, operationId, recoveryPage, response } from "./v2-ui.fixtures";

const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("lists, reloads, pages, selects and opens cases through typed list outcomes", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [{ caseReference: "CASE-1", revision: "1", status: "OPENED" }],
      nextCursor: "next",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [{ caseReference: "CASE-2", revision: "2", status: "CLOSED" }],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  const select = vi.fn();
  const open = vi.fn();
  render(<CaseList token="token" onSelect={select} onOpen={open} />);
  await user.click(await screen.findByRole("button", { name: "CASE-1" }));
  expect(select).toHaveBeenCalledWith("CASE-1");
  await user.click(screen.getByRole("button", { name: "Open new case" }));
  expect(open).toHaveBeenCalledOnce();
  await user.click(screen.getByRole("button", { name: "Load more cases" }));
  expect(await screen.findByRole("button", { name: "CASE-2" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Find case" }));
  expect(select).toHaveBeenCalledTimes(1);
  await user.type(screen.getByLabelText("Exact case reference"), "CASE-9");
  await user.click(screen.getByRole("button", { name: "Find case" }));
  expect(select).toHaveBeenCalledWith("CASE-9");
  await user.click(screen.getByRole("button", { name: "Reload cases" }));
});

it("shows current fields, server command labels, full expandable history and retryable history pages", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const history = generatedWebValue("case.history");
  if (history.outcome.tag !== "SUCCEEDED" || history.outcome.data.tag !== "FOUND")
    throw new Error("Generated history fixture must be found.");
  const full = history.outcome.data.entries.find((entry) => entry.tag === "FULL");
  if (full === undefined) throw new Error("Generated history fixture must include a full receipt.");
  fetch.mockResolvedValueOnce(response("case.get", "SUCCEEDED", { tag: "FOUND", current }));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", {
      ...history.outcome.data,
      entries: [
        ...history.outcome.data.entries,
        {
          tag: "FULL",
          receipt: {
            ...full.receipt,
            operationId: "20000000-0000-4000-8000-000000000002",
            replayed: true,
          },
        },
      ],
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", { tag: "FOUND", entries: [], nextCursor: null }),
  );
  const back = vi.fn();
  const command = vi.fn();
  render(
    <CaseDetail
      token="token"
      caseReference="CASE-1"
      reloadSignal={0}
      definition={definition.definition}
      onBack={back}
      onCommand={command}
    />,
  );
  expect(await screen.findByText("CASE-1")).toBeVisible();
  await user.click(screen.getByRole("button", { name: /Close the case/u }));
  expect(command).toHaveBeenCalledWith(current, "CLOSE");
  await user.click(screen.getAllByText(full.receipt.command)[0]!);
  expect(screen.getAllByText("synthetic-operator")).toHaveLength(2);
  expect(document.body).toHaveTextContent("exact replay");
  await user.click(screen.getByRole("button", { name: "Load more history" }));
  await user.click(screen.getByRole("button", { name: "Back to cases" }));
  expect(back).toHaveBeenCalledOnce();
});

it("navigates dashboard case, operation and recovery surfaces without hidden business dispatch", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  fetch.mockResolvedValueOnce(response("recovery.list", "SUCCEEDED", recoveryPage([])));
  render(<Dashboard token="token" sessionEpoch={1} onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Recovery" }));
  expect(await screen.findByRole("heading", { name: "Recovery" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Operations" }));
  expect(screen.getByRole("heading", { name: "Operation lookup" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Cases" }));
  expect(await screen.findByRole("heading", { name: "Cases" })).toBeVisible();
});

it("uses an exact operation lookup result and clears absent lookup presentation", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  fetch.mockResolvedValueOnce(generatedResponse("operation.observe"));
  render(<Dashboard token="token" sessionEpoch={1} onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Operations" }));
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(screen.getByRole("button", { name: "Observe operation" }));
  expect(await screen.findByText(/Accepted operation/u)).toBeVisible();
});

it("reports a failed list as a request-local error without manufacturing rows", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        kind: "HOST_FAILURE",
        code: "WEB_BUSY",
        message: "Busy",
        executionPhase: null,
      }),
      { status: 503, headers: { "content-type": "application/json" } },
    ),
  );
  render(<CaseList token="token" onSelect={vi.fn()} onOpen={vi.fn()} />);
  expect(await screen.findByRole("alert")).toHaveTextContent("WEB_BUSY");
  expect(screen.queryByRole("listitem")).toBeNull();
});

it("fails closed when the server definition is rejected or has a different Web fingerprint", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        kind: "HOST_FAILURE",
        code: "WEB_SESSION_REJECTED",
        message: "No",
        executionPhase: null,
      }),
      { status: 401, headers: { "content-type": "application/json" } },
    ),
  );
  const first = render(
    <Dashboard token="token" sessionEpoch={2} onLogout={vi.fn(() => Promise.resolve())} />,
  );
  expect(await screen.findByRole("alert")).toHaveTextContent("WEB_SESSION_REJECTED");
  first.unmount();
  fetch.mockResolvedValueOnce(
    response("definition", "DESCRIBED", { ...definition, webFingerprint: "0".repeat(64) }),
  );
  render(<Dashboard token="token" sessionEpoch={3} onLogout={vi.fn(() => Promise.resolve())} />);
  expect(await screen.findByRole("alert")).toHaveTextContent("does not match");
});

it("routes dashboard list selections and the new-case command into the typed editor", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [{ caseReference: "CASE-1", revision: "1", status: "OPENED" }],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [{ caseReference: "CASE-1", revision: "1", status: "OPENED" }],
      nextCursor: null,
    }),
  );
  render(<Dashboard token="token" sessionEpoch={4} onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Open new case" }));
  expect(screen.getByRole("heading", { name: "Open a case" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Back without preparing" }));
  await user.click(screen.getByRole("button", { name: "CASE-1" }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
});
