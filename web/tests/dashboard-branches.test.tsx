import { render, screen, waitFor } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { Dashboard } from "../src/views/Dashboard";
import { response } from "./v3-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("renders public product metadata while an authorized case read is pending", async () => {
  vi.mocked(globalThis.fetch).mockReturnValueOnce(
    new Promise<Response>(() => {
      /* never settles */
    }),
  );
  render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
  expect(screen.queryByText("Loading application…")).toBeNull();
  await waitFor(() => {
    expect(globalThis.fetch).toHaveBeenCalledWith("/api/v3/cases/list", expect.anything());
  });
  expect(screen.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
});

it("locks navigation and logout while a prepared mutation is dispatched", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("case.list", "SUCCEEDED", { items: [], nextCursor: null }));
  fetch.mockReturnValueOnce(
    new Promise<Response>(() => {
      /* never settles */
    }),
  );
  render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Open a case" }));
  const inputs = screen.getAllByRole("textbox");
  await user.type(inputs[0]!, "LOCK-001");
  const incidentDate = document.querySelector<HTMLInputElement>('input[name="incidentDate"]');
  if (incidentDate === null) {
    throw new Error("Open-case incident date input was not rendered.");
  }
  await user.type(incidentDate, "2026-09-09");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(screen.getByRole("button", { name: "Recovery" })).toBeDisabled();
  });
  expect(screen.getByRole("button", { name: "Sign out" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Back to cases" })).toBeDisabled();
});
