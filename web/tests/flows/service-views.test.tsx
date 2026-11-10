import { caseDocumentInput, closeCaseScreen } from "../support/case-document.fixtures";
import {
  humanReadHttpResponse as response,
  humanReadResponse,
} from "../support/human-read-response.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";

import { mockEndpoint } from "../support/endpoint-mocks";
import { timelinePage, timelineEntry } from "../support/timeline.fixtures";
import { navigate } from "../../src/routing/navigation";
import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { caseSummaryInput, workBasis } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";

import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { CaseDetail } from "../../src/views/CaseDetail";
import { CaseList } from "../../src/views/CaseList";
import { Dashboard } from "../../src/views/Dashboard";
import { wireReply } from "../support/reply.fixtures";
import { context, definition, operationId } from "../support/service-ui.fixtures";
const current: CurrentCase = caseDocumentInput(
  {
    tasks: ["CASE:CLOSE"],
    caseId: "20000000-0000-4000-8000-000000000001",
    revision: 1,
    history: "FULL",
  },
  closeCaseScreen,
);
beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn());
  stubDashboardContext();
});
const queueListPages = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      basis: workBasis,
      exactReference: null,
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE" },
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "CASE-1",
          revision: 1,
        }),
      ],
      nextCursor: "next",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      basis: workBasis,
      exactReference: null,
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE" },
          caseId: "20000000-0000-4000-8000-000000000002",
          caseReference: "CASE-2",
          revision: 2,
          headline: "CLOSED",
        }),
      ],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      basis: workBasis,
      exactReference: null,
      offers: context.offers,
      items: [],
      nextCursor: null,
    }),
  );
};
it("lists, reloads, pages and selects cases through typed list outcomes", async () => {
  const user = userEvent.setup();
  queueListPages();
  const select = vi.fn();
  render(<CaseList onSelect={select} />);
  await user.click(
    await screen.findByRole(
      "link",
      {
        name: "CASE-1",
      },
      {
        timeout: 5000,
      },
    ),
  );
  expect(select).toHaveBeenCalledWith({
    kind: "ID",
    caseId: "20000000-0000-4000-8000-000000000001",
  });
  await user.click(getButton("Load more cases"));
  expect(await screen.findByRole("link", { name: "CASE-2" })).toBeVisible();
  await user.click(screen.getByText("Open by exact reference", { selector: "summary" }));
  await user.click(getButton("Open by exact reference"));
  expect(select).toHaveBeenCalledTimes(1);
  await user.type(screen.getByLabelText("Case reference (exact)"), "CASE-9");
  await user.click(getButton("Open by exact reference"));
  expect(select).toHaveBeenCalledTimes(2);
  expect(select).toHaveBeenLastCalledWith({ kind: "REFERENCE", caseReference: "CASE-9" });
  await user.click(getButton("Reload cases"));
});
const historyWithTwoReceipts = () =>
  timelinePage(
    [
      timelineEntry(),
      timelineEntry({
        position: { sequence: 2, effectIndex: 0 },
        operationHash: "b".repeat(64),
        operationId: "20000000-0000-7000-8000-000000000002",
      }),
    ],
    "next",
  );
it("shows current fields, server act labels, full expandable history and retryable history pages", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const extendedHistory = historyWithTwoReceipts();
  fetch.mockResolvedValueOnce(
    endpointResponse("case.get", {
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: current },
    }),
  );
  fetch.mockResolvedValueOnce(response("case.timeline", "SUCCEEDED", extendedHistory));
  fetch.mockResolvedValueOnce(response("case.timeline", "SUCCEEDED", timelinePage()));
  const [back, act] = [vi.fn(), vi.fn()];
  render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "CASE-1" }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={back}
      onAct={act}
    />,
  );
  expect(await screen.findByText("CASE-1")).toBeVisible();
  await user.click(await findButton(/Close the case/u));
  expect(act).toHaveBeenCalledWith(expect.objectContaining(current), "CLOSE");
  const entry = (await screen.findAllByText("Timeline recorder"))[0]!.closest("li")!;
  expect(entry).toHaveTextContent("Timeline recorder");
  await user.click(getButton("Load more"));
  await user.click(getButton("Back to cases"));
  expect(back).toHaveBeenCalledOnce();
});
it("uses an exact operation lookup result and clears absent lookup presentation", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const reply = wireReply("operation.get");
  if (reply.outcome.kind !== "ACCEPTED") {
    throw new Error("Current receipt fixture required");
  }
  const { document } = reply.outcome;
  fetch.mockImplementation(() =>
    Promise.resolve(
      endpointResponse("operation.get", {
        ...reply,
        outcome: {
          ...reply.outcome,
          document: {
            ...document,
            screen: {
              regions: [
                {
                  role: "DETAILS",
                  titleId: "label.recordDetails",
                  content: {
                    kind: "ENTRIES",
                    entries: [
                      {
                        key: "operationId",
                        labelId: "field.operationId.label",
                        prominence: "DETAIL",
                        value: { kind: "IDENTIFIER", value: operationId },
                      },
                    ],
                  },
                },
              ],
            },
          },
        },
      }),
    ),
  );
  navigate("/recovery");
  render(<Dashboard />);
  await user.click(await screen.findByText("General operation lookup", { selector: "summary" }));
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(getButton("Look up recorded result"));
  await user.click(await screen.findByText("Record details", { selector: "summary" }));
  expect(screen.getByText(operationId, { selector: ".identifier-value" })).toBeVisible();
});
it("reports a failed list as a request-local error without manufacturing rows", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        hostFailure: {
          code: "RATE_LIMITED",
          phase: "NOT_STARTED",
        },
      }),
      {
        status: 429,
        headers: {
          "content-type": "application/json",
        },
      },
    ),
  );
  render(<CaseList onSelect={vi.fn()} />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "ClaimCore is busy. Try again in a moment.",
  );
  expect(screen.queryByRole("listitem")).toBeNull();
});
it("keeps public metadata and other panes available when a case read is refused", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        hostFailure: {
          code: "SESSION_REJECTED",
          phase: "NOT_STARTED",
        },
      }),
      {
        status: 401,
        headers: {
          "content-type": "application/json",
        },
      },
    ),
  );
  const first = render(<Dashboard />);
  expect(await screen.findByRole("alert")).toHaveTextContent("You are signed out. Sign in again.");
  expect(document.querySelector('.workspace-sidebar a[href="/people"]')).not.toHaveAttribute(
    "aria-disabled",
    "true",
  );
  expect(
    screen.getByRole("link", {
      name: "ClaimCore",
    }),
  ).toBeVisible();
  first.unmount();
});
it("routes dashboard list selections and the new-case act into the typed editor", async () => {
  mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: current },
    },
  });
  mockEndpoint("case.timeline").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadResponse("case.timeline", "SUCCEEDED", timelinePage()),
    },
  });
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      basis: workBasis,
      exactReference: null,
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE" },
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "CASE-1",
          revision: 1,
        }),
      ],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      basis: workBasis,
      exactReference: null,
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE" },
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "CASE-1",
          revision: 1,
        }),
      ],
      nextCursor: null,
    }),
  );
  render(<Dashboard />);
  await user.click(await screen.findByRole("link", { name: "Open a case" }));
  expect(
    await screen.findByRole("heading", {
      name: "Open a case",
    }),
  ).toBeVisible();
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  await user.click(screen.getByRole("link", { name: "CASE-1" }));
  expect(
    await screen.findByRole("heading", {
      name: (_name, element) => element.id === "case-detail-title",
    }),
  ).toBeVisible();
});

beforeEach(() => {
  stubWorklistSummary();
});
