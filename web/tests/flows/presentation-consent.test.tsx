import { act } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { service } from "../../src/api/service";
import { hook, pending, preview } from "../support/operation-hook.fixtures";
import { current } from "../support/operation-form.fixtures";
afterEach(() => vi.restoreAllMocks());
it("consent belongs to a completed review and is cleared by a fresh preview or input edit", async () => {
  const read = pending<Awaited<ReturnType<typeof service.preview>>>();
  vi.spyOn(service, "preview").mockResolvedValueOnce(preview()).mockReturnValueOnce(read.promise);
  const execute = vi.spyOn(service, "execute");
  const view = hook();
  act(() => {
    view.result.current.confirm(true);
  });
  expect(view.result.current.confirmed).toBe(false);
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.confirm(true);
  });
  expect(view.result.current.confirmed).toBe(true);
  let completion: Promise<void>;
  act(() => {
    completion = view.result.current.preview();
    view.result.current.confirm(true);
  });
  expect(view.result.current.confirmed).toBe(false);
  expect(view.result.current.review).toBeNull();
  await act(async () => {
    await view.result.current.execute();
  });
  expect(execute).not.toHaveBeenCalled();
  await act(async () => {
    read.finish(preview());
    await completion!;
  });
  expect(view.result.current.confirmed).toBe(false);
  act(() => {
    view.result.current.confirm(true);
    view.result.current.edit("act.reason", "Changed");
  });
  expect(view.result.current.confirmed).toBe(false);
  expect(view.result.current.review).toBeNull();
});
it.each(["token", "target"] as const)(
  "masks stale review synchronously when %s changes and rejects the late result",
  async (changed) => {
    const read = pending<Awaited<ReturnType<typeof service.preview>>>();
    vi.spyOn(service, "preview").mockReturnValue(read.promise);
    const view = hook();
    let completion: Promise<void>;
    act(() => {
      completion = view.result.current.preview();
    });
    view.rerender({
      credential: changed === "token" ? "other-token" : "token",
      target: changed === "target" ? { ...current, caseReference: "OTHER" } : current,
    });
    expect(view.result.current.review).toBeNull();
    expect(view.result.current.receipt).toBeNull();
    await act(async () => {
      read.finish(preview());
      await completion!;
    });
    expect(view.result.current.review).toBeNull();
    expect(view.result.current.confirmed).toBe(false);
  },
);
