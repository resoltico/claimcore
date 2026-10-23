import { stubDashboardContext } from "./dashboard-context.fixtures";
import { caseOffers } from "./case-advice.fixtures";
import { fireEvent, render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { Dashboard } from "../src/views/Dashboard";

import { previewForRequest } from "./preview-request.fixtures";
import { context, fields, response, runtime } from "./service-ui.fixtures";

const current: CurrentCase = {
  caseReference: fields.caseReference,
  revision: 1,
  advice: { acts: caseOffers(["CLOSE"]), history: ["SUMMARY", "FULL"] },

  state: { tag: "LIVE", fields, disposition: "ACTIVE" },
};

import { wireReply } from "./contract-corpus.fixtures";
const accepted = wireReply("operation.execute");
if (accepted.outcome.tag !== "ACCEPTED") {
  throw new Error("CaseReceipt fixture absent.");
}
const receipt = accepted.outcome.data;
const renderDashboard = () =>
  render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
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
        {
          state: "LIVE",
          disposition: "ACTIVE",
          caseReference: "CASE-1",
          revision: 1,
          status: "OPENED",
        },
      ],
      nextCursor: null,
    }),
  );
};

const queueDetail = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("case.get", "SUCCEEDED", current));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", { entries: [], nextCursor: null }),
  );
};

const queueAcceptedOpen = (): void => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("operation.execute", "ACCEPTED", {
      ...receipt,
      caseReference: fields.caseReference,
      snapshot: { tag: "DISCLOSED", state: { tag: "LIVE", disposition: "ACTIVE", fields } },
    }),
  );
};

it("returns from a selected case through Dashboard's detail-back transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "CASE-1" }, { timeout: 5_000 }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Back to cases" }));
  expect(await screen.findByRole("heading", { name: "Cases" })).toBeVisible();
});

it("opens a metadata-derived command from Dashboard's current-case transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "CASE-1" }));
  await user.click(await screen.findByRole("button", { name: /Close the case/u }));
  expect(screen.getByRole("heading", { name: "Close the case" })).toBeVisible();
  expect(screen.getByRole("button", { name: "Operations" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Sign out" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Back to case" }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
});

it("returns an accepted open operation to Dashboard through its committed transition", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueDefinitionAndList();
  fetch.mockImplementationOnce(
    previewForRequest(
      response("operation.preview", "REVIEWED", {
        review: {
          before: null,
          after: {
            caseReference: fields.caseReference,
            revision: 1,
            state: { tag: "LIVE", disposition: "ACTIVE", fields },
          },
          changes: [],
          context: runtime,
        },
      }),
    ),
  );
  queueAcceptedOpen();
  queueDetail();
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "Open a case" }));
  const fill = (label: RegExp, value: string): void => {
    fireEvent.change(screen.getByLabelText(label), { target: { value } });
  };
  fill(/Handler's case reference/u, "CASE-1");
  fill(/^Incident date/u, "2026-09-01");
  fill(/Incident notification date/u, "2026-09-02");
  fill(/Country of incident/u, "LV");
  fill(/Claimant name/u, "Synthetic claimant");
  fill(/Responsible insurer/u, "Synthetic insurer");
  fill(/Amount claimed/u, "12.34");
  fill(/Currency of claimed amount/u, "EUR");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  await user.click(await screen.findByRole("button", { name: "Return to case" }));
  expect(await screen.findByRole("heading", { name: "Case detail" })).toBeVisible();
  const currentRead = fetch.mock.calls.findLast(([path]) => path === "/api/case.get");
  expect(JSON.parse(typeof currentRead?.[1]?.body === "string" ? currentRead[1].body : "")).toEqual(
    {
      caseReference: fields.caseReference,
    },
  );
});
