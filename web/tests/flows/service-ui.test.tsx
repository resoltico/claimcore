import { caseSubject, caseDocumentInput } from "../support/case-document.fixtures";
import { operationAffordance } from "../support/affordances.fixtures";

import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { workBasis } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";

import { getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPresenter } from "../../src/presentation/presenter";
import { defaults } from "../../src/presentation/preferences";
import { App } from "../../src/App";
import type { CurrentCase } from "../../src/api/service";
import { ScreenView } from "../../src/ui/ScreenView";
import { CatalogField } from "../../src/components/CatalogField";
import { OperationEditor } from "../../src/views/OperationEditor";
import { wireReply } from "../support/reply.fixtures";
import { previewReply, draftAt } from "../support/presentation-state.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";
import { humanReadHttpReply } from "../support/human-read-reply.fixtures";
import { definition } from "../support/service-ui.fixtures";
const current: CurrentCase = caseDocumentInput({
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
});
describe("catalog rendering", () => {
  it("renders catalog labels and all supplied field values without client-side normalization", () => {
    const changed = vi.fn();
    const incident = definition.catalog.fields.find((field) => field.name === "incidentDate");
    if (incident === undefined) {
      throw new Error("Incident catalog entry is required.");
    }
    render(
      <>
        <CatalogField field={incident} value="2026-09-01" onChange={changed} />
        <ScreenView
          view={{
            regions: (
              [
                {
                  titleId: "subject.CASE.label",
                  entries: [
                    {
                      key: "arbitrary-server-key",
                      labelId: "field.caseReference.label",
                      prominence: "PRIMARY",
                      value: { kind: "REFERENCE", value: "CASE-1" },
                    },
                  ],
                },
              ] as const
            ).map((section) => ({
              role: "FACTS" as const,
              titleId: section.titleId,
              content: { kind: "ENTRIES" as const, entries: section.entries },
            })),
          }}
        />
      </>,
    );
    const input = screen.getByRole("group", { name: "Incident date" });
    expect(document.querySelector('input[name="incidentDate"]')).toHaveValue("2026-09-01");
    const descriptionId = input.getAttribute("aria-describedby");
    expect(descriptionId).not.toBeNull();
    expect(document.getElementById(descriptionId?.split(" ").at(-1) ?? "")).toHaveTextContent(
      createPresenter(defaults).fieldHint(incident.name),
    );
    expect(screen.getAllByText(createPresenter(defaults).fieldHint(incident.name))).toHaveLength(1);
    expect(screen.getByText("CASE-1")).toBeVisible();
  });
});
const queueClosePreview = () => {
  vi.mocked(globalThis.fetch).mockImplementationOnce(() => Promise.resolve(previewReply()));
};
const sessionReply = (authenticated: boolean, antiforgeryToken: string) => {
  const reply = wireReply("session");
  if (reply.outcome.kind !== "SUCCEEDED") {
    throw new Error("Session fixture required");
  }
  return endpointResponse("session", {
    outcome: { ...reply.outcome, data: { ...reply.outcome.data, authenticated, antiforgeryToken } },
  });
};
const acceptsOperation = async (): Promise<void> => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueClosePreview();
  fetch.mockImplementationOnce(() => {
    const reply = wireReply("operation.execute");
    if (reply.outcome.kind !== "ACCEPTED") {
      throw new Error("Receipt fixture required");
    }
    return Promise.resolve(
      endpointResponse("operation.execute", {
        ...reply,
        outcome: {
          kind: "ACCEPTED",
          document: {
            ...reply.outcome.document,
            screen: {
              regions: [
                {
                  role: "NOTICE",
                  content: { kind: "NOTICE", tone: "INFO", messageId: "task.case.close.done" },
                },
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
                        value: { kind: "IDENTIFIER", value: draftAt(1).operationId },
                      },
                    ],
                  },
                },
              ],
            },
          },
        },
      }),
    );
  });
  const committed = vi.fn();
  render(
    <OperationEditor
      definition={definition}
      current={current}
      initialAct="case.close"
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />,
  );
  await user.click(getButton("Review changes"));
  expect(await screen.findByRole("dialog", { name: "Close the case" })).toBeVisible();
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  expect(
    await screen.findByRole("heading", {
      name: "Recorded change",
    }),
  ).toBeVisible();
  expect(screen.getByText("Case closed.")).toBeVisible();
  expect(document.querySelector('.receipt [data-region="DETAILS"]')).not.toHaveAttribute("open");
  expect(committed).toHaveBeenCalledOnce();
};
describe("metadata-driven operation acceptance", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it(
    "prepares, reviews, and accepts an operation with React Aria modal semantics",
    acceptsOperation,
  );
});
const sessionLifecycle = async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(sessionReply(false, "anonymous"));
  const anonymous = render(<App />);
  const signIn = await screen.findByRole("link", {
    name: "Sign in",
  });
  expect(signIn).toHaveAttribute("href", "/auth/login");
  expect(fetch).toHaveBeenCalledTimes(1);
  anonymous.unmount();
  fetch.mockResolvedValueOnce(sessionReply(true, "authenticated"));
  fetch.mockResolvedValueOnce(
    humanReadHttpReply("case.list", {
      screen: { regions: [] },
      subject: { kind: "LIST" },
      actions: [
        operationAffordance(
          "case.open",
          { kind: "CASE", target: { kind: "REFERENCE", caseReference: "" }, expectedRevision: 0 },
          {},
          undefined,
          {},
        ),
      ],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    }),
  );
  fetch.mockResolvedValueOnce(endpointResponse("session.logout", wireReply("session.logout")));
  render(<App />);
  expect(
    await screen.findByRole("heading", {
      name: "Cases",
    }),
  ).toBeVisible();
  await user.click(document.querySelector(".account-menu > summary")!);
  await user.click(getButton("Sign out"));
  expect(
    await screen.findByRole("heading", {
      name: "Sign in to the case register",
    }),
  ).toBeVisible();
};
describe("service session lifecycle", () => {
  beforeEach(() => {
    stubDashboardContext();
  });
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it("offers OIDC sign-in, then hydrates an authenticated return and signs out", sessionLifecycle);
});

beforeEach(() => {
  stubWorklistSummary();
});
