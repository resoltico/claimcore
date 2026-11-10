import { act, renderHook } from "@testing-library/react";
import { expect, it } from "vitest";
import { useWorkspaceMutationLock } from "../../../src/hooks/useWorkspaceMutationLock";
import { acquireNavigationLock, navigate } from "../../../src/routing/navigation";

it("one operation release cannot unlock another operation in the same workspace", () => {
  const owner = {};
  const first = {};
  const second = {};
  const view = renderHook(() => useWorkspaceMutationLock(owner));
  try {
    act(() => {
      view.result.current.change(true, first);
      view.result.current.change(true, second);
    });
    expect(view.result.current.locked).toBe(true);
    expect(navigate("/people", true)).toBe(false);
    act(() => {
      view.result.current.change(false, first);
    });
    expect(view.result.current.locked).toBe(true);
    expect(navigate("/settings", true)).toBe(false);
    act(() => {
      view.result.current.change(false, first);
    });
    expect(view.result.current.locked).toBe(true);
    act(() => {
      view.result.current.change(false, second);
    });
    expect(view.result.current.locked).toBe(false);
    expect(navigate("/settings", true)).toBe(true);
  } finally {
    view.unmount();
  }
});
it("late acquire/release from an earlier owner cannot affect the replacement owner's lease", () => {
  const firstOwner = {};
  const secondOwner = {};
  const oldLease = {};
  const newLease = {};
  const view = renderHook<ReturnType<typeof useWorkspaceMutationLock>, { owner: object | null }>(
    ({ owner }) => useWorkspaceMutationLock(owner),
    {
      initialProps: { owner: firstOwner },
    },
  );
  try {
    const oldChange = view.result.current.change;
    act(() => {
      oldChange(true, oldLease);
    });
    view.rerender({ owner: secondOwner });
    expect(view.result.current.locked).toBe(false);
    act(() => {
      view.result.current.change(true, newLease);
    });
    act(() => {
      oldChange(false, oldLease);
      oldChange(true, {});
    });
    expect(view.result.current.locked).toBe(true);
    expect(navigate("/people", true)).toBe(false);
    act(() => {
      view.result.current.change(false, newLease);
    });
    expect(navigate("/people", true)).toBe(true);
    view.rerender({ owner: null });
    act(() => {
      oldChange(true, oldLease);
    });
    expect(view.result.current.locked).toBe(false);
    expect(navigate("/settings", true)).toBe(true);
  } finally {
    view.unmount();
  }
});
it("workspace teardown releases only its own navigation lease", () => {
  const outside = acquireNavigationLock();
  const owner = {};
  const view = renderHook(() => useWorkspaceMutationLock(owner));
  act(() => {
    view.result.current.change(true, {});
  });
  view.unmount();
  expect(navigate("/people", true)).toBe(false);
  outside();
  expect(navigate("/people", true)).toBe(true);
});
