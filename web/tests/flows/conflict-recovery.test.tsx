import {
  caseDocumentInput,
  registrationInputs,
  emptyCaseSeeds,
} from "../support/case-document.fixtures";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { selectFact } from "../support/field-entry";
import { timelineDocument } from "../support/timeline-document.fixtures";
import { humanReadHttpReply } from "../support/human-read-reply.fixtures";
import { definition, fields } from "../support/service-ui.fixtures";
import { OperationEditor } from "../../src/views/OperationEditor";
import { isRequest, isResponseFor } from "../../src/protocol/client";
import type {
  HumanRejection_CONFLICT,
  HumanDocument,
  RegionTimelineEntry,
  RequestByEndpoint,
  Screen,
} from "@contracts/client";
import { endpointResponse } from "../support/transport-response.fixtures";
import { taskForCommand } from "../../src/protocol/taskCatalog";
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
  nextCursor: string | null,
  disclosure: "FULL" | "SUMMARY" | "ERASED",
  readScreen: Screen,
): Response =>
  humanReadHttpReply("case.timeline", {
    screen: readScreen,
    context: { kind: "PAGE", tasks: [], nextCursor },
    basis: { kind: "SNAPSHOT", observation: basis },
    disclosure,
  });
const response = (
  endpoint: "operation.preview" | "case.get" | "case.timeline",
  kind: "REJECTED" | "SUCCEEDED",
  authored: unknown,
): Response => {
  const reply: unknown = {
    representation: "HUMAN",
    ...(endpoint === "operation.preview"
      ? { operationId: "10000000-0000-7000-8000-000000000001" }
      : {}),
    outcome: kind === "SUCCEEDED" ? { kind, document: authored } : { kind, rejection: authored },
  };
  if (!isResponseFor(endpoint, "HUMAN", reply)) {
    throw new Error("Authored HUMAN conflict interaction response is invalid");
  }
  return endpointResponse(endpoint, reply);
};
const conflictDocument = (
  entries: ReadonlyArray<RegionTimelineEntry>,
  nextCursor: string | null,
): HumanDocument => ({
  screen: { regions: [{ role: "ACTIVITY", content: { kind: "TIMELINE", entries } }] },
  context: { kind: "CONTINUATION", nextCursor },
  basis: { kind: "SNAPSHOT", observation: basis },
  disclosure: "FULL",
});
const withheldConflictDocument: HumanDocument = {
  screen: {
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
  context: { kind: "CONTINUATION", nextCursor: null },
  basis: { kind: "SNAPSHOT", observation: basis },
  disclosure: "WITHHELD",
};
const current = (
  revision: number,
  name = "Original name",
  history: "NONE" | "SUMMARY" | "FULL" = "FULL",
) => {
  const value = caseDocumentInput(
    {
      tasks: ["CASE:AMEND_REGISTRATION"],
      revision,
      history,
      editableInputs: registrationInputs,
      seeds: { ...emptyCaseSeeds, registration: { ...fields.registration, claimantName: name } },
    },
    {
      regions: [
        {
          role: "IDENTITY",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "caseReference",
                labelId: "field.caseReference.label",
                prominence: "PRIMARY",
                value: { kind: "REFERENCE", value: "CASE-1" },
              },
              {
                key: "claimantName",
                labelId: "field.claimantName.label",
                prominence: "PRIMARY",
                value: { kind: "PARTY", value: name },
              },
            ],
          },
        },
      ],
    },
  );
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
const fact = (revision: number): RegionTimelineEntry => ({
  key: `${revision}/0`,
  when: "2026-11-02T12:00:00.000000Z",
  who: {
    actorId: "10000000-0000-4000-8000-000000000001",
    displayName: { kind: "NAMED", value: "Laila Haddad" },
    principalKind: "HUMAN",
    availability: "AVAILABLE",
  },
  summary: { messageId: "timeline.event.registrationAmended", arguments: [] },
  highlights: [
    {
      titleId: "field.claimantName.label",
      entries: [
        {
          key: "before",
          labelId: "label.before",
          prominence: "PRIMARY",
          value: { kind: "PARTY", value: "Earlier stored name" },
        },
        {
          key: "after",
          labelId: "label.after",
          prominence: "PRIMARY",
          value: { kind: "PARTY", value: `Core change ${revision}` },
        },
      ],
    },
  ],
  details: [],
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
  const conflict: HumanRejection_CONFLICT = {
    kind: "CONFLICT",
    advice: {
      message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
      action: "REVIEW_LATEST",
    },
    actualRevision: 53,
    document: conflictDocument(
      Array.from({ length: 10 }, (_, index) => fact(53 - index)),
      "opaque-observed-range",
    ),
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
      return Promise.resolve(timelineResponse(null, "FULL", suppliedContinuation()));
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
  const conflict: HumanRejection_CONFLICT = {
    kind: "CONFLICT",
    advice: {
      message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
      action: "REVIEW_LATEST",
    },
    actualRevision: 2,
    document: withheldConflictDocument,
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
  const conflict: HumanRejection_CONFLICT = {
    kind: "CONFLICT",
    advice: {
      message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
      action: "REVIEW_LATEST",
    },
    actualRevision: 3,
    document: withheldConflictDocument,
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
    const conflict: HumanRejection_CONFLICT = {
      kind: "CONFLICT",
      advice: {
        message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
        action: "REVIEW_LATEST",
      },
      actualRevision: 53,
      document: conflictDocument(
        Array.from({ length: 10 }, (_, index) => fact(53 - index)),
        "private-range",
      ),
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
          kind: "REFUSED",
          advice: {
            message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
            action: "ASK_FOR_ACCESS",
          },
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
  const conflict: HumanRejection_CONFLICT = {
    kind: "CONFLICT",
    advice: {
      message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
      action: "REVIEW_LATEST",
    },
    actualRevision: 53,
    document: conflictDocument(
      Array.from({ length: 10 }, (_, index) => fact(53 - index)),
      "late-range",
    ),
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
              kind: "INPUT",
              advice: {
                message: {
                  messageId: "diagnostic.INVALID_INPUT",
                  arguments: [{ key: "issueCount", value: { kind: "COUNT", value: 1 } }],
                },
                action: "CORRECT_INPUT",
              },
              issues: [
                {
                  input: "field:claimantName",
                  row: null,
                  related: null,
                  message: { messageId: "diagnostic.TEXT_REQUIRED", arguments: [] },
                },
              ],
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
    response(
      "case.timeline",
      "SUCCEEDED",
      timelineDocument([
        {
          ...fact(1),
          highlights: [
            {
              titleId: "label.changes",
              entries: [
                {
                  key: "late",
                  labelId: "label.changes",
                  prominence: "PRIMARY",
                  value: { kind: "TEXT", value: "LATE_PAGE_PAYLOAD_CANARY" },
                },
              ],
            },
          ],
        },
      ]),
    ),
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
    const conflict: HumanRejection_CONFLICT = {
      kind: "CONFLICT",
      advice: {
        message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
        action: "REVIEW_LATEST",
      },
      actualRevision: 53,
      document: conflictDocument(initial, "observed-range"),
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
        const rows = empty ? [] : [{ key: "supplied", values: [] }];
        return Promise.resolve(
          timelineResponse(cursors.length === 1 ? "remaining-range" : null, level, {
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
                  rows,
                },
              },
            ],
          }),
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
        : {
            ...caseDocumentInput(
              { revision: 55, history: "SUMMARY" },
              {
                regions: [
                  {
                    role: "IDENTITY",
                    content: {
                      kind: "ENTRIES",
                      entries: [
                        {
                          key: "caseReference",
                          labelId: "field.caseReference.label",
                          prominence: "PRIMARY",
                          value: { kind: "REFERENCE", value: "CASE-1" },
                        },
                      ],
                    },
                  },
                ],
              },
            ),
            disclosure: "ERASED" as const,
          };
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
  const conflict: HumanRejection_CONFLICT = {
    kind: "CONFLICT",
    advice: {
      message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
      action: "REVIEW_LATEST",
    },
    actualRevision: 53,
    document: conflictDocument(initial, "invalid-range"),
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
            kind: "REFUSED",
            advice: {
              message: { messageId: "diagnostic.INVALID_CURSOR", arguments: [] },
              action: "READ_CURRENT",
            },
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
