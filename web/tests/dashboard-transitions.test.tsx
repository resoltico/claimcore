import { fireEvent, render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { Dashboard } from "../src/views/Dashboard";

import { previewForRequest } from "./preview-request.fixtures";
import { fields, digest, response, runtime } from "./service-ui.fixtures";

const current: CurrentCase = {
  state: "LIVE",
  disposition: "ACTIVE",
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

import { generatedWebValue } from "./contract-corpus.fixtures";
const accepted = generatedWebValue("command.execute");
if (accepted.outcome.tag !== "ACCEPTED") {
  throw new Error("Receipt fixture absent.");
}
const receipt = accepted.outcome.data;
const renderDashboard = () =>
  render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

const queueDefinitionAndList = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [
        {
          state: "LIVE",
          disposition: "ACTIVE",
          caseReference: "CASE-1",
          revision: "1",
          status: "OPENED",
        },
      ],
      nextCursor: null,
    }),
  );
};

const queueDetail = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("case.get", "SUCCEEDED", { tag: "FOUND", current }));
  fetch.mockResolvedValueOnce(
    response("case.history", "SUCCEEDED", { tag: "FOUND", entries: [], nextCursor: null }),
  );
};

const queueAcceptedOpen = (): void => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("command.execute", "ACCEPTED", {
      ...receipt,
      caseReference: fields.caseReference,
      snapshot: { fields, revision: "1" },
    }),
  );
};

it("returns from a selected case through Dashboard's detail-back transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  // Cold on-demand schema loading is not a one-second UI latency contract.
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
      response("command.preview", "REVIEWED", {
        requestSha256: digest,
        review: {
          before: null,
          proposed: { fields, revision: "1" },
          changes: [],
          context: runtime,
          advisory: true,
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
  fill(/Country of incident/u, "Latvia");
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
