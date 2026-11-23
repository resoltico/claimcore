import { markSignedOut } from "../../src/routing/signInAttempt";
import { localNotice } from "../../src/protocol/notices";
import { render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { App } from "../../src/App";

const useSession = vi.hoisted(() => vi.fn());

vi.mock("../../src/session/useSession", () => ({ useSession }));

const actions = () => ({ logout: vi.fn(), endedBrowserSessions: vi.fn(), refresh: vi.fn() });

it("renders the loading shell before local session discovery completes", () => {
  useSession.mockReturnValue({ state: { kind: "loading", epoch: 0 }, ...actions() });
  render(<App />);
  expect(screen.getByText("Loading session…")).toBeVisible();
});

it("renders a recoverable session failure and invokes its refresh action", async () => {
  const user = userEvent.setup();
  const state = actions();
  useSession.mockReturnValue({
    state: { kind: "failure", message: localNotice("unreachable"), epoch: 1 },
    ...state,
  });
  render(<App />);
  expect(screen.getByRole("alert")).toHaveTextContent(
    "ClaimCore could not be reached. Check your connection.",
  );
  await user.click(screen.getByRole("button", { name: "Try again" }));
  expect(state.refresh).toHaveBeenCalledOnce();
});

it("offers OIDC sign-in after explicit sign-out without collecting a credential", () => {
  markSignedOut(window.sessionStorage);
  useSession.mockReturnValue({
    state: { kind: "anonymous", token: null, message: null, epoch: 2 },
    ...actions(),
  });
  render(<App />);
  expect(screen.getByRole("heading", { name: "You signed out" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Sign in again" })).toHaveAttribute(
    "href",
    "/auth/login?language=en",
  );
  expect(screen.queryByLabelText("Bootstrap credential")).toBeNull();
});
