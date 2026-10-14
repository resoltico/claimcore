import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { CaseDetail } from "../src/views/CaseDetail";
import { CaseList } from "../src/views/CaseList";
import { Dashboard } from "../src/views/Dashboard";
import { generatedResponse, generatedWebValue } from "./contract-corpus.fixtures";
import { definition, fields, operationId, response } from "./service-ui.fixtures";

const current: CurrentCase = {
  state: "LIVE",
  disposition: "ACTIVE",
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("lists, reloads, pages, selects and opens cases through typed list outcomes", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [
        {
          state: "LIVE",
          disposition: "ACTIVE",
          caseReference: "CASE-1",
          revision: "1",
          status: "OPENED",
        },
      ],
      nextCursor: "next",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [
        {
          state: "LIVE",
          disposition: "ACTIVE",
          caseReference: "CASE-2",
          revision: "2",
          status: "CLOSED",
        },
      ],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  const select = vi.fn();
  const open = vi.fn();
  render(<CaseList token="token" onSelect={select} onOpen={open} />);
  await user.click(await screen.findByRole("button", { name: "CASE-1" }, { timeout: 5000 }));
  expect(select).toHaveBeenCalledWith("CASE-1");
  await user.click(screen.getByRole("button", { name: "Open a case" }));
  expect(open).toHaveBeenCalledOnce();
  await user.click(screen.getByRole("button", { name: "Load more cases" }));
  expect(await screen.findByRole("button", { name: "CASE-2" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Find case" }));
  expect(select).toHaveBeenCalledTimes(1);
  await user.type(screen.getByLabelText("Handler's case reference (exact)"), "CASE-9");
  await user.click(screen.getByRole("button", { name: "Find case" }));
  expect(select).toHaveBeenCalledWith("CASE-9");
  await user.click(screen.getByRole("button", { name: "Reload cases" }));
});

const historyWithTwoReceipts = () => {
  const history = generatedWebValue("case.history");
  if (history.outcome.tag !== "SUCCEEDED" || history.outcome.data.tag !== "FOUND") {
    throw new Error("Generated history fixture must be found.");
  }
  const full = history.outcome.data.entries.find((entry) => entry.tag === "FULL");
  if (full === undefined) {
    throw new Error("Generated history fixture must include a full receipt.");
  }
  return {
    ...history.outcome.data,
    entries: [
      full,
      {
        tag: "FULL",
        receipt: {
          ...full.receipt,
          operationId: "20000000-0000-4000-8000-000000000002",
        },
      },
    ],
  };
};

it("shows current fields, server command labels, full expandable history and retryable history pages", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const extendedHistory = historyWithTwoReceipts();
  fetch.mockResolvedValueOnce(response("case.get", "SUCCEEDED", { tag: "FOUND", current }));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", { tag: "FOUND", entries: [], nextCursor: null }),
  );
  fetch.mockResolvedValueOnce(response("case.history", "SUCCEEDED", extendedHistory));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", { tag: "FOUND", entries: [], nextCursor: null }),
  );
  const [back, command] = [vi.fn(), vi.fn()];
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
  await user.click(await screen.findByRole("button", { name: /Close the case/u }));
  expect(command).toHaveBeenCalledWith(current, "CLOSE");
  await user.selectOptions(screen.getByLabelText("History detail"), "FULL");
  await user.click((await screen.findAllByText(/Open a case · revision/u))[0]!);
  expect(screen.getAllByText(/10000000-0000-4000-8000-000000000001/u)).toHaveLength(3);
  expect(document.body).toHaveTextContent("accepted");
  await user.click(screen.getByRole("button", { name: "Load more history" }));
  await user.click(screen.getByRole("button", { name: "Back to cases" }));
  expect(back).toHaveBeenCalledOnce();
});

it("uses an exact operation lookup result and clears absent lookup presentation", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  fetch.mockResolvedValueOnce(generatedResponse("operation.observe"));
  render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Operations" }));
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(screen.getByRole("button", { name: "Look up recorded result" }));
  expect(await screen.findByText(/Accepted operation/u)).toBeVisible();
});

it("reports a failed list as a request-local error without manufacturing rows", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        kind: "HOST_FAILURE",
        code: "RATE_LIMITED",
        status: 429,
        diagnostic: { id: "HOST_RATELIMITED", parameters: {} },
        message: "Busy",
        executionPhase: "NOT_STARTED",
      }),
      { status: 429, headers: { "content-type": "application/json" } },
    ),
  );
  render(<CaseList token="token" onSelect={vi.fn()} onOpen={vi.fn()} />);
  expect(await screen.findByRole("alert")).toHaveTextContent("Request admission is busy.");
  expect(screen.queryByRole("listitem")).toBeNull();
});

it("keeps public metadata and other panes available when a case read is refused", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        kind: "HOST_FAILURE",
        code: "SESSION_REJECTED",
        status: 401,
        diagnostic: { id: "HOST_SESSIONREJECTED", parameters: {} },
        message: "No",
        executionPhase: "NOT_STARTED",
      }),
      { status: 401, headers: { "content-type": "application/json" } },
    ),
  );
  const first = render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
  expect(await screen.findByRole("alert")).toHaveTextContent("Authentication was refused.");
  expect(screen.getByRole("button", { name: "Operations" })).not.toBeDisabled();
  expect(screen.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  first.unmount();
});

it("routes dashboard list selections and the new-case command into the typed editor", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [
        {
          state: "LIVE",
          disposition: "ACTIVE",
          caseReference: "CASE-1",
          revision: "1",
          status: "OPENED",
        },
      ],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [
        {
          state: "LIVE",
          disposition: "ACTIVE",
          caseReference: "CASE-1",
          revision: "1",
          status: "OPENED",
        },
      ],
      nextCursor: null,
    }),
  );
  render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Open a case" }));
  expect(screen.getByRole("heading", { name: "Open a case" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Back to cases" }));
  await user.click(screen.getByRole("button", { name: "CASE-1" }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
});
