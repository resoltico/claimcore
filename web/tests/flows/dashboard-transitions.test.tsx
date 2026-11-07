import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { timelinePage } from "../support/timeline.fixtures";
import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { changeField } from "../support/field-entry";
import { currentCaseInput, caseSummaryInput } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { caseAdvice } from "../support/case-advice.fixtures";
import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { Dashboard } from "../../src/views/Dashboard";
import * as operationIds from "../../src/operation/operationId";
import { context, fields, response, runtime } from "../support/service-ui.fixtures";
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
import { wireReply } from "../support/reply.fixtures";
const accepted = wireReply("operation.execute");
if (accepted.outcome.kind !== "ACCEPTED") {
  throw new Error("CaseOperationReceipt fixture absent.");
}
const receipt = accepted.outcome.data;
const renderDashboard = () => render(<Dashboard />);
beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn());
  stubDashboardContext();
});
const queueDefinitionAndList = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
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
};
const queueDetail = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("case.get", "SUCCEEDED", current));
  fetch.mockResolvedValueOnce(response("case.timeline", "SUCCEEDED", timelinePage()));
};
it("returns from a selected case through Dashboard's detail-back transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(
    await screen.findByRole(
      "link",
      {
        name: "CASE-1",
      },
      {
        timeout: 5_000,
      },
    ),
  );
  expect(
    await screen.findByRole("heading", {
      name: "Case detail",
    }),
  ).toBeVisible();
  await user.click(getButton("Back to cases"));
  expect(
    await screen.findByRole("heading", {
      name: "Cases",
    }),
  ).toBeVisible();
});
it("opens a metadata-derived act from Dashboard's current-case transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(await screen.findByRole("link", { name: "CASE-1" }));
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(response("case.get", "SUCCEEDED", current));
  await user.click(await findButton(/Close the case/u));
  expect(
    await screen.findByRole("heading", {
      name: "Close the case",
    }),
  ).toBeVisible();
  expect(document.querySelector('.workspace-sidebar a[href="/people"]')).not.toHaveAttribute(
    "aria-disabled",
    "true",
  );
  expect(screen.queryByRole("button", { name: "Sign out" })).toBeNull();
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  await user.click(document.querySelector(".account-menu > summary")!);
  expect(getButton("Sign out")).not.toBeDisabled();
  expect(
    await screen.findByRole("heading", {
      name: "Case detail",
    }),
  ).toBeVisible();
});
it("returns an accepted open operation to Dashboard through its committed transition", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const creation = operationIds.operationId();
  vi.spyOn(operationIds, "operationId").mockReturnValue(creation);
  fetch.mockImplementation((path) => {
    if (typeof path !== "string") {
      throw new Error("Expected an API path");
    }
    switch (path) {
      case "/api/case.list":
        return Promise.resolve(
          response("case.list", "SUCCEEDED", {
            items: [],
            offers: context.offers,
            nextCursor: null,
          }),
        );
      case "/api/operation.preview":
        return Promise.resolve(
          response("operation.preview", "REVIEWED", {
            review: {
              view: { kind: "RECORD" as const, sections: [] },
              presentation: { changes: [], unchanged: [] },
              subject: {
                kind: "CASE",
                before: null,
                after: {
                  caseId: creation,
                  caseReference: "CASE-1",
                  revision: 1,
                  state: { kind: "ACTIVE", fields },
                },
                context: runtime,
              },
            },
          }),
        );
      case "/api/operation.execute":
        return Promise.resolve(
          response("operation.execute", "ACCEPTED", {
            ...receipt,
            operationId: creation,
            subject: {
              kind: "CASE",
              caseId: creation,
              caseReference: "CASE-1",
              revision: 1,
              act: "OPEN",
              snapshot: { kind: "DISCLOSED", view: { kind: "ACTIVE", fields } },
            },
          }),
        );
      case "/api/case.get":
        return Promise.resolve(response("case.get", "SUCCEEDED", { ...current, caseId: creation }));
      case "/api/case.timeline":
        return Promise.resolve(
          response("case.timeline", "SUCCEEDED", { ...timelinePage(), caseId: creation }),
        );
      default:
        throw new Error(`Unexpected transition request: ${path}`);
    }
  });
  renderDashboard();
  await user.click(await screen.findByRole("link", { name: "Open a case" }));
  await screen.findByLabelText("Case reference");
  const fill = (label: string, value: string): void => {
    changeField(label, value);
  };
  fill("Case reference", "CASE-1");
  fill("Incident date", "2026-09-01");
  fill("Notified date", "2026-09-02");
  fill("Country", "LV");
  fill("Claimant name", "Synthetic claimant");
  fill("Responsible insurer", "Synthetic insurer");
  fill("Amount claimed", "12.34");
  fill("Currency of claimed amount", "EUR");
  await user.click(getButton("Review changes"));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());

  expect(
    await screen.findByRole("heading", {
      name: "Case detail",
    }),
  ).toBeVisible();
  const currentRead = fetch.mock.calls.findLast(([path]) => path === "/api/case.get");
  expect(JSON.parse(typeof currentRead?.[1]?.body === "string" ? currentRead[1].body : "")).toEqual(
    {
      target: { kind: "ID", caseId: creation },
    },
  );
});

beforeEach(() => {
  stubWorklistSummary();
});
