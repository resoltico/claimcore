import { expect, it, vi } from "vitest";
import type { ApiResult } from "../../../src/protocol/types";
import { ReadAdmission } from "../../../src/api/readAdmission";
const result = (value: string): ApiResult<string> => ({ kind: "outcome", value, status: 200 });
it("bounds different reads at two and starts the next only after a response completes", async () => {
  const admission = new ReadAdmission();
  const first = Promise.withResolvers<ApiResult<string>>();
  const second = Promise.withResolvers<ApiResult<string>>();
  const next = vi.fn(() => Promise.resolve(result("third")));
  const a = admission.run(new AbortController().signal, () => first.promise);
  const b = admission.run(new AbortController().signal, () => second.promise);
  const c = admission.run(new AbortController().signal, next);
  await Promise.resolve();
  expect(next).not.toHaveBeenCalled();
  first.resolve(result("first"));
  await a;
  expect(await c).toEqual(result("third"));
  second.resolve(result("second"));
  await b;
  expect(next).toHaveBeenCalledTimes(1);
});
it("removes a queued obsolete scope without invoking its network handoff", async () => {
  const admission = new ReadAdmission();
  const first = Promise.withResolvers<ApiResult<string>>();
  const second = Promise.withResolvers<ApiResult<string>>();
  const a = admission.run(new AbortController().signal, () => first.promise);
  const b = admission.run(new AbortController().signal, () => second.promise);
  const controller = new AbortController();
  const network = vi.fn(() => Promise.resolve(result("foreign")));
  const cancelled = admission.run(controller.signal, network);
  controller.abort();
  expect(await cancelled).toEqual({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "cancelled" },
    dispatched: false,
  });
  first.resolve(result("first"));
  second.resolve(result("second"));
  await Promise.all([a, b]);
  expect(network).not.toHaveBeenCalled();
});
it("checks scope cancellation again after a queued read receives a turn, before dispatch", async () => {
  const admission = new ReadAdmission();
  const firstController = new AbortController();
  const controller = new AbortController();
  const first = Promise.withResolvers<ApiResult<string>>();
  const second = Promise.withResolvers<ApiResult<string>>();
  const a = admission.run(firstController.signal, () => first.promise);
  const b = admission.run(new AbortController().signal, () => second.promise);
  const network = vi.fn(() => Promise.resolve(result("stale")));
  const cancelled = admission.run(controller.signal, network);
  firstController.abort();
  controller.abort();
  expect(await cancelled).toMatchObject({ kind: "deliveryFailure", dispatched: false });
  expect(network).not.toHaveBeenCalled();
  first.resolve(result("first"));
  second.resolve(result("second"));
  await Promise.all([a, b]);
  expect(
    await admission.run(new AbortController().signal, () => Promise.resolve(result("current"))),
  ).toEqual(result("current"));
});
it("an active cancelled read releases its turn without a late completion releasing another turn", async () => {
  const admission = new ReadAdmission();
  const controller = new AbortController();
  const first = Promise.withResolvers<ApiResult<string>>();
  const second = Promise.withResolvers<ApiResult<string>>();
  const a = admission.run(controller.signal, () => first.promise);
  const b = admission.run(new AbortController().signal, () => second.promise);
  const third = Promise.withResolvers<ApiResult<string>>();
  const next = vi.fn(() => third.promise);
  const c = admission.run(new AbortController().signal, next);
  await Promise.resolve();
  controller.abort();
  await Promise.resolve();
  expect(next).toHaveBeenCalledTimes(1);
  first.resolve(result("obsolete"));
  await a;
  const fourth = vi.fn(() => Promise.resolve(result("fourth")));
  const d = admission.run(new AbortController().signal, fourth);
  await Promise.resolve();
  expect(fourth).not.toHaveBeenCalled();
  third.resolve(result("third"));
  await c;
  await d;
  second.resolve(result("second"));
  await b;
});
