import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { selectFact } from "../support/field-entry";
import { currentCaseInput, actorRefInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import { timelineEntry, timelinePage } from "../support/timeline.fixtures";
import { definition, fields, response } from "../support/service-ui.fixtures";
import { OperationEditor } from "../../src/views/OperationEditor";
import { isRequest } from "../../src/protocol/client";
import type { Diagnostic_REVISION_CONFLICT, RequestByEndpoint } from "@contracts/client";
const basis = {
  observedAt: "2026-11-04T12:00:00.000000Z",
  businessDate: "2026-11-04",
  timeZone: "Etc/UTC",
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
  return { ...value, work: { ...value.work, basis } };
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
    position: { sequence: revision, entryHash: "a".repeat(64) },
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
        entries: Array.from({ length: 50 }, (_, index) => fact(53 - index)),
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
      return Promise.resolve(
        response("operation.preview", "REVIEWED", {
          review: {
            view: { kind: "RECORD" as const, sections: [] },
            presentation: { changes: [], unchanged: [] },
            subject: {
              kind: "CASE",
              before: {
                caseId: latest.caseId,
                caseReference: latest.caseReference,
                revision: 54,
                state: latest.state,
              },
              after: {
                caseId: latest.caseId,
                caseReference: latest.caseReference,
                revision: 55,
                state: {
                  kind: "ACTIVE",
                  fields: {
                    ...latest.state.fields,
                    registration: {
                      ...latest.state.fields.registration,
                      claimantName: "My retained draft",
                    },
                  },
                },
              },

              context: {
                productVersion: "test",
                observedAt: basis.observedAt,
                businessDate: basis.businessDate,
                timeZone: basis.timeZone,
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
        response("case.timeline", "SUCCEEDED", { ...timelinePage([fact(3), fact(2)]), basis }),
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
  expect(document.querySelectorAll(".revision-conflict .timeline-entry")).toHaveLength(50);
  expect(drafts).toHaveLength(1);
  expect(pages).toHaveLength(0);
  await user.click(screen.getByRole("button", { name: "Load more" }));
  await waitFor(() => {
    expect(document.querySelectorAll(".revision-conflict .timeline-entry")).toHaveLength(52);
  });
  expect(pages).toEqual([
    {
      target: { kind: "REFERENCE", caseReference: "CASE-1" },
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
  expect(document.querySelector(".revision-conflict .timeline-entry")).toBeNull();
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
  expect(document.querySelector(".case-detail")).toHaveTextContent("Original name");
  expect(field).toHaveValue("My retained draft");
  expect(fetch).toHaveBeenCalledTimes(2);
  await user.click(screen.getByRole("button", { name: "Reload current case" }));
  await screen.findByRole("button", { name: "Review against latest" });
  expect(document.querySelector(".case-detail")).toHaveTextContent("Fresh name");
  expect(fetch).toHaveBeenCalledTimes(3);
});

it("hides previously disclosed changes when a continuation refusal is followed by loss of history access", async () => {
  const conflict: Diagnostic_REVISION_CONFLICT = {
    code: "REVISION_CONFLICT",
    action: "REVIEW_LATEST",
    actualRevision: 53,
    changes: {
      kind: "PAGE",
      page: {
        entries: Array.from({ length: 50 }, (_, index) => fact(53 - index)),
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
            reads === 1 ? "FULL" : "NONE",
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
  expect(document.querySelector(".revision-conflict .timeline-entry")).toBeNull();
  expect(field).toHaveValue("My retained draft");
  expect(previews).toBe(1);
});
it("discards a late conflict page when explicit re-preparation replaces its owner", async () => {
  const conflict: Diagnostic_REVISION_CONFLICT = {
    code: "REVISION_CONFLICT",
    action: "REVIEW_LATEST",
    actualRevision: 53,
    changes: {
      kind: "PAGE",
      page: {
        entries: Array.from({ length: 50 }, (_, index) => fact(53 - index)),
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
