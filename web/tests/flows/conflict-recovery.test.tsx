import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { selectFact } from "../support/field-entry";
import { currentCaseInput, actorRefInput, caseViewInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import { timelineEntry, timelinePage } from "../support/timeline.fixtures";
import { definition, fields } from "../support/service-ui.fixtures";
import { OperationEditor } from "../../src/views/OperationEditor";
import { isRequest, isResponseFor } from "../../src/protocol/client";
import type { Diagnostic_REVISION_CONFLICT, RequestByEndpoint, Screen } from "@contracts/client";
import { endpointResponse } from "../support/transport-response.fixtures";
import { humanReadResponse } from "../support/human-read-response.fixtures";
import { taskForCommand } from "../../src/protocol/taskCatalog";
import { timelineFixtureView } from "../support/read-view.fixtures";
const requestBody = (init?: RequestInit): string => {
  if (typeof init?.body !== "string") {
    throw Error("Serialized request required");
  }
  return init.body;
};
const suppliedContinuation = (): Screen => ({
  regions: [
    {
      role: "RESULTS",
      content: {
        kind: "TABLE",
        sections: [],
        columns: [
          {
            key: "server-continuation",
            labelId: "label.changes",
            prominence: "PRIMARY",
            isMoney: false,
          },
        ],
        rows: (
          [
            [{ kind: "TEXT", value: "SUPPLIED CONTINUATION ONLY" }],
            [{ kind: "TEXT", value: "SECOND SUPPLIED ROW" }],
          ] as const
        ).map((values, index) => ({
          key: String(index),
          values,
        })),
      },
    },
  ],
});
const basis = {
  observedAt: "2026-11-04T12:00:00.000000Z",
  businessDate: "2026-11-04",
  timeZone: "Etc/UTC",
};
const timelineResponse = (
  _endpoint: "case.timeline",
  _kind: "SUCCEEDED",
  data: unknown,
  _status: 200,
  readScreen: Screen,
): Response => {
  const reply = humanReadResponse("case.timeline", "SUCCEEDED", data);
  if (reply.outcome.kind !== "SUCCEEDED") {
    throw new Error("Timeline fixture required");
  }
  return endpointResponse("case.timeline", {
    ...reply,
    outcome: { ...reply.outcome, document: { ...reply.outcome.document, screen: readScreen } },
  });
};
const conflictRejection = (
  endpoint: "operation.preview" | "case.get" | "case.timeline",
  data: unknown,
): Response => {
  const facts: unknown = {
    representation: "DATA",
    operationId: "10000000-0000-7000-8000-000000000001",
    outcome: { kind: "REJECTED", data },
  };
  if (!isResponseFor("operation.preview", "DATA", facts) || facts.outcome.kind !== "REJECTED") {
    throw new Error("Authored conflict diagnostic must follow the current DATA contract");
  }
  const diagnostic = facts.outcome.data;
  if (diagnostic.code === "REVISION_CONFLICT") {
    const { changes } = diagnostic;
    return endpointResponse(endpoint, {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "CONFLICT",
          advice: {
            message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
            action: "REVIEW_LATEST",
          },
          actualRevision: diagnostic.actualRevision,
          document: {
            screen:
              changes.kind === "PAGE"
                ? timelineFixtureView(changes.page.entries)
                : {
                    regions: [
                      {
                        role: "NOTICE",
                        content: {
                          kind: "ENTRIES",
                          entries: [
                            {
                              key: "withheld",
                              labelId: "label.changes",
                              prominence: "PRIMARY",
                              value: { kind: "MESSAGE", messageId: "label.changesWithheld" },
                            },
                          ],
                        },
                      },
                    ],
                  },
            context: {
              kind: "CONTINUATION",
              nextCursor: changes.kind === "PAGE" ? changes.page.nextCursor : null,
            },
            basis: { kind: "SNAPSHOT", observation: basis },
            disclosure: changes.kind === "PAGE" ? changes.page.disclosure : "WITHHELD",
          },
        },
      },
    });
  }
  if (diagnostic.code === "INVALID_INPUT") {
    return endpointResponse(endpoint, {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "INPUT",
          advice: {
            message: {
              messageId: "diagnostic.INVALID_INPUT",
              arguments: [
                { key: "issueCount", value: { kind: "COUNT", value: diagnostic.issues.length } },
              ],
            },
            action: "CORRECT_INPUT",
          },
          issues: diagnostic.issues.map((issue) => {
            if (issue.field !== "claimantName" || issue.code !== "TEXT_REQUIRED") {
              throw new Error(
                "This conflict interaction fixture supplies only the authored claimant-name issue",
              );
            }
            return {
              input: "field:claimantName",
              row: null,
              related: null,
              message: { messageId: "diagnostic.TEXT_REQUIRED", arguments: [] },
            };
          }),
        },
      },
    });
  }
  if (diagnostic.code !== "RESOURCE_UNAVAILABLE" && diagnostic.code !== "INVALID_CURSOR") {
    throw new Error("Unsupported conflict fixture refusal");
  }
  return endpointResponse(endpoint, {
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "REFUSED",
        advice: {
          message: { messageId: `diagnostic.${diagnostic.code}`, arguments: [] },
          action: diagnostic.action,
        },
      },
    },
  });
};
const response = (
  endpoint: "operation.preview" | "case.get" | "case.timeline",
  kind: "REJECTED" | "SUCCEEDED",
  data: unknown,
): Response => {
  if (kind === "SUCCEEDED") {
    if (endpoint === "case.get") {
      const reply: unknown = {
        representation: "HUMAN",
        outcome: { kind: "SUCCEEDED", document: data },
      };
      if (isResponseFor("case.get", "HUMAN", reply)) {
        return endpointResponse(endpoint, reply);
      }
      return endpointResponse(endpoint, humanReadResponse("case.get", "SUCCEEDED", data));
    }
    if (endpoint === "case.timeline") {
      return endpointResponse(endpoint, humanReadResponse("case.timeline", "SUCCEEDED", data));
    }
    throw new Error("Unsupported conflict fixture success");
  }
  return conflictRejection(endpoint, data);
};
const current = (
  revision: number,
  name = "Original name",
  history: "NONE" | "SUMMARY" | "FULL" = "FULL",
) => {
  const value = currentCaseInput({
    caseReference: "CASE-1",
    revision,
    advice: {
      ...caseAdvice(["AMEND_REGISTRATION"]),
      history,
    },
    state: {
      kind: "ACTIVE",
      fields: { ...fields, registration: { ...fields.registration, claimantName: name } },
    },
  });
  return { ...value, basis: { kind: "SNAPSHOT" as const, observation: basis } };
};
const inputFrom = (init?: RequestInit): RequestByEndpoint["operation.preview"] => {
  if (typeof init?.body !== "string") {
    throw new Error("Expected a serialized preview frame.");
  }
  const input: unknown = JSON.parse(init.body);
  if (!isRequest("operation.preview", input)) {
    throw new Error("Expected the production request shape.");
  }
  return input;
};
const fact = (revision: number) =>
  timelineEntry({
    act: "AMEND_REGISTRATION",
    revision,
    position: { sequence: revision, effectIndex: 0 },
    operationHash: "a".repeat(64),
    operationId: `10000000-0000-7000-8000-${String(revision).padStart(12, "0")}`,
    recordedBy: actorRefInput({ displayName: { kind: "NAMED", value: "Laila Haddad" } }),
    changes: {
      kind: "DISCLOSED",
      overview: {
        event: { kind: "REGISTRATION_AMENDED", messageId: "timeline.event.registrationAmended" },
        highlights: [{ kind: "CHANGE", index: 0 }],
      },
      items: [
        {
          kind: "CASE_FIELD",
          change: {
            field: "claimantName",
            before: { kind: "PARTY", value: "Earlier stored name" },
            after: { kind: "PARTY", value: `Core change ${revision}` },
          },
        },
      ],
    },
  });
const editor = () =>
  render(
    <OperationEditor
      definition={definition}
      current={current(1)}
      initialAct="AMEND_REGISTRATION"
      onClose={vi.fn()}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
const author = async (user: ReturnType<typeof userEvent.setup>) => {
  selectFact("claimantName");
  const field = screen.getByLabelText("Claimant name", { exact: true });
  await user.clear(field);
  await user.type(field, "My retained draft");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  return field;
};
it("seeds the bounded conflict range, pages its issued cursor, and explicitly re-prepares the retained draft with a new identity", async () => {
  const conflict: Diagnostic_REVISION_CONFLICT = {
    code: "REVISION_CONFLICT",
    action: "REVIEW_LATEST",
    actualRevision: 53,
    changes: {
      kind: "PAGE",
      page: {
        disclosure: "FULL",
        entries: Array.from({ length: 10 }, (_, index) => fact(53 - index)),
        nextCursor: "opaque-observed-range",
      },
    },
  };
  const drafts: RequestByEndpoint["operation.preview"][] = [];
  const latest = current(54, "Latest stored name");
  const pages: unknown[] = [];
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    if (typeof path !== "string") {
      throw new Error("Expected the same-origin API path.");
    }
    if (path.endsWith("operation.preview")) {
      const input = inputFrom(init);
      drafts.push(input);
      if (drafts.length === 1) {
        return Promise.resolve(response("operation.preview", "REJECTED", conflict));
      }
      if (input.kind !== "CASE") {
        throw new Error("Expected the retained case draft.");
      }
      const task = taskForCommand(input);
      return Promise.resolve(
        endpointResponse("operation.preview", {
          representation: "HUMAN",
          outcome: {
            kind: "REVIEWED",
            review: {
              document: {
                screen: { regions: [] },
                context: {
                  kind: "REVIEW",
                  task: task.id,
                  inputs: task.inputs.map((taskInput) => taskInput.id),
                  target: { kind: "CASE", caseId: latest.context.caseId, beforeRevision: 54 },
                  consequence: task.consequence,
                  confirmation: { kind: "CASE_REFERENCE", reference: "CASE-1" },
                },
                basis: { kind: "SNAPSHOT", observation: basis },
                disclosure: "CURRENT",
              },
              presentation: {
                changes: [
                  {
                    key: "claimantName",
                    labelId: "field.claimantName.label",
                    before: { kind: "PARTY", value: "Latest stored name" },
                    after: { kind: "PARTY", value: "My retained draft" },
                  },
                ],
                unchanged: [],
              },
            },
          },
        }),
      );
    }
    if (path.endsWith("case.get")) {
      return Promise.resolve(response("case.get", "SUCCEEDED", latest));
    }
    if (path.endsWith("case.timeline")) {
      if (typeof init?.body !== "string") {
        throw new Error("Expected continuation bytes.");
      }
      pages.push(JSON.parse(init.body));
      return Promise.resolve(
        timelineResponse(
          "case.timeline",
          "SUCCEEDED",
          { ...timelinePage([fact(3), fact(2)]), basis },
          200,
          suppliedContinuation(),
        ),
      );
    }
    throw new Error(`Unexpected conflict request: ${path}`);
  });
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  editor();
  const field = await author(user);
  await screen.findByRole("button", { name: "Review against latest" });
  expect(field).toHaveValue("My retained draft");
  expect(document.querySelector(".case-detail")).toHaveTextContent("Latest stored name");
  expect(
    document.querySelectorAll(
      ".revision-conflict .region-timeline > li, .revision-conflict .table-view tbody > tr",
    ),
  ).toHaveLength(10);
  expect(drafts).toHaveLength(1);
  expect(pages).toHaveLength(0);
  await user.click(screen.getByRole("button", { name: "Load more" }));
  await waitFor(() => {
    expect(
      document.querySelectorAll(
        ".revision-conflict .region-timeline > li, .revision-conflict .table-view tbody > tr",
      ),
    ).toHaveLength(12);
  });
  expect(document.querySelector(".revision-conflict")).toHaveTextContent(
    "SUPPLIED CONTINUATION ONLY",
  );
  expect(document.querySelector(".revision-conflict")).not.toHaveTextContent(
    "Core change 3Core change 2",
  );
  expect(pages).toEqual([
    {
      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
      cursor: "opaque-observed-range",
      limit: 50,
    },
  ]);
  await user.click(screen.getByRole("button", { name: "Review against latest" }));
  await screen.findByRole("dialog");
  expect(drafts).toHaveLength(2);
  expect(drafts[1]).toMatchObject({
    kind: "CASE",
    expectedRevision: 54,
    act: { kind: "AMEND_REGISTRATION", values: { claimantName: "My retained draft" } },
  });
  expect(drafts[1]!.operationId).not.toBe(drafts[0]!.operationId);
  expect(
    fetch.mock.calls.some(
      ([path]) => typeof path === "string" && path.endsWith("operation.execute"),
    ),
  ).toBe(false);
});
it("keeps a withheld conflict opaque and refreshes before allowing an explicit new review", async () => {
  const conflict: Diagnostic_REVISION_CONFLICT = {
    code: "REVISION_CONFLICT",
    action: "REVIEW_LATEST",
    actualRevision: 2,
    changes: { kind: "WITHHELD" },
  };
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockImplementation((path) =>
      Promise.resolve(
        typeof path === "string" && path.endsWith("operation.preview")
          ? response("operation.preview", "REJECTED", conflict)
          : response("case.get", "SUCCEEDED", current(2, "Latest name", "NONE")),
      ),
    );
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  editor();
  const field = await author(user);
  await screen.findByRole("button", { name: "Review against latest" });
  expect(field).toHaveValue("My retained draft");
  expect(document.querySelector(".revision-conflict")).toHaveTextContent(
    "You do not have access to these changes.",
  );
  expect(
    document.querySelector(
      ".revision-conflict .region-timeline > li, .revision-conflict .table-view tbody > tr",
    ),
  ).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(
    fetch.mock.calls.some(([path]) => typeof path === "string" && path.endsWith("case.timeline")),
  ).toBe(false);
});
it("does not use an older-than-conflict read as latest or replay the refused operation", async () => {
  const conflict: Diagnostic_REVISION_CONFLICT = {
    code: "REVISION_CONFLICT",
    action: "REVIEW_LATEST",
    actualRevision: 3,
    changes: { kind: "WITHHELD" },
  };
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("operation.preview", "REJECTED", conflict))
    .mockResolvedValueOnce(response("case.get", "SUCCEEDED", current(2, "Stale name")))
    .mockResolvedValueOnce(response("case.get", "SUCCEEDED", current(4, "Fresh name")));
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  editor();
  const field = await author(user);
  await screen.findByRole("alert");
  expect(screen.queryByRole("button", { name: "Review against latest" })).toBeNull();
  expect(document.body).not.toHaveTextContent("Stale name");
  expect(document.querySelector(".case-detail")).toBeNull(); // An older current snapshot is hidden while conflict disclosure is refreshed.
  expect(field).toHaveValue("My retained draft");
  expect(fetch).toHaveBeenCalledTimes(2);
  await user.click(screen.getByRole("button", { name: "Reload current case" }));
  await screen.findByRole("button", { name: "Review against latest" });
  expect(document.querySelector(".case-detail")).toHaveTextContent("Fresh name");
  expect(fetch).toHaveBeenCalledTimes(3);
});

it.each(["NONE", "SUMMARY"] as const)(
  "hides previously disclosed changes when a continuation refusal is followed by %s history access",
  async (history) => {
    const conflict: Diagnostic_REVISION_CONFLICT = {
      code: "REVISION_CONFLICT",
      action: "REVIEW_LATEST",
      actualRevision: 53,
      changes: {
        kind: "PAGE",
        page: {
          disclosure: "FULL",
          entries: Array.from({ length: 10 }, (_, index) => fact(53 - index)),
          nextCursor: "private-range",
        },
      },
    };
    let reads = 0;
    let previews = 0;
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((path) => {
      if (typeof path !== "string") {
        throw new Error("Expected an API path.");
      }
      if (path.endsWith("operation.preview")) {
        previews += 1;
        return Promise.resolve(response("operation.preview", "REJECTED", conflict));
      }
      if (path.endsWith("case.get")) {
        reads += 1;
        return Promise.resolve(
          response(
            "case.get",
            "SUCCEEDED",
            current(
              reads === 1 ? 54 : 55,
              reads === 1 ? "Before access change" : "After access change",
              reads === 1 ? "FULL" : history,
            ),
          ),
        );
      }
      return Promise.resolve(
        response("case.timeline", "REJECTED", {
          code: "RESOURCE_UNAVAILABLE",
          action: "ASK_FOR_ACCESS",
        }),
      );
    });
    vi.stubGlobal("fetch", fetch);
    const user = userEvent.setup();
    editor();
    const field = await author(user);
    await screen.findByRole("button", { name: "Review against latest" });
    expect(document.body).toHaveTextContent("Core change 53");
    await user.click(screen.getByRole("button", { name: "Load more" }));
    await waitFor(() => {
      expect(document.querySelector(".case-detail")).toHaveTextContent("After access change");
    });
    expect(document.body).not.toHaveTextContent("Core change 53");
    expect(
      document.querySelector(
        ".revision-conflict .region-timeline > li, .revision-conflict .table-view tbody > tr",
      ),
    ).toBeNull();
    expect(field).toHaveValue("My retained draft");
    expect(previews).toBe(1);
  },
);
it("discards a late conflict page when explicit re-preparation replaces its owner", async () => {
  const conflict: Diagnostic_REVISION_CONFLICT = {
    code: "REVISION_CONFLICT",
    action: "REVIEW_LATEST",
    actualRevision: 53,
    changes: {
      kind: "PAGE",
      page: {
        disclosure: "FULL",
        entries: Array.from({ length: 10 }, (_, index) => fact(53 - index)),
        nextCursor: "late-range",
      },
    },
  };
  let release!: (value: Response) => void;
  const late = new Promise<Response>((resolve) => {
    release = resolve;
  });
  let previewCount = 0;
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((path) => {
    if (typeof path !== "string") {
      throw new Error("Expected an API path.");
    }
    if (path.endsWith("operation.preview")) {
      previewCount += 1;
      return Promise.resolve(
        previewCount === 1
          ? response("operation.preview", "REJECTED", conflict)
          : response("operation.preview", "REJECTED", {
              code: "INVALID_INPUT",
              action: "CORRECT_INPUT",
              issues: [{ field: "claimantName", code: "TEXT_REQUIRED" }],
            }),
      );
    }
    return path.endsWith("case.timeline")
      ? late
      : Promise.resolve(response("case.get", "SUCCEEDED", current(54, "Current stored name")));
  });
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  editor();
  await author(user);
  await screen.findByRole("button", { name: "Review against latest" });
  await user.click(screen.getByRole("button", { name: "Load more" }));
  const pageCall = fetch.mock.calls.find(
    ([path]) => typeof path === "string" && path.endsWith("case.timeline"),
  );
  expect(pageCall).toBeDefined();
  await user.click(screen.getByRole("button", { name: "Review against latest" }));
  await waitFor(() => {
    expect(previewCount).toBe(2);
  }); // An uncooperative transport may finish late; its obsolete owner still discards the payload.
  expect(pageCall?.[1]?.signal?.aborted).toBe(true);
  release(
    response("case.timeline", "SUCCEEDED", {
      ...timelinePage([
        timelineEntry({
          changes: {
            kind: "DISCLOSED",
            overview: {
              event: {
                kind: "HANDLING_SUMMARY_ADDED",
                messageId: "timeline.event.handlingSummaryAdded",
              },
              highlights: [{ kind: "CHANGE", index: 0 }],
            },
            items: [
              {
                kind: "HANDLING_SUMMARY",
                change: { before: null, after: "LATE_PAGE_PAYLOAD_CANARY" },
              },
            ],
          },
        }),
      ]),
      basis,
    }),
  );
  await late;
  await waitFor(() => {
    expect(document.querySelector(".revision-conflict")).toBeNull();
  });
  expect(document.body).not.toHaveTextContent("LATE_PAGE_PAYLOAD_CANARY");
});

it.each([
  ["SUMMARY", false],
  ["ERASED", false],
  ["SUMMARY", true],
  ["ERASED", true],
] as const)(
  "discards full conflict pages on successful %s continuation (empty=%s) before revalidation",
  async (level, empty) => {
    const initial = Array.from({ length: 10 }, (_, index) => fact(53 - index));
    const conflict: Diagnostic_REVISION_CONFLICT = {
      code: "REVISION_CONFLICT",
      action: "REVIEW_LATEST",
      actualRevision: 53,
      changes: {
        kind: "PAGE",
        page: {
          disclosure: "FULL",
          entries: initial,
          nextCursor: "observed-range",
        },
      },
    };
    let reads = 0;
    const refresh = Promise.withResolvers<Response>();
    const cursors: unknown[] = [];
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
      if (typeof path !== "string") {
        throw Error("API path required");
      }
      if (path.endsWith("operation.preview")) {
        return Promise.resolve(response("operation.preview", "REJECTED", conflict));
      }
      if (path.endsWith("case.get")) {
        reads += 1;
        return reads === 1
          ? Promise.resolve(response("case.get", "SUCCEEDED", current(54, "Current case")))
          : refresh.promise;
      }
      if (path.endsWith("case.timeline")) {
        cursors.push(JSON.parse(requestBody(init)));
        const items = empty
          ? []
          : [timelineEntry({ changes: { kind: level === "SUMMARY" ? "WITHHELD" : "ERASED" } })];
        return Promise.resolve(
          timelineResponse(
            "case.timeline",
            "SUCCEEDED",
            timelinePage(items, cursors.length === 1 ? "remaining-range" : null, level),
            200,
            {
              regions: [
                {
                  role: "RESULTS",
                  content: {
                    kind: "TABLE",
                    columns: [],
                    sections: [
                      {
                        titleId: "label.changes",
                        entries: [
                          {
                            key: "supplied-only",
                            labelId: "label.changes",
                            prominence: "PRIMARY",
                            value: {
                              kind: "TEXT",
                              value: `${level}_CONTINUATION_${cursors.length}`,
                            },
                          },
                        ],
                      },
                    ],
                    rows: items
                      .map(() => [])
                      .map((values, index) => ({
                        key: String(index),
                        values,
                      })),
                  },
                },
              ],
            },
          ),
        );
      }
      throw Error("Unexpected request");
    });
    vi.stubGlobal("fetch", fetch);
    const user = userEvent.setup();
    editor();
    const field = await author(user);
    await screen.findByRole("button", { name: "Review against latest" });
    expect(document.body).toHaveTextContent("Core change 53");
    await user.click(screen.getByRole("button", { name: "Load more" }));
    await screen.findByText(`${level}_CONTINUATION_1`);
    expect(document.body).not.toHaveTextContent("Core change 53");
    expect(field).toHaveValue("My retained draft");
    expect(reads).toBe(2);
    expect(document.querySelector(".case-detail")).toBeNull();
    const after =
      level === "SUMMARY"
        ? current(55, "After downgrade", "SUMMARY")
        : caseViewInput({
            caseReference: "CASE-1",
            revision: 55,
            state: { kind: "ERASED" },
            advice: { ...caseAdvice([]), history: "SUMMARY" },
          });
    refresh.resolve(response("case.get", "SUCCEEDED", after));
    await screen.findByRole("button", { name: "Review against latest" });
    await user.click(screen.getByRole("button", { name: "Load more" }));
    await screen.findByText(`${level}_CONTINUATION_2`);
    expect(cursors).toEqual([
      {
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        cursor: "observed-range",
        limit: 50,
      },
      {
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        cursor: "remaining-range",
        limit: 50,
      },
    ]);
    expect(document.body).not.toHaveTextContent("Core change 53");
    expect(field).toHaveValue("My retained draft");
    expect(
      fetch.mock.calls.some(
        ([path]) => typeof path === "string" && path.endsWith("operation.execute"),
      ),
    ).toBe(false);
  },
);

it("clears conflict views and its invalid cursor without replaying or revalidating", async () => {
  const initial = [fact(53)];
  let reads = 0;
  const conflict: Diagnostic_REVISION_CONFLICT = {
    code: "REVISION_CONFLICT",
    action: "REVIEW_LATEST",
    actualRevision: 53,
    changes: {
      kind: "PAGE",
      page: {
        disclosure: "FULL",
        entries: initial,
        nextCursor: "invalid-range",
      },
    },
  };
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation((path) => {
      if (typeof path !== "string") {
        throw Error("Expected API path");
      }
      if (path.endsWith("operation.preview")) {
        return Promise.resolve(response("operation.preview", "REJECTED", conflict));
      }
      if (path.endsWith("case.get")) {
        reads += 1;
        return Promise.resolve(response("case.get", "SUCCEEDED", current(54)));
      }
      if (path.endsWith("case.timeline")) {
        return Promise.resolve(
          response("case.timeline", "REJECTED", {
            code: "INVALID_CURSOR",
            action: "READ_CURRENT",
            target: "cursor",
          }),
        );
      }
      throw Error("Unexpected write");
    }),
  );
  const user = userEvent.setup();
  editor();
  const field = await author(user);
  await screen.findByRole("button", { name: "Review against latest" });
  await user.click(screen.getByRole("button", { name: "Load more" }));
  await screen.findByRole("alert");
  expect(document.body).not.toHaveTextContent("Core change 53");
  expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  expect(field).toHaveValue("My retained draft");
  expect(reads).toBe(1);
});
