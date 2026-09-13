import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { SessionState } from "../src/hooks/useSession";

afterEach(() => vi.resetModules());

const renderState = async (state: SessionState) => {
  vi.resetModules();
  vi.doMock("../src/hooks/useSession", () => ({
    useSession: () => ({ state, login: vi.fn(), logout: vi.fn(), refresh: vi.fn() }),
  }));
  const { App } = await import("../src/App");
  return render(<App />);
};

it("renders loading and refreshable session failures", async () => {
  const loading = await renderState({ kind: "loading", epoch: 0 });
  expect(screen.getByText("Loading local session…")).toBeVisible();
  loading.unmount();
  await renderState({ kind: "failure", message: "Safe failure", epoch: 1 });
  expect(screen.getByRole("alert")).toHaveTextContent("Safe failure");
  await userEvent.setup().click(screen.getByRole("button", { name: "Try again" }));
});

it("renders anonymous login and authenticated dashboard paths", async () => {
  const anonymous = await renderState({ kind: "anonymous", token: null, message: null, epoch: 2 });
  expect(screen.getByRole("button", { name: "Sign in" })).toBeDisabled();
  anonymous.unmount();
  const rejected = await renderState({
    kind: "anonymous",
    token: "anonymous",
    message: "Credential was rejected.",
    epoch: 3,
  });
  expect(screen.getByRole("alert")).toHaveTextContent("Credential was rejected.");
  expect(screen.getByRole("button", { name: "Sign in" })).not.toBeDisabled();
  rejected.unmount();
  await renderState({ kind: "authenticated", token: "token", epoch: 4 });
  expect(screen.getByText("Loading core definition…")).toBeVisible();
});
