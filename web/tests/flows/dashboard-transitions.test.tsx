import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { changeField } from "../support/field-entry";
import { caseViewInput, caseSummaryInput } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { caseAdvice } from "../support/case-advice.fixtures";
import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { Dashboard } from "../../src/views/Dashboard";
import { previewForRequest } from "../support/preview-request.fixtures";
import { context, fields, response, runtime } from "../support/service-ui.fixtures";
const current: CurrentCase = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
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
});
import { wireReply } from "../support/contract-corpus.fixtures";
const accepted = wireReply("operation.execute");
if (accepted.outcome.kind !== "ACCEPTED") {
  throw new Error("CaseReceipt fixture absent.");
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
          state: { kind: "ACTIVE", status: "OPENED" },
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
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", {
      entries: [],
      nextCursor: null,
    }),
  );
};
const queueAcceptedOpen = (): void => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("operation.execute", "ACCEPTED", {
      ...receipt,
      subject: {
        ...receipt.subject,
        kind: "CASE",
        caseReference: fields.caseReference,
        snapshot: {
          kind: "DISCLOSED",
          view: {
            kind: "ACTIVE",
            fields,
          },
        },
      },
    }),
  );
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
    screen.getByRole("heading", {
      name: "Close the case",
    }),
  ).toBeVisible();
  expect(screen.getByRole("link", { name: "Operations" })).not.toHaveAttribute(
    "aria-disabled",
    "true",
  );
  expect(getButton("Sign out")).not.toBeDisabled();
  await user.click(getButton("Back to case"));
  expect(
    await screen.findByRole("heading", {
      name: "Case detail",
    }),
  ).toBeVisible();
});
const queueOpenPreview = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockImplementationOnce(
    previewForRequest(
      response("operation.preview", "REVIEWED", {
        review: {
          subject: {
            kind: "CASE",
            before: null,
            after: {
              caseId: "20000000-0000-4000-8000-000000000001",
              caseReference: fields.caseReference,
              revision: 1,
              state: {
                kind: "ACTIVE",
                fields,
              },
            },
            changes: [],
            context: runtime,
          },
        },
      }),
    ),
  );
};
it("returns an accepted open operation to Dashboard through its committed transition", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueDefinitionAndList();
  queueOpenPreview();
  queueAcceptedOpen();
  queueDetail();
  renderDashboard();
  await user.click(await findButton("Open a case"));
  const fill = (label: string, value: string): void => {
    changeField(label, value);
  };
  fill("Handler's case reference", "CASE-1");
  fill("Incident date", "2026-09-01");
  fill("Incident notification date (FNOL)", "2026-09-02");
  fill("Country of incident (ISO alpha-2 code)", "LV");
  fill("Claimant name", "Synthetic claimant");
  fill("Responsible insurer", "Synthetic insurer");
  fill("Amount claimed", "12.34");
  fill("Currency of claimed amount", "EUR");
  await user.click(getButton("Review changes"));
  await user.click(
    await screen.findByRole("checkbox", {
      name: "I confirm these changes.",
    }),
  );
  await user.click(getButton("Record changes"));

  expect(
    await screen.findByRole("heading", {
      name: "Case detail",
    }),
  ).toBeVisible();
  const currentRead = fetch.mock.calls.findLast(([path]) => path === "/api/case.get");
  expect(JSON.parse(typeof currentRead?.[1]?.body === "string" ? currentRead[1].body : "")).toEqual(
    {
      target: { kind: "REFERENCE", caseReference: fields.caseReference },
    },
  );
});

beforeEach(() => {
  stubWorklistSummary();
});
