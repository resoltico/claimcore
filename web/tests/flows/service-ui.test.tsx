import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { currentCaseInput } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { caseAdvice } from "../support/case-advice.fixtures";
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
import { wireResponse } from "../support/reply.fixtures";
import { previewForRequest } from "../support/preview-request.fixtures";
import { context, definition, fields, runtime, response } from "../support/service-ui.fixtures";
const current: CurrentCase = currentCaseInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: fields.caseReference,
  revision: 1,
  advice: {
    ...caseAdvice(["CLOSE"]),
    history: "FULL",
  },
  state: {
    kind: "ACTIVE",
    fields,
  },
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
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockImplementationOnce(
    previewForRequest(
      response("operation.preview", "REVIEWED", {
        review: {
          view: { regions: [] },
          presentation: { changes: [], unchanged: [] },
          subject: {
            kind: "CASE",
            before: {
              caseId: "20000000-0000-4000-8000-000000000001",
              caseReference: fields.caseReference,
              revision: 1,
              state: {
                kind: "ACTIVE",
                fields,
              },
            },
            after: {
              caseId: "20000000-0000-4000-8000-000000000001",
              caseReference: fields.caseReference,
              revision: 2,
              state: {
                kind: "ACTIVE",
                fields,
              },
            },

            context: runtime,
          },
        },
      }),
    ),
  );
};
const acceptsOperation = async (): Promise<void> => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueClosePreview();
  fetch.mockResolvedValueOnce(wireResponse("operation.execute"));
  const committed = vi.fn();
  render(
    <OperationEditor
      definition={definition}
      current={current}
      initialAct="CLOSE"
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
  expect(screen.getByText("Case opened.")).toBeVisible();
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
  fetch.mockResolvedValueOnce(
    response("session", "SUCCEEDED", {
      authenticated: false,
      antiforgeryToken: "anonymous",
    }),
  );
  const anonymous = render(<App />);
  const signIn = await screen.findByRole("link", {
    name: "Sign in",
  });
  expect(signIn).toHaveAttribute("href", "/auth/login");
  expect(fetch).toHaveBeenCalledTimes(1);
  anonymous.unmount();
  fetch.mockResolvedValueOnce(
    response("session", "SUCCEEDED", {
      authenticated: true,
      antiforgeryToken: "authenticated",
    }),
  );
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("session.logout", "SUCCEEDED", {
      authenticated: false,
      antiforgeryToken: "replacement",
    }),
  );
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
