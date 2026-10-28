import { caseSummaryInput } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { caseAdvice } from "../support/case-advice.fixtures";
import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { CaseDetail } from "../../src/views/CaseDetail";
import { CaseList } from "../../src/views/CaseList";
import { Dashboard } from "../../src/views/Dashboard";
import { wireResponse, historyVariants } from "../support/contract-corpus.fixtures";
import { context, definition, fields, operationId, response } from "../support/service-ui.fixtures";
const current: CurrentCase = {
  caseReference: fields.caseReference,
  revision: 1,
  advice: {
    ...caseAdvice(["CLOSE"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields,
  },
};
beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn());
  stubDashboardContext();
});
const queueListPages = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE", status: "OPENED" },
          caseReference: "CASE-1",
          revision: 1,
        }),
      ],
      nextCursor: "next",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE", status: "CLOSED" },
          caseReference: "CASE-2",
          revision: 2,
        }),
      ],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [],
      nextCursor: null,
    }),
  );
};
it("lists, reloads, pages, selects and opens cases through typed list outcomes", async () => {
  const user = userEvent.setup();
  queueListPages();
  const select = vi.fn();
  const open = vi.fn();
  render(<CaseList onSelect={select} onOpen={open} />);
  await user.click(
    await screen.findByRole(
      "button",
      {
        name: "CASE-1",
      },
      {
        timeout: 5000,
      },
    ),
  );
  expect(select).toHaveBeenCalledWith("CASE-1");
  await user.click(getButton("Open a case"));
  expect(open).toHaveBeenCalledOnce();
  await user.click(getButton("Load more cases"));
  expect(await findButton("CASE-2")).toBeVisible();
  await user.click(getButton("Find case"));
  expect(select).toHaveBeenCalledTimes(1);
  await user.type(screen.getByLabelText("Handler's case reference (exact)"), "CASE-9");
  await user.click(getButton("Find case"));
  expect(select).toHaveBeenCalledWith("CASE-9");
  await user.click(getButton("Reload cases"));
});
const historyWithTwoReceipts = () => {
  const { full } = historyVariants();
  return {
    nextCursor: "next",
    entries: [
      full,
      {
        kind: "FULL",
        receipt: {
          ...full.receipt,
          operationId: "20000000-0000-4000-8000-000000000002",
        },
      },
    ],
  };
};
it("shows current fields, server act labels, full expandable history and retryable history pages", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const extendedHistory = historyWithTwoReceipts();
  fetch.mockResolvedValueOnce(response("case.get", "SUCCEEDED", current));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", {
      entries: [],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(response("case.history", "SUCCEEDED", extendedHistory));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", {
      entries: [],
      nextCursor: null,
    }),
  );
  const [back, act] = [vi.fn(), vi.fn()];
  render(
    <CaseDetail
      caseReference="CASE-1"
      reloadSignal={0}
      definition={definition.catalog}
      onBack={back}
      onAct={act}
    />,
  );
  expect(await screen.findByText("CASE-1")).toBeVisible();
  await user.click(await findButton(/Close the case/u));
  expect(act).toHaveBeenCalledWith(current, "CLOSE");
  await user.selectOptions(screen.getByLabelText("History detail"), "FULL");
  await user.click((await screen.findAllByText(/Open a case · revision/u))[0]!);
  expect(screen.getAllByText(/10000000-0000-4000-8000-000000000001/u)).toHaveLength(3);
  expect(document.body).toHaveTextContent("accepted");
  await user.click(getButton("Load more history"));
  await user.click(getButton("Back to cases"));
  expect(back).toHaveBeenCalledOnce();
});
it("uses an exact operation lookup result and clears absent lookup presentation", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(wireResponse("operation.observe"));
  render(<Dashboard />);
  await user.click(await screen.findByRole("link", { name: "Operations" }));
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(getButton("Look up recorded result"));
  expect(await screen.findByText(/Accepted operation/u)).toBeVisible();
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
  render(<CaseList onSelect={vi.fn()} onOpen={vi.fn()} />);
  expect(await screen.findByRole("alert")).toHaveTextContent("Request admission is busy.");
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
  expect(await screen.findByRole("alert")).toHaveTextContent("Authentication was refused.");
  expect(screen.getByRole("link", { name: "Operations" })).not.toHaveAttribute(
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
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE", status: "OPENED" },
          caseReference: "CASE-1",
          revision: 1,
        }),
      ],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE", status: "OPENED" },
          caseReference: "CASE-1",
          revision: 1,
        }),
      ],
      nextCursor: null,
    }),
  );
  render(<Dashboard />);
  await user.click(await findButton("Open a case"));
  expect(
    screen.getByRole("heading", {
      name: "Open a case",
    }),
  ).toBeVisible();
  await user.click(getButton("Back to cases"));
  await user.click(getButton("CASE-1"));
  expect(
    await screen.findByRole("heading", {
      name: "Case detail",
    }),
  ).toBeVisible();
});
