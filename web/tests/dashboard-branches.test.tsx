import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { Dashboard } from "../src/views/Dashboard";
import { definition, response } from "./v2-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("renders a neutral shell while definition admission is pending", () => {
  vi.mocked(globalThis.fetch).mockReturnValueOnce(new Promise<Response>(() => undefined));
  render(<Dashboard token="token" sessionEpoch={1} onLogout={vi.fn(() => Promise.resolve())} />);
  expect(screen.getByText("Loading core definition…")).toBeVisible();
  expect(screen.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
});

it("locks navigation and logout while a prepared mutation is dispatched", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  fetch.mockReturnValueOnce(new Promise<Response>(() => undefined));
  render(<Dashboard token="token" sessionEpoch={2} onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Open new case" }));
  const inputs = screen.getAllByRole("textbox");
  await user.type(inputs[0]!, "LOCK-001");
  const incidentDate = document.querySelector<HTMLInputElement>('input[type="date"]');
  if (incidentDate === null) throw new Error("Open-case incident date input was not rendered.");
  await user.type(incidentDate, "2026-09-09");
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Recovery" })).toBeDisabled());
  expect(screen.getByRole("button", { name: "Sign out" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Back without preparing" })).toBeDisabled();
});
