import { recoveryOptions } from "../support/recovery.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { authorityAcceptance, authorityPreview } from "../support/authority-operation.fixtures";
import { authorityReceipt } from "../support/reply.fixtures";
beforeEach(() => {
  authorityPreview();
});
afterEach(() => vi.restoreAllMocks());
const preparedHook = async () => {
  const view = renderHook(
    ({ credential }) =>
      useOperation({
        ...recoveryOptions,
        subject: "AUTHORITY",
        kind: "REGISTER",
        current: null,
        credential,
        onLockChange: vi.fn(),
      }),
    {
      initialProps: {
        credential: "first",
      },
    },
  );
  act(() => {
    for (const [path, value] of Object.entries({
      "input:principal.kind": "SERVICE",
      "input:principal.issuer": "https://issuer.example/",
      "input:principal.subject": "synthetic-service",
      "input:profile.displayName": "Synthetic service",
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
  await waitFor(() => {
    expect(send).toHaveBeenCalledOnce();
  });
  await act(async () => {
    completion.finish(authorityAcceptance(send.mock.calls[0]![0]));
    await task;
  });
  expect(view.result.current.priorUncertain).toBe(false);
  expect(view.result.current.phase).toBe("ACCEPTED");
});
it("keeps a superseded session attempt pending then settles failure without publishing stale content", async () => {
  const completion = pending<Awaited<ReturnType<typeof service.execute>>>();
  vi.spyOn(service, "execute").mockReturnValue(completion.promise);
  const view = await preparedHook();
  const { prepared } = view.result.current;
  let task = Promise.resolve();
  act(() => {
    task = view.result.current.execute();
  });
  await waitFor(() => {
    expect(vi.mocked(service.execute)).toHaveBeenCalledOnce();
  });
  view.rerender({
    credential: "second",
  });
  expect(view.result.current.phase).toBe("SUBMITTING");
  expect(view.result.current.receipt).toBeNull();
  expect(view.result.current.review).toBeNull();
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
    action: "ASK_FOR_ACCESS",
  },
  conflict: {
    code: "IDEMPOTENCY_CONFLICT",
    action: "STOP_AND_INVESTIGATE",
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
        document: accepted.value.outcome.document,
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
          advice: {
            message: { messageId: "diagnostic.CORE_STORE_UNAVAILABLE", arguments: [] },
            action: "RETRY_SAFE" as const,
          },
        }
      : {
          kind: "REJECTED" as const,
          rejection: {
            kind: "REFUSED" as const,
            advice: {
              message: {
                messageId:
                  next === "conflict"
                    ? "diagnostic.IDEMPOTENCY_CONFLICT"
                    : "diagnostic.RESOURCE_UNAVAILABLE",
                arguments: [],
              },
              action: refusalData[next].action,
            },
          },
        };
  return {
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
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
  mockEndpoint("operation.get").mockImplementation((_credential, input) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: input.operationId,
        outcome: {
          kind: "ACCEPTED",
          byRequester: true,
          document: {
            ...authorityReceipt(),
            context: {
              kind: "RECEIPT",
              recordedFields: [],
              target: { kind: "AUTHORITY", actorId: input.operationId },
              caseLink: null,
              refreshCase: null,
            },
          },
        },
      },
    }),
  );
  const view = await preparedHook();
  await act(async () => {
    await view.result.current.execute();
  });
  const prepared = view.result.current.prepared!;
  await act(async () => {
    await service.operationGet(
      prepared.input.operationId,
      "first",
      recoveryOptions.recoveryScope.installationId,
    );
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
