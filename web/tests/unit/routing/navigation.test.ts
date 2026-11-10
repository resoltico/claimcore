import { expect, it, vi } from "vitest";
import {
  navigate,
  routeSnapshot,
  setNavigationBlocker,
  acquireNavigationLock,
} from "../../../src/routing/navigation";

it("passes history destinations to the existing departure guard and preserves denials and busy locks", () => {
  navigate("/cases/new", true);
  const guard = vi.fn(
    (_proceed: () => void, destination?: string) =>
      destination === "/settings" || destination === "/cases/new",
  );
  const release = setNavigationBlocker(guard);
  const historyMove = (path: string) => {
    window.history.replaceState(null, "", path);
    window.dispatchEvent(new PopStateEvent("popstate"));
  };
  let releaseLock: (() => void) | undefined;
  try {
    historyMove("/settings");
    expect(routeSnapshot()).toBe("/settings");
    expect(guard.mock.lastCall?.[1]).toBe("/settings");
    historyMove("/cases/new");
    expect(routeSnapshot()).toBe("/cases/new");
    historyMove("/people");
    expect(guard.mock.lastCall?.[1]).toBe("/people");
    expect(routeSnapshot()).toBe("/cases/new");
    expect(window.location.pathname).toBe("/cases/new");
    releaseLock = acquireNavigationLock();
    historyMove("/settings");
    expect(routeSnapshot()).toBe("/cases/new");
    expect(guard).toHaveBeenCalledTimes(3);
  } finally {
    releaseLock?.();
    release();
  }
});
