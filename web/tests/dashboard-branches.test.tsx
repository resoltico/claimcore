import { fillRegistrationInputs } from "./registration-inputs.fixtures";
import { render, screen, waitFor } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { Dashboard } from "../src/views/Dashboard";
import { context, response } from "./service-ui.fixtures";

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
    expect(globalThis.fetch).toHaveBeenCalledWith("/api/case.list", expect.anything());
  });
  expect(screen.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
});

it("locks navigation and logout while a reviewed mutation is dispatched", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", { offers: context.offers, items: [], nextCursor: null }),
  );
  fetch.mockReturnValueOnce(
    new Promise<Response>(() => {
      /* never settles */
    }),
  );
  render(<Dashboard token="token" onLogout={vi.fn(() => Promise.resolve())} />);
  await user.click(await screen.findByRole("button", { name: "Open a case" }));
  fillRegistrationInputs({ caseReference: "LOCK-001", incidentDate: "2026-09-09" });
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(screen.getByRole("button", { name: "Operations" })).toBeDisabled();
  });
  expect(screen.getByRole("button", { name: "Sign out" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Back to cases" })).toBeDisabled();
});
