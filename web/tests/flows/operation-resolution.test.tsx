import { useOperation } from "../../src/hooks/operation/useOperation";
import { current } from "../support/operation-form.fixtures";
import { act, waitFor, renderHook } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { getButton, render, screen } from "../support/presentation-test-support";
import { service } from "../../src/api/service";
import { UnresolvedNotice } from "../../src/views/operation/UnresolvedNotice";
import {
  unresolvedOperation,
  useUnresolvedOperation,
} from "../../src/views/operation/unresolvedOperation";
import { preview } from "../support/operation-hook.fixtures";
import { wireReply } from "../support/contract-corpus.fixtures";
afterEach(() => {
  vi.restoreAllMocks();
  sessionStorage.clear();
});
const identity = {
  operationId: "10000000-0000-4000-8000-000000000001",
  subject: "CASE",
  act: "CLOSE",
} as const;
it("reload notice retains identity only and resolves without sending a mutation", async () => {
  unresolvedOperation.save(identity);
  const resolve = vi.spyOn(service, "resolve").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "NOT_ACCEPTED",
        data: null,
      },
    },
  });
  const execute = vi.spyOn(service, "execute");
  render(<UnresolvedNotice />);
  expect(
    screen.queryByRole("button", {
      name: /retry exact|send again/iu,
    }),
  ).toBeNull();
  await userEvent.click(getButton(/^Check outcome /u));
  expect(resolve).toHaveBeenCalledExactlyOnceWith(identity.operationId, "token");
  expect(execute).not.toHaveBeenCalled();
  expect(unresolvedOperation.read()).toBeNull();
  expect(await screen.findByText(/will never be accepted/u)).toBeVisible();
});
const closeOperation = () =>
  renderHook(() => {
    const model = useOperation({
      subject: "CASE",
      kind: "CLOSE",
      current,
      credential: "token",
      onLockChange: vi.fn(),
    });
    useUnresolvedOperation(model);
    return model;
  });
it("uncertain submit freezes its bytes and NOT_ACCEPTED permits a new identity", async () => {
  vi.spyOn(service, "preview").mockResolvedValue(preview());
  vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: {
      kind: "local",
      reason: "unreachable",
    },
  });
  vi.spyOn(service, "resolve").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "NOT_ACCEPTED",
        data: null,
      },
    },
  });
  const view = closeOperation();
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.confirm(true);
  });
  const { prepared } = view.result.current;
  await act(async () => {
    await view.result.current.execute();
  });
  await waitFor(() => {
    expect(view.result.current.phase).toBe("UNRESOLVED");
  });
  expect(view.result.current.prepared).toBe(prepared);
  expect(Object.keys(unresolvedOperation.read() ?? {}).sort()).toEqual([
    "act",
    "operationId",
    "subject",
  ]);
  act(() => {
    view.result.current.resolve();
  });
  await waitFor(() => {
    expect(view.result.current.phase).toBe("EDITING");
  });
  expect(view.result.current.values["operationId"]).not.toBe(prepared?.input.operationId);
  expect(unresolvedOperation.read()).toBeNull();
});
it("storage failures do not escape admission or completion", () => {
  for (const method of ["getItem", "setItem", "removeItem"] as const) {
    vi.spyOn(Storage.prototype, method).mockImplementation(() => {
      throw new Error("unavailable");
    });
  }
  expect(() => {
    unresolvedOperation.save(identity);
  }).not.toThrow();
  expect(unresolvedOperation.read()).toBeNull();
  expect(() => {
    unresolvedOperation.clear(identity.operationId);
  }).not.toThrow();
});
it("resolve acceptance returns the stored receipt", async () => {
  const reply = wireReply("operation.execute");
  if (reply.outcome.kind !== "ACCEPTED") {
    throw new Error("Acceptance example missing");
  }
  unresolvedOperation.save(identity);
  vi.spyOn(service, "resolve").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "ACCEPTED",
        data: reply.outcome.data,
      },
    },
  });
  render(<UnresolvedNotice />);
  await userEvent.click(getButton(/^Check outcome /u));
  await waitFor(() => {
    expect(unresolvedOperation.read()).toBeNull();
  });
});
it("a different accepted operation cannot discard an unresolved identity", () => {
  unresolvedOperation.save(identity);
  unresolvedOperation.clear("20000000-0000-4000-8000-000000000001");
  expect(unresolvedOperation.read()).toEqual(identity);
  unresolvedOperation.clear(identity.operationId);
  expect(unresolvedOperation.read()).toBeNull();
});
