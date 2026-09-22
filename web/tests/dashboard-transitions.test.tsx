import { fireEvent, render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/v2";
import { Dashboard } from "../src/views/Dashboard";
import { generatedResponse } from "./contract-corpus.fixtures";
import { definition, fields, preparation, response } from "./v2-ui.fixtures";

const current: CurrentCase = { case: { fields, revision: "1" }, availableCommands: ["CLOSE"] };

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

const queueDefinitionAndList = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", {
      items: [{ caseReference: "CASE-1", revision: "1", status: "OPENED" }],
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

const renderDashboard = () =>
  render(<Dashboard token="token" sessionEpoch={1} onLogout={vi.fn(() => Promise.resolve())} />);

it("returns from a selected case through Dashboard's detail-back transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "CASE-1" }));
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
});

it("returns an accepted open operation to Dashboard through its committed transition", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueDefinitionAndList();
  fetch.mockResolvedValueOnce(
    response("command.prepare", "PREPARED", {
      details: preparation,
      review: {
        before: null,
        proposed: { fields, revision: "1" },
        changes: [],
        context: definition.runtime,
        advisory: true,
      },
    }),
  );
  fetch.mockResolvedValueOnce(generatedResponse("command.execute"));
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  renderDashboard();
  await user.click(await screen.findByRole("button", { name: "Open new case" }));
  const fill = (label: RegExp, value: string): void => {
    fireEvent.change(screen.getByLabelText(label), { target: { value } });
  };
  fill(/Handler's case reference/u, "COMMIT-001");
  fill(/^Incident date/u, "2026-09-01");
  fill(/Incident notification date/u, "2026-09-02");
  fill(/Country of incident/u, "Latvia");
  fill(/Claimant name/u, "Synthetic claimant");
  fill(/Allegedly responsible insurer/u, "Synthetic insurer");
  fill(/Amount claimed/u, "12.34");
  fill(/Currency of claimed amount/u, "EUR");
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await user.click(
    await screen.findByRole("checkbox", { name: "I will submit this exact prepared request." }),
  );
  await user.click(screen.getByRole("button", { name: "Submit exact request" }));
  await user.click(await screen.findByRole("button", { name: "Return to case" }));
  expect(await screen.findByRole("heading", { name: "Cases" })).toBeVisible();
});
