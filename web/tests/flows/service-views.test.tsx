import { casesBreadcrumb } from "../support/workspace-actions";
import { withRecommendation, operationAffordance } from "../support/affordances.fixtures";
import {
  caseSubject,
  caseDocumentInput,
  caseIdentityScreen,
} from "../support/case-document.fixtures";

import {
  humanReadHttpReply as response,
  humanReadReply,
} from "../support/human-read-reply.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";

import { mockEndpoint } from "../support/endpoint-mocks";
import { timelineDocument, openedTimelineEntry } from "../support/timeline-document.fixtures";
import { act as flush, waitFor } from "@testing-library/react";
import { csvExportTransport } from "@contracts/client";
import { activeRequester } from "../support/human-requester.fixtures";
import { navigate, setNavigationBlocker } from "../../src/routing/navigation";
import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { workBasis } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";

import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { CaseDetail } from "../../src/features/case/CaseDetail";
import { CaseList } from "../../src/features/work/CaseList";
import { Dashboard } from "../../src/shell/Dashboard";
import { wireReply } from "../support/reply.fixtures";
import { definition, operationId } from "../support/service-ui.fixtures";
const current: CurrentCase = withRecommendation(
  caseDocumentInput(
    {
      subject: { ...caseSubject, caseId: "20000000-0000-4000-8000-000000000001", revision: 1 },
      actions: [
        operationAffordance(
          "case.close",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 1,
          },
          {},
          undefined,
          {},
        ),
      ],
      history: "FULL",
    },
    caseIdentityScreen,
  ),
  "case.close",
);
beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn());
  stubDashboardContext();
});
const queueListPages = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "caseReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  joins: null,
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: "20000000-0000-4000-8000-000000000001",
                  link: {
                    endpoint: "case.get",
                    input: {
                      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
                    },
                  },
                  values: [{ kind: "REFERENCE", value: "CC-55KW-SSJ4-8HMS-G1BH-73" }],
                },
              ],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [
        operationAffordance("case.open", { kind: "CASE", expectedRevision: 0 }, {}, undefined, {}),
      ],
      links: [],
      history: null,
      nextCursor: "next",
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "caseReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  joins: null,
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: "20000000-0000-4000-8000-000000000002",
                  link: {
                    endpoint: "case.get",
                    input: {
                      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000002" },
                    },
                  },
                  values: [{ kind: "REFERENCE", value: "CC-6XBX-RTDQ-XE13-CG4W-91" }],
                },
              ],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [
        operationAffordance("case.open", { kind: "CASE", expectedRevision: 0 }, {}, undefined, {}),
      ],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "caseReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  joins: null,
                  isMoney: false,
                },
              ],
              rows: [],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [
        operationAffordance("case.open", { kind: "CASE", expectedRevision: 0 }, {}, undefined, {}),
      ],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
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
        name: "CC-55KW-SSJ4-8HMS-G1BH-73",
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
  await user.click(getButton("Load more"));
  expect(await screen.findByRole("link", { name: "CC-6XBX-RTDQ-XE13-CG4W-91" })).toBeVisible();
  await user.click(getButton("Refresh"));
  expect(select).toHaveBeenCalledTimes(1);
});
const historyWithTwoReceipts = () =>
  timelineDocument([openedTimelineEntry, { ...openedTimelineEntry, key: "2/0" }], "next");
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
  fetch.mockResolvedValueOnce(response("case.timeline", extendedHistory));
  fetch.mockResolvedValueOnce(response("case.timeline", timelineDocument()));
  const [back, act] = [vi.fn(), vi.fn()];
  render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "CC-55KW-SSJ4-8HMS-G1BH-73" }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={back}
      onAct={act}
    />,
  );
  expect(await screen.findByRole("heading", { name: "CC-55KW-SSJ4-8HMS-G1BH-73" })).toBeVisible();
  await user.click(await findButton(/Close the case/u));
  expect(act).toHaveBeenCalledWith(
    expect.objectContaining(current),
    expect.objectContaining({ task: "case.close" }),
    expect.any(String),
  );
  const entry = (await screen.findAllByText("Timeline recorder"))[0]!.closest("li")!;
  expect(entry).toHaveTextContent("Timeline recorder");
  await user.click(getButton("Load more"));
  await user.click(casesBreadcrumb());
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
                  titleId: "label.technicalDetails",
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
  expect(await screen.findByRole("heading", { name: "General operation lookup" })).toBeVisible();
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(getButton("Look up recorded result"));
  await user.click(await screen.findByRole("button", { name: "Technical details" }));
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
      ...humanReadReply("case.timeline", timelineDocument()),
    },
  });
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "caseReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  joins: null,
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: "20000000-0000-4000-8000-000000000001",
                  link: {
                    endpoint: "case.get",
                    input: {
                      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
                    },
                  },
                  values: [{ kind: "REFERENCE", value: "CC-55KW-SSJ4-8HMS-G1BH-73" }],
                },
              ],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [
        operationAffordance("case.open", { kind: "CASE", expectedRevision: 0 }, {}, undefined, {}),
      ],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "caseReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  joins: null,
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: "20000000-0000-4000-8000-000000000001",
                  link: {
                    endpoint: "case.get",
                    input: {
                      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
                    },
                  },
                  values: [{ kind: "REFERENCE", value: "CC-55KW-SSJ4-8HMS-G1BH-73" }],
                },
              ],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [
        operationAffordance("case.open", { kind: "CASE", expectedRevision: 0 }, {}, undefined, {}),
      ],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
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
  await user.click(screen.getByRole("link", { name: "CC-55KW-SSJ4-8HMS-G1BH-73" }));
  expect(
    await screen.findByRole("heading", {
      name: (_name, element) => element.id === "case-detail-title",
    }),
  ).toBeVisible();
});

beforeEach(() => {
  stubWorklistSummary();
});

it("retains the current worklist draft during a guarded Find navigation", async () => {
  navigate("/cases?view=ALL&status=OPENED", true);
  const user = userEvent.setup();
  render(<Dashboard />);
  await screen.findByRole("tab", { name: /^All /u, selected: true });
  const original = screen.getByRole("searchbox");
  await user.type(original, "unsent draft");

  let confirm: (() => void) | undefined;
  const unregister = setNavigationBlocker((proceed) => {
    confirm = proceed;
    return false;
  });
  try {
    await user.click(screen.getByRole("button", { name: /^Find a case/u }));
    await user.type(
      screen.getByRole("combobox", { name: "Find a case" }),
      "matching claimants{Enter}",
    );
    expect(location.search).toBe("?view=ALL&status=OPENED");
    expect(original).toHaveValue("unsent draft");
    expect(confirm).toBeTypeOf("function");

    await flush(() =>
      Promise.resolve().then(() => {
        confirm?.();
      }),
    );
    await waitFor(() => {
      expect(screen.queryByRole("dialog", { name: "Find a case" })).toBeNull();
      const query = new URLSearchParams(location.search);
      expect(query.get("view")).toBe("ALL");
      expect(query.get("q")).toBe("matching claimants");
      expect(query.has("status")).toBe(false);
      expect(screen.getByRole("searchbox")).toHaveValue("matching claimants");
    });
  } finally {
    unregister();
  }
});

it.each(["clear", "apply"])("%s of the last viewless filter keeps All", async (action) => {
  navigate("/cases?status=CLOSED", true);
  const user = userEvent.setup();
  const empty = humanReadReply("case.list", {
    ...timelineDocument([]),
    disclosure: "CURRENT",
  });
  const list = mockEndpoint("case.list").mockResolvedValue({
    kind: "outcome",
    value: empty,
    status: 200,
  });
  render(<Dashboard />);
  await waitFor(() => {
    expect(list).toHaveBeenCalledWith(
      "token",
      { limit: 50, status: ["CLOSED"] },
      expect.anything(),
    );
  });
  if (action === "clear") {
    await user.click(await screen.findByRole("button", { name: "Clear filters" }));
  } else {
    await user.click(await screen.findByRole("button", { name: "Filters (1)" }));
    await user.click(screen.getByRole("checkbox", { name: "Closed" }));
    await user.click(screen.getByRole("button", { name: "Apply filters" }));
  }
  await waitFor(() => {
    expect(location.search).toBe("?view=ALL");
  });
});

it.each([
  ["case.list", "/cases?view=ALL", "Case list (CSV)"],
  ["case.summary", "/cases/report?view=ALL", "Export report CSV"],
  ["people.history", "/people-history", "Download CSV"],
  ["case.timeline", "/cases/id/20000000-0000-4000-8000-000000000001", "Download CSV"],
] as const)("retires a %s export when Settings parks its route", async (endpoint, path, label) => {
  navigate(path, true);
  const { installationId } = activeRequester.subject;
  stubDashboardContext().mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("context", {
      ...activeRequester,
      links: [
        ...activeRequester.links,
        {
          rel: "VIEW",
          labelId: "endpoint.people.history.label",
          link: { endpoint: "people.history", input: { limit: 20 } },
        },
      ],
    }),
  });
  mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.get", current),
  });
  const document = timelineDocument([]);
  mockEndpoint(endpoint).mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: { ...document, disclosure: endpoint === "case.timeline" ? "FULL" : "CURRENT" },
      },
    },
  });
  const create = vi.fn(() => "blob:export");
  const NativeURL = URL;
  vi.stubGlobal(
    "URL",
    class extends NativeURL {
      static override createObjectURL = create;
      static override revokeObjectURL = vi.fn();
    },
  );
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(vi.fn());
  const pending: {
    reply: PromiseWithResolvers<Response>;
    signal: AbortSignal | null | undefined;
  }[] = [];
  vi.mocked(fetch).mockImplementation((_, init) => {
    const reply = Promise.withResolvers<Response>();
    pending.push({ reply, signal: init?.signal });
    return reply.promise;
  });
  const payload = "Scope,record\r\n";
  const download = () =>
    new Response(payload, {
      headers: {
        "content-type": csvExportTransport.mediaType,
        "content-length": String(payload.length),
        "content-encoding": "identity",
        "content-disposition": `attachment; filename="${csvExportTransport.filenames[endpoint]}"`,
        "ClaimCore-Installation": installationId,
      },
    });

  const user = userEvent.setup();
  render(<Dashboard />);
  if (endpoint === "case.summary") {
    await user.click(await screen.findByRole("combobox", { name: "Group by" }));
    await user.click(await screen.findByRole("option", { name: "Received month" }));
  }
  const startExport = async () => {
    if (endpoint === "case.list") {
      await screen.findByText("0 cases shown");
      await user.click(await screen.findByRole("button", { name: "Export" }));
      const item = await screen.findByRole("menuitem", { name: label });
      await waitFor(() => {
        expect(item).not.toHaveAttribute("aria-disabled", "true");
      });
      await user.click(item);
    } else {
      const button = await screen.findByRole("button", { name: label });
      await waitFor(() => {
        expect(button).toBeEnabled();
      });
      await user.click(button);
    }
  };
  await startExport();
  await waitFor(() => {
    expect(pending).toHaveLength(1);
  });
  await flush(() =>
    Promise.resolve().then(() => {
      pending[0]!.reply.resolve(download());
    }),
  );
  await waitFor(() => {
    expect(create).toHaveBeenCalledTimes(1);
  });

  await startExport();
  await waitFor(() => {
    expect(pending).toHaveLength(2);
  });
  await flush(() =>
    Promise.resolve().then(() => {
      navigate("/settings");
    }),
  );
  await screen.findByRole("heading", { name: "Settings" });
  expect(pending[1]!.signal?.aborted).toBe(true);

  // Even a transport that delivers after abort cannot create a Blob, including after return.
  await flush(() =>
    Promise.resolve().then(() => {
      navigate(path);
    }),
  );
  await flush(() =>
    Promise.resolve().then(() => {
      pending[1]!.reply.resolve(download());
    }),
  );
  expect(create).toHaveBeenCalledTimes(1);
  if (endpoint === "case.list") {
    await user.click(await screen.findByRole("button", { name: "Export" }));
    expect(await screen.findByRole("menuitem", { name: label })).not.toHaveAttribute(
      "aria-disabled",
      "true",
    );
    await user.keyboard("{Escape}");
  } else {
    await waitFor(() => {
      expect(screen.getByRole("button", { name: label })).toBeEnabled();
    });
  }
});
