import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { useAuthorityOperation } from "../src/hooks/operation/useAuthorityOperation";
import { service } from "../src/api/service";
import { generatedWebValue } from "./contract-corpus.fixtures";
const preparedHook = () => {
  const view = renderHook(({ token }) => useAuthorityOperation(token, "REGISTER_ACTOR", vi.fn()), {
    initialProps: { token: "first" },
  });
  act(() => {
    view.result.current.setValues({
      operationId: "10000000-0000-4000-8000-000000000001",
      "target.kind": "SERVICE",
      "target.issuer": "https://issuer.example/",
      "target.subject": "synthetic-service",
    });
  });
  act(() => {
    view.result.current.review();
  });
  act(() => {
    view.result.current.setConfirmed(true);
  });
  return view;
};
it("coalesces authority execution before React commits busy state", async () => {
  let finish: ((value: Awaited<ReturnType<typeof service.authorityExecute>>) => void) | undefined;
  const pending = new Promise<Awaited<ReturnType<typeof service.authorityExecute>>>((resolve) => {
    finish = resolve;
  });
  const send = vi.spyOn(service, "authorityExecute").mockReturnValue(pending);
  const view = preparedHook();
  let first: Promise<void> = Promise.resolve();
  act(() => {
    first = view.result.current.execute();
    void view.result.current.execute();
  });
  expect(send).toHaveBeenCalledOnce();
  await act(async () => {
    finish?.({ kind: "outcome", status: 200, value: generatedWebValue("authority.execute") });
    await first;
  });
  expect(view.result.current.uncertain).toBe(false);
  expect(view.result.current.prepared).toBeNull();
  expect(view.result.current.values["operationId"]).not.toBe(
    "10000000-0000-4000-8000-000000000001",
  );
  send.mockRestore();
});
it("ignores an authority completion from a superseded session context", async () => {
  let finish: ((value: Awaited<ReturnType<typeof service.authorityExecute>>) => void) | undefined;
  const pending = new Promise<Awaited<ReturnType<typeof service.authorityExecute>>>((resolve) => {
    finish = resolve;
  });
  const send = vi.spyOn(service, "authorityExecute").mockReturnValue(pending);
  const view = preparedHook();
  let first: Promise<void> = Promise.resolve();
  act(() => {
    first = view.result.current.execute();
  });
  view.rerender({ token: "second" });
  expect(view.result.current.uncertain).toBe(true);
  await act(async () => {
    finish?.({ kind: "deliveryFailure", notice: { kind: "local", reason: "unreachable" } });
    await first;
  });
  expect(view.result.current.uncertain).toBe(true);
  expect(view.result.current.receipt).toBeNull();
  send.mockRestore();
});
