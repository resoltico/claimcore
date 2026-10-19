import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useOperationEditor } from "../src/hooks/operation/useOperationEditor";
import { service, type ApiResult, type ServiceResponse } from "../src/api/service";
import { definition, review } from "./service-ui.fixtures";
import { generatedWebValue } from "./contract-corpus.fixtures";

afterEach(() => vi.restoreAllMocks());
const editor = () =>
  renderHook(() =>
    useOperationEditor({
      token: "synthetic-token",
      definition,
      current: null,
      initialCommand: "OPEN",
      onClose: vi.fn(),
      onCommitted: vi.fn(),
      onMutationLockChange: vi.fn(),
    }),
  );
const preview = (): ApiResult<ServiceResponse<"case.preview">> => ({
  kind: "outcome",
  status: 200,
  value: {
    endpoint: "case.preview",
    operationId: "10000000-0000-4000-8000-000000000001",
    outcome: { tag: "REVIEWED", data: { review } },
  },
});
const rejected = (conflict: boolean): ApiResult<ServiceResponse<"case.execute">> => ({
  kind: "outcome",
  status: 200,
  value: {
    endpoint: "case.execute",
    operationId: "10000000-0000-4000-8000-000000000001",
    outcome: {
      tag: "REJECTED",
      data: {
        diagnostic: {
          id: conflict ? "OPERATION_CONTENT_CONFLICT" : "ACCESS_RESOURCE_UNAVAILABLE",
          parameters: {},
        },
        message: "Synthetic refusal",
        field: null,
        actualRevision: null,
        recommendedAction: "NONE_REQUIRED",
      },
    },
  },
});
const fill = (view: ReturnType<typeof editor>) => {
  act(() => {
    view.result.current.editReference("SYNTHETIC-CASE");
    for (const [name, value] of Object.entries({
      incidentDate: "2026-01-01",
      incidentNotificationDate: "2026-01-02",
      incidentCountry: "LV",
      claimantName: "Synthetic claimant",
      insurerName: "Synthetic insurer",
      claimedAmount: "1.00",
      claimedCurrency: "EUR",
    })) {
      view.result.current.edit(name, value);
    }
  });
};
it.each([false, true])(
  "case execution folds failed retry and exact conflict evidence; conflict=%s",
  async (conflict) => {
    vi.spyOn(service, "preview").mockResolvedValue(preview());
    const execute = vi
      .spyOn(service, "execute")
      .mockResolvedValueOnce({
        kind: "deliveryFailure",
        notice: { kind: "local", reason: "unreachable" },
      })
      .mockResolvedValue(rejected(conflict));
    const view = editor();
    fill(view);
    await act(async () => {
      await view.result.current.preview();
    });
    act(() => {
      view.result.current.setConfirmed(true);
    });
    await act(async () => {
      await view.result.current.submit();
    });
    expect(view.result.current.state.unresolved).toBe(true);
    const bytes = view.result.current.state.requestBytes;
    await act(async () => {
      await view.result.current.submit();
    });
    expect(view.result.current.state.unresolved).toBe(!conflict);
    expect(view.result.current.state.delivery).toBe(
      conflict ? "DEFINITELY_REJECTED" : "OUTCOME_UNKNOWN",
    );
    expect(view.result.current.state.requestBytes).toBe(bytes);
    expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  },
);
it("exact preview replay accepts through the production case reducer", async () => {
  const value = generatedWebValue("case.execute");
  if (value.outcome.tag !== "ACCEPTED") {
    throw new Error("Missing acceptance fixture");
  }
  vi.spyOn(service, "preview").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      endpoint: "case.preview",
      operationId: value.operationId,
      outcome: { tag: "ALREADY_ACCEPTED", data: value.outcome.data },
    },
  });
  const view = editor();
  fill(view);
  await act(async () => {
    await view.result.current.preview();
  });
  expect(view.result.current.state.delivery).toBe("ACCEPTED");
  expect(view.result.current.state.unresolved).toBe(false);
});
it("operation lookup cannot resolve the case editor's pending content", async () => {
  vi.spyOn(service, "preview").mockResolvedValue(preview());
  vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  vi.spyOn(service, "observe").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: generatedWebValue("operation.observe"),
  });
  const view = editor();
  fill(view);
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.setConfirmed(true);
  });
  await act(async () => {
    await view.result.current.submit();
  });
  const prior = view.result.current.state;
  await act(async () => {
    await service.observe(prior.operationId, "synthetic-token");
  });
  expect(view.result.current.state).toBe(prior);
  expect(view.result.current.state.unresolved).toBe(true);
});
it("exact preview content conflict refuses the prepared content without dispatching execution", async () => {
  const rejection = rejected(true);
  if (rejection.kind !== "outcome" || rejection.value.outcome.tag !== "REJECTED") {
    throw new Error("Missing content conflict fixture");
  }
  const previewCall = vi.spyOn(service, "preview").mockResolvedValue({
    ...rejection,
    value: { ...rejection.value, endpoint: "case.preview", outcome: rejection.value.outcome },
  });
  const execution = vi.spyOn(service, "execute");
  const view = editor();
  fill(view);
  await act(async () => {
    await view.result.current.preview();
  });
  expect(view.result.current.state.delivery).toBe("DEFINITELY_REJECTED");
  expect(view.result.current.state.unresolved).toBe(false);
  expect(view.result.current.state.requestBytes).toBe(previewCall.mock.calls[0]![0].bytes);
  expect(execution).not.toHaveBeenCalled();
});
