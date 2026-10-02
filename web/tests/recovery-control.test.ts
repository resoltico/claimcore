import { expect, it, vi } from "vitest";
import { createRecoveryAdmission } from "../src/hooks/recoveryControl";

it("releases the recovery navigation lock when an action throws", async () => {
  const admission = createRecoveryAdmission();
  const lock = vi.fn();
  await expect(
    admission.mutate(() => Promise.reject(new Error("Synthetic action failure")), lock),
  ).rejects.toThrow("Synthetic action failure");
  expect(lock.mock.calls).toEqual([[true], [false]]);
  expect(admission.idle()).toBe(true);
});

it("coalesces recovery mutations and previews without replacing an in-flight navigation lock", async () => {
  let finish: () => void = () => undefined;
  const pending = new Promise<void>((resolve) => {
    finish = resolve;
  });
  const admission = createRecoveryAdmission();
  const lock = vi.fn();
  const newerLock = vi.fn();
  const action = vi.fn(() => pending);
  const task = admission.mutate(action, lock);
  await admission.mutate(action, newerLock);
  await admission.read(action);
  expect(action).toHaveBeenCalledOnce();
  expect(newerLock).not.toHaveBeenCalled();
  expect(admission.idle()).toBe(false);
  finish();
  await task;
  expect(lock.mock.calls).toEqual([[true], [false]]);
  expect(admission.idle()).toBe(true);
});
