import { localNotice } from "../src/api/notices";
import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { App } from "../src/App";

const useSession = vi.hoisted(() => vi.fn());

vi.mock("../src/hooks/useSession", () => ({ useSession }));

const actions = () => ({ logout: vi.fn(), refresh: vi.fn() });

it("renders the loading shell before local session discovery completes", () => {
  useSession.mockReturnValue({ state: { kind: "loading", epoch: 0 }, ...actions() });
  render(<App />);
  expect(screen.getByText("Loading local session…")).toBeVisible();
});

it("renders a recoverable session failure and invokes its refresh action", async () => {
  const user = userEvent.setup();
  const state = actions();
  useSession.mockReturnValue({
    state: { kind: "failure", message: localNotice("unreachable"), epoch: 1 },
    ...state,
  });
  render(<App />);
  expect(screen.getByRole("alert")).toHaveTextContent("The local service could not be reached.");
  await user.click(screen.getByRole("button", { name: "Try again" }));
  expect(state.refresh).toHaveBeenCalledOnce();
});

it("renders an OIDC sign-in link without collecting a credential", () => {
  useSession.mockReturnValue({
    state: { kind: "anonymous", token: null, message: null, epoch: 2 },
    ...actions(),
  });
  render(<App />);
  expect(screen.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/auth/login");
  expect(screen.queryByLabelText("Bootstrap credential")).toBeNull();
});
