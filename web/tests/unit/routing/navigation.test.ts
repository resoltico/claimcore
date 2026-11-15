import { expect, it } from "vitest";
import { waitFor } from "@testing-library/react";
import {
  subscribeRoute,
  navigate,
  routeSnapshot,
  setNavigationBlocker,
  cancelPendingNavigation,
  acquireNavigationLock,
  replaceDisclosureRoute,
  setRouteFocusOwner,
} from "../../../src/routing/navigation";

it("preserves actual Back/Forward entries when traversal is allowed", async () => {
  navigate("/cases/history-start", true);
  navigate("/settings");
  navigate("/cases/history-end");
  const release = setNavigationBlocker(() => true);
  try {
    window.history.back();
    await waitFor(() => {
      expect(routeSnapshot()).toBe("/settings");
    });
    window.history.forward();
    await waitFor(() => {
      expect(routeSnapshot()).toBe("/cases/history-end");
    });
  } finally {
    release();
  }
});

it("restores a multi-entry traversal before consent and replays it once without truncating Forward", async () => {
  navigate("/cases/consent-start", true);
  navigate("/settings");
  navigate("/cases/consent-end");
  let approve: (() => void) | undefined;
  const release = setNavigationBlocker((proceed) => {
    approve = proceed;
    return false;
  });
  try {
    window.history.go(-2);
    await waitFor(() => {
      expect(approve).toBeDefined();
    });
    await waitFor(() => {
      expect(window.location.pathname).toBe("/cases/consent-end");
    });
    cancelPendingNavigation();
    expect(routeSnapshot()).toBe("/cases/consent-end");
    approve = undefined;
    window.history.go(-2);
    await waitFor(() => {
      expect(approve).toBeDefined();
    });
    await waitFor(() => {
      expect(window.location.pathname).toBe("/cases/consent-end");
    });
    const proceed = approve as (() => void) | undefined;
    proceed?.();
    proceed?.();
    await waitFor(() => {
      expect(routeSnapshot()).toBe("/cases/consent-start");
    });
    release();
    window.history.forward();
    await waitFor(() => {
      expect(routeSnapshot()).toBe("/settings");
    });
    window.history.forward();
    await waitFor(() => {
      expect(routeSnapshot()).toBe("/cases/consent-end");
    });
  } finally {
    release();
    cancelPendingNavigation();
  }
});

it("requests native departure prompting for drafts and delivery and releases it", () => {
  const warned = () => !window.dispatchEvent(new Event("beforeunload", { cancelable: true }));
  const release = setNavigationBlocker(() => false);
  expect(warned()).toBe(true);
  release();
  const unlock = acquireNavigationLock();
  expect(warned()).toBe(true);
  unlock();
  expect(warned()).toBe(false);
});

it("retains entry identity and unrelated state on disclosure replacement", () => {
  const state = {
    claimcoreNavigation: { group: "external-group", index: 1, key: "external-entry" },
    external: "kept",
  };
  window.history.replaceState(state, "");
  replaceDisclosureRoute("/people/selected");
  expect(window.history.state).toEqual(state);
});

it("cancels a pending traversal when the verified session scope changes", async () => {
  setRouteFocusOwner("actor:session-one");
  navigate("/cases/session-start", true);
  navigate("/cases/session-draft");
  const continuation: { approve?: () => void } = {};
  const release = setNavigationBlocker((proceed) => {
    continuation.approve = proceed;
    return false;
  });
  try {
    window.history.back();
    await waitFor(() => {
      expect(continuation.approve).toBeDefined();
    });
    await waitFor(() => {
      expect(window.location.pathname).toBe("/cases/session-draft");
    });
    setRouteFocusOwner("actor:session-two");
    continuation.approve?.();
    expect(routeSnapshot()).toBe("/cases/session-draft");
    release();
    expect(navigate("/cases/session-new")).toBe(true);
  } finally {
    release();
    cancelPendingNavigation(true);
    setRouteFocusOwner(null);
  }
});

it("compensates an already dispatched approval after session replacement without publishing its target", async () => {
  setRouteFocusOwner("race:one");
  navigate("/first", true);
  navigate("/middle");
  navigate("/draft");
  const { length } = history;
  const continuation: { approve?: () => void } = {};
  const release = setNavigationBlocker((proceed) => {
    continuation.approve = proceed;
    return false;
  });
  history.go(-2);
  await waitFor(() => {
    expect(continuation.approve).toBeDefined();
    expect(location.pathname).toBe("/draft");
  });
  const published: string[] = [];
  const unsubscribe = subscribeRoute(() => {
    published.push(routeSnapshot());
  });
  continuation.approve?.();
  setRouteFocusOwner("race:two");
  release();
  // Wait for the replay and its compensating traversal to be processed.
  await new Promise((resolve) => {
    setTimeout(resolve, 100);
  });
  expect(location.pathname).toBe("/draft");
  expect(routeSnapshot()).toBe("/draft");
  expect(published).not.toContain("/first");
  expect(history.length).toBe(length);
  unsubscribe();
  history.back();
  await waitFor(() => {
    expect(routeSnapshot()).toBe("/middle");
  });
  history.forward();
  await waitFor(() => {
    expect(routeSnapshot()).toBe("/draft");
  });
  setRouteFocusOwner(null);
});

it("publishes actual unknown and null entry traversal without inventing repair entries", async () => {
  navigate("/known", true);
  history.pushState(null, "", "/unknown-null");
  history.pushState({ external: "kept" }, "", "/unknown-object");
  history.back();
  await waitFor(() => {
    expect(routeSnapshot()).toBe("/unknown-null");
  });
  const { length } = history;
  history.forward();
  await waitFor(() => {
    expect(routeSnapshot()).toBe("/unknown-object");
  });
  expect(history.state).toEqual({ external: "kept" });
  expect(history.length).toBe(length);
});

it("composes operation and inline draft departure decisions without replacing either owner", () => {
  const approvals: (() => void)[] = [];
  const calls: string[] = [];
  const note = setNavigationBlocker((proceed) => {
    calls.push("note");
    approvals.push(proceed);
    return false;
  }, 0);
  const operation = setNavigationBlocker((proceed) => {
    calls.push("operation");
    approvals.push(proceed);
    return false;
  });
  try {
    expect(navigate("/composed-destination")).toBe(false);
    expect(calls).toEqual(["operation"]);
    approvals.shift()!();
    expect(calls).toEqual(["operation", "note"]);
    expect(routeSnapshot()).not.toBe("/composed-destination");
    approvals.shift()!();
    expect(routeSnapshot()).toBe("/composed-destination");
  } finally {
    operation();
    note();
  }
});
