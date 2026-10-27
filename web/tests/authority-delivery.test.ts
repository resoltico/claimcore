import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { useOperation } from "../src/hooks/operation/useOperation";
import { service, type ApiResult, type ServiceResponse } from "../src/api/service";
import { authorityAcceptance, authorityPreview } from "./authority-operation.fixtures";
import { wireReply } from "./contract-corpus.fixtures";
beforeEach(() => {
  authorityPreview();
});
afterEach(() => vi.restoreAllMocks());
const preparedHook = async () => {
  const view = renderHook(
    ({ token }) =>
      useOperation({
        subject: "AUTHORITY",
        kind: "REGISTER",
        current: null,
        token,
        onLockChange: vi.fn(),
      }),
    {
      initialProps: {
        token: "first",
      },
    },
  );
  act(() => {
    for (const [path, value] of Object.entries({
      "target.kind": "SERVICE",
      "target.issuer": "https://issuer.example/",
      "target.subject": "synthetic-service",
    })) {
      view.result.current.edit(path, value);
    }
  });
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.confirm(true);
  });
  expect(view.result.current.confirmed).toBe(true);
  return view;
};
const pending = <T>() => {
  let finish!: (value: T) => void;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return {
    promise,
    finish,
  };
};
it("coalesces authority execution before React commits busy state", async () => {
  const completion = pending<Awaited<ReturnType<typeof service.execute>>>();
  const send = vi.spyOn(service, "execute").mockReturnValue(completion.promise);
  const view = await preparedHook();
  let task = Promise.resolve();
  act(() => {
    task = view.result.current.execute();
    void view.result.current.execute();
  });
  expect(send).toHaveBeenCalledOnce();
  await act(async () => {
    completion.finish(authorityAcceptance(send.mock.calls[0]![0]));
    await task;
  });
  expect(view.result.current.priorUncertain).toBe(false);
  expect(view.result.current.phase).toBe("ACCEPTED");
});
it("ignores completion from a superseded session while retaining the exact frozen request", async () => {
  const completion = pending<Awaited<ReturnType<typeof service.execute>>>();
  vi.spyOn(service, "execute").mockReturnValue(completion.promise);
  const view = await preparedHook();
  const { prepared } = view.result.current;
  let task = Promise.resolve();
  act(() => {
    task = view.result.current.execute();
  });
  view.rerender({
    token: "second",
  });
  expect(view.result.current.priorUncertain).toBe(true);
  await act(async () => {
    completion.finish({
      kind: "deliveryFailure",
      notice: {
        kind: "local",
        reason: "unreachable",
      },
    });
    await task;
  });
  expect(view.result.current.priorUncertain).toBe(true);
  expect(view.result.current.receipt).toBeNull();
  expect(view.result.current.prepared).toBe(prepared);
});
type RetryKind = "refusal" | "conflict" | "failed" | "not-started" | "replay";
const refusalData = {
  refusal: {
    code: "RESOURCE_UNAVAILABLE",
    action: "NONE_REQUIRED",
    target: null,
    actualRevision: null,
    limits: {},
  },
  conflict: {
    code: "IDEMPOTENCY_CONFLICT",
    action: "STOP_AND_INVESTIGATE",
    target: null,
    actualRevision: null,
    limits: {},
  },
} as const;
const replayResult = (
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => {
  const accepted = authorityAcceptance(prepared);
  if (accepted.kind !== "outcome" || accepted.value.outcome.kind !== "ACCEPTED") {
    throw new Error("Acceptance required.");
  }
  return {
    ...accepted,
    value: {
      ...accepted.value,
      outcome: {
        kind: "ALREADY_ACCEPTED",
        data: accepted.value.outcome.data,
      },
    },
  };
};
const retryResult = (
  next: RetryKind,
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => {
  if (next === "replay") {
    return replayResult(prepared);
  }
  if (next === "not-started") {
    return {
      kind: "hostFailure",
      status: 409,
      failure: {
        code: "CONTRACT_MISMATCH",
        phase: "NOT_STARTED",
      },
    };
  }
  const outcome =
    next === "failed"
      ? {
          kind: "FAILED" as const,
          data: {
            code: "CORE_STORE_UNAVAILABLE" as const,
            action: "RETRY_SAFE" as const,
          },
        }
      : {
          kind: "REJECTED" as const,
          data: refusalData[next],
        };
  return {
    kind: "outcome",
    status: 200,
    value: {
      operationId: prepared.input.operationId,
      outcome,
    },
  };
};
it.each(["refusal", "conflict", "failed", "not-started", "replay"] as const)(
  "only acceptance resolves uncertainty after %s",
  async (next) => {
    const send = vi.spyOn(service, "execute").mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: {
        kind: "local",
        reason: "unreachable",
      },
    });
    const view = await preparedHook();
    await act(async () => {
      await view.result.current.execute();
    });
    const prepared = view.result.current.prepared!;
    send.mockResolvedValue(retryResult(next, prepared));
    await act(async () => {
      await view.result.current.execute();
    });
    expect(send.mock.calls[1]![0]).toBe(prepared);
    expect(view.result.current.priorUncertain).toBe(next !== "replay");
    expect(view.result.current.prepared).toBe(prepared);
  },
);
it("observation cannot resolve pending authority content", async () => {
  vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: {
      kind: "local",
      reason: "unreachable",
    },
  });
  vi.spyOn(service, "observe").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: wireReply("operation.observe"),
  });
  const view = await preparedHook();
  await act(async () => {
    await view.result.current.execute();
  });
  const prepared = view.result.current.prepared!;
  await act(async () => {
    await service.observe(prepared.input.operationId, "first");
  });
  expect(view.result.current.priorUncertain).toBe(true);
  expect(view.result.current.prepared).toBe(prepared);
});
it("requires warned discard while unresolved and refuses discard during dispatch", async () => {
  const completion = pending<Awaited<ReturnType<typeof service.execute>>>();
  vi.spyOn(service, "execute").mockReturnValue(completion.promise);
  const view = await preparedHook();
  const { prepared } = view.result.current;
  let task = Promise.resolve();
  act(() => {
    task = view.result.current.execute();
    view.result.current.discard(true);
  });
  expect(view.result.current.prepared).toBe(prepared);
  await act(async () => {
    completion.finish({
      kind: "deliveryFailure",
      notice: {
        kind: "local",
        reason: "unreachable",
      },
    });
    await task;
  });
  act(() => {
    view.result.current.discard();
  });
  expect(view.result.current.prepared).toBe(prepared);
  act(() => {
    view.result.current.discard(true);
  });
  expect(view.result.current.prepared).toBeNull();
  expect(view.result.current.priorUncertain).toBe(false);
});
