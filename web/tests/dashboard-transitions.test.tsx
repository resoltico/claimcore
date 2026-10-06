import { fireEvent, render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/v3";
import { Dashboard } from "../src/views/Dashboard";
import { receipt, list, inspection, accepted } from "./v3-recovery.fixtures";
import { deferredResponse } from "./presentation-state.fixtures";
import { preparedForRequest } from "./prepared-request.fixtures";
import { definition, fields, preparation, response, recoveryPage } from "./v3-ui.fixtures";

const current: CurrentCase = { case: { fields, revision: "1" }, availableCommands: ["CLOSE"] };

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

const queueDefinitionAndList = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [{ caseReference: "CASE-1", revision: "1", status: "OPENED" }],
      nextCursor: null,
    }),
  );
};

const queueDetail = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("case.get", "SUCCEEDED", { tag: "FOUND", current }));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", { tag: "FOUND", entries: [], nextCursor: null }),
  );
  fetch.mockResolvedValueOnce(response("lifecycle.review", "RESOURCE_UNAVAILABLE", null));
};

const queueAcceptedOpen = (): void => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("command.execute", "OBSERVED_ACCEPTED", {
      receipt: { ...receipt, snapshot: { fields, revision: "1" } },
    }),
  );
};

const sentOperationId = (path: string): string => {
  const body = vi
    .mocked(globalThis.fetch)
    .mock.calls.find(([candidate]) => candidate === path)?.[1]?.body;
  const draft: unknown = JSON.parse(typeof body === "string" ? body : "");
  if (
    typeof draft !== "object" ||
    draft === null ||
    !("operationId" in draft) ||
    typeof draft.operationId !== "string"
  ) {
    throw new Error("Expected a synthetic operation identity.");
  }
  return draft.operationId;
};

const renderDashboard = () =>
  render(<Dashboard token="token" sessionEpoch={1} onLogout={vi.fn(() => Promise.resolve())} />);

it("returns from a selected case through Dashboard's detail-back transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  // Cold on-demand schema loading is not a one-second UI latency contract.
  await user.click(await screen.findByRole("button", { name: "CASE-1" }, { timeout: 5_000 }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Back to cases" }));
  expect(await screen.findByRole("heading", { name: "Cases" })).toBeVisible();
});

it("opens a metadata-derived command from Dashboard's current-case transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "CASE-1" }));
  await user.click(await screen.findByRole("button", { name: /Close the case/u }));
  expect(screen.getByRole("heading", { name: "Close the case" })).toBeVisible();
  expect(screen.getByRole("button", { name: "Recovery" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Sign out" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
});

it("hands a lost submit to exact recovery even when the pending list is empty", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueDefinitionAndList();
  queueDetail();
  fetch.mockImplementationOnce(
    preparedForRequest(
      response("command.prepare", "PREPARED", {
        details: preparation,
        review: {
          before: current.case,
          proposed: { fields, revision: "2" },
          changes: [],
          context: definition.runtime,
          advisory: true,
        },
      }),
    ),
  );
  fetch.mockRejectedValueOnce(new Error("Synthetic response loss"));
  fetch.mockResolvedValueOnce(response("recovery.list", "SUCCEEDED", recoveryPage([])));
  fetch.mockResolvedValueOnce(
    response("recovery.inspect", "SUCCEEDED", {
      tag: "NOT_FOUND",
      identity: preparation.summary.operationId,
    }),
  );
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "CASE-1" }));
  await user.click(await screen.findByRole("button", { name: /Close the case/u }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  expect(await screen.findByRole("heading", { name: "Recovery" })).toBeVisible();
  expect(screen.queryByRole("textbox")).toBeNull();
  expect(screen.queryByText(fields.claimantName)).toBeNull();
  expect(screen.getByRole("button", { name: "Sign out" })).not.toBeDisabled();
  const operationId = sentOperationId("/api/v3/operations/submit");
  expect(screen.getByRole("alert")).toHaveTextContent(operationId);
  await user.click(screen.getByRole("button", { name: "Inspect" }));
  expect(sentOperationId("/api/v3/recovery/inspect")).toBe(operationId);
  await user.click(screen.getByRole("button", { name: "Operations" }));
  expect(screen.getByLabelText("Exact operation ID")).toHaveValue(operationId);
  expect(fetch.mock.calls.filter(([path]) => path === "/api/v3/operations/submit")).toHaveLength(1);
});

it("locks Dashboard during recovery dispatch and keeps the identity after lost resolution", async () => {
  const user = userEvent.setup();
  const pending = deferredResponse();
  const fetch = vi.mocked(globalThis.fetch);
  queueDefinitionAndList();
  fetch
    .mockResolvedValueOnce(list())
    .mockResolvedValueOnce(inspection())
    .mockReturnValueOnce(pending.promise)
    .mockResolvedValueOnce(list([]));
  renderDashboard();
  await screen.findByRole("heading", { name: "Cases" });
  await user.click(screen.getByRole("button", { name: "Recovery" }));
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(await screen.findByRole("button", { name: "Resolve exact preparation" }));
  await user.click(await screen.findByRole("button", { name: "Confirm resolve" }));
  expect(screen.getByRole("button", { name: "Cases", hidden: true })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Sign out", hidden: true })).toBeDisabled();
  pending.reject(new Error("Synthetic lost resolution"));
  expect(await screen.findByRole("alert")).toHaveTextContent(preparation.summary.operationId);
  expect(screen.getByRole("alert")).toHaveTextContent(preparation.summary.requestSha256!);
  expect(screen.getByRole("button", { name: "Sign out" })).not.toBeDisabled();
  expect(screen.queryByRole("button", { name: "Resolve exact preparation" })).toBeNull();
  expect(fetch.mock.calls.filter(([path]) => path === "/api/v3/recovery/resolve")).toHaveLength(1);
});

it("reports definite accepted recovery knowledge and keeps its observation target", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  const fetch = vi.mocked(globalThis.fetch);
  fetch
    .mockResolvedValueOnce(list())
    .mockResolvedValueOnce(inspection())
    .mockResolvedValueOnce(accepted())
    .mockResolvedValueOnce(list([]));
  renderDashboard();
  await screen.findByRole("heading", { name: "Cases" });
  await user.click(screen.getByRole("button", { name: "Recovery" }));
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(await screen.findByRole("button", { name: "Resolve exact preparation" }));
  await user.click(await screen.findByRole("button", { name: "Confirm resolve" }));
  expect(await screen.findByRole("status")).toHaveTextContent(
    `Accepted exact operation ${preparation.summary.operationId}`,
  );
  expect(screen.queryByRole("alert")).toBeNull();
  await user.click(screen.getByRole("button", { name: "Operations" }));
  expect(screen.getByLabelText("Exact operation ID")).toHaveValue(preparation.summary.operationId);
});

it("returns an accepted open operation to Dashboard through its committed transition", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueDefinitionAndList();
  fetch.mockImplementationOnce(
    preparedForRequest(
      response("command.prepare", "PREPARED", {
        details: preparation,
        review: {
          before: null,
          proposed: { fields, revision: "1" },
          changes: [],
          context: definition.runtime,
          advisory: true,
        },
      }),
    ),
  );
  queueAcceptedOpen();
  queueDetail();
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "Open new case" }));
  const fill = (label: RegExp, value: string): void => {
    fireEvent.change(screen.getByLabelText(label), { target: { value } });
  };
  fill(/Handler's case reference/u, "CASE-1");
  fill(/^Incident date/u, "2026-09-01");
  fill(/Incident notification date/u, "2026-09-02");
  fill(/Country of incident/u, "Latvia");
  fill(/Claimant name/u, "Synthetic claimant");
  fill(/Responsible insurer/u, "Synthetic insurer");
  fill(/Amount claimed/u, "12.34");
  fill(/Currency of claimed amount/u, "EUR");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  await user.click(await screen.findByRole("button", { name: "Return to case" }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
  const currentRead = fetch.mock.calls.findLast(([path]) => path === "/api/v3/cases/get");
  expect(JSON.parse(typeof currentRead?.[1]?.body === "string" ? currentRead[1].body : "")).toEqual(
    {
      caseReference: fields.caseReference,
    },
  );
});
