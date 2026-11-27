import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { endpointResponse } from "../../support/transport-response.fixtures";
import { consumerContractIdentity } from "@contracts/client";
import { useSession } from "../../../src/session/useSession";

const session = (data: Record<string, unknown>) =>
  endpointResponse("session", {
    outcome: {
      kind: "SUCCEEDED",
      view: { regions: [] },
      data: { contractIdentity: consumerContractIdentity, ...data },
    },
  });

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

const unsuccessful = [
  [
    "REJECTED",
    { code: "RESOURCE_UNAVAILABLE", action: "ASK_FOR_ACCESS" },
    { kind: "local", reason: "incomplete" },
  ],
  [
    "FAILED",
    {
      code: "CORE_STORE_UNAVAILABLE",
      action: "RETRY_SAFE",
      messageId: "fault.CORE_STORE_UNAVAILABLE",
    },
    {
      kind: "human",
      advice: {
        message: { messageId: "fault.CORE_STORE_UNAVAILABLE", arguments: [] },
        action: "RETRY_SAFE",
      },
    },
  ],
] as const;

it.each(unsuccessful)(
  "surfaces a valid %s session bootstrap outcome",
  async (tag, data, message) => {
    vi.mocked(fetch).mockResolvedValueOnce(
      endpointResponse("session", {
        outcome: { kind: tag, [tag === "REJECTED" ? "rejection" : "fault"]: data },
      }),
    );
    const { result } = renderHook(() => useSession());
    await waitFor(() => {
      expect(result.current.state).toMatchObject({
        kind: "failure",
        message,
      });
    });
  },
);

it.each(unsuccessful)(
  "surfaces a valid %s logout without claiming success",
  async (tag, data, message) => {
    vi.mocked(fetch).mockResolvedValueOnce(
      session({ authenticated: true, antiforgeryToken: "token" }),
    );
    const { result } = renderHook(() => useSession());
    await waitFor(() => {
      expect(result.current.state.kind).toBe("authenticated");
    });
    const previousEpoch = result.current.state.epoch;
    vi.mocked(fetch).mockResolvedValueOnce(
      endpointResponse("session.logout", {
        outcome: { kind: tag, [tag === "REJECTED" ? "rejection" : "fault"]: data },
      }),
    );
    await act(async () => {
      await result.current.logout();
    });
    expect(result.current.state).toMatchObject({
      kind: "failure",
      epoch: previousEpoch + 1,
      message,
    });
    expect("token" in result.current.state).toBe(false);
  },
);

it("fails a malformed authenticated session and leaves logout inert", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: null }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("failure");
  });
  await result.current.logout();
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("refuses an invalid refresh and clears credentials after confirmed logout", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: false, antiforgeryToken: "anonymous" }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("anonymous");
  });
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: null }));
  await result.current.refresh();
  await waitFor(() => {
    expect(result.current.state.kind).toBe("failure");
  });
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "token" }));
  await result.current.refresh();
  await waitFor(() => {
    expect(result.current.state.kind).toBe("authenticated");
  });
  fetch.mockResolvedValueOnce(
    endpointResponse("session.logout", {
      outcome: {
        kind: "SUCCEEDED",
        data: { kind: "PROVIDER", location: "https://issuer.example.test/logout" },
        view: { regions: [] },
      },
    }),
  );
  await result.current.logout();
  await waitFor(() => {
    expect(result.current.state).toMatchObject({ kind: "anonymous", token: null });
  });
});

it("does not send a credential from an anonymous session", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: false, antiforgeryToken: "anonymous" }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("anonymous");
  });
  expect(fetch).toHaveBeenCalledTimes(1);
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "new" }));
  await result.current.refresh();
  await waitFor(() => {
    expect(result.current.state).toMatchObject({ kind: "authenticated", token: "new" });
  });
});

it("shows logout transport failure as retriable and clears it after a session refresh", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "token" }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("authenticated");
  });
  fetch.mockRejectedValueOnce(new Error("Synthetic delivery loss"));
  await result.current.logout();
  await waitFor(() => {
    expect(result.current.state).toMatchObject({ kind: "failure" });
  });
  fetch.mockResolvedValueOnce(session({ authenticated: false, antiforgeryToken: "fresh" }));
  await result.current.refresh();
  await waitFor(() => {
    expect(result.current.state).toMatchObject({
      kind: "anonymous",
      token: "fresh",
      message: null,
    });
  });
});

it("confirmed session termination advances privacy state without a second deletion and cannot clear a replacement epoch", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "first" }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("authenticated");
  });
  const original = result.current.state.epoch;
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "replacement" }));
  await act(() => result.current.refresh());
  const replacement = result.current.state.epoch;
  act(() => {
    result.current.refreshAfterTermination(original);
  });
  expect(result.current.state).toMatchObject({
    kind: "authenticated",
    token: "replacement",
    epoch: replacement,
  });
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "same-current" }));
  act(() => {
    result.current.refreshAfterTermination(replacement);
  });
  await waitFor(() => {
    expect(result.current.state).toMatchObject({
      kind: "authenticated",
      token: "same-current",
      epoch: replacement + 1,
    });
  });
  expect(fetch).toHaveBeenCalledTimes(3);
});

it("publishes authentication and session termination even when the storage getter throws", async () => {
  const storage = vi.spyOn(window, "sessionStorage", "get").mockImplementation(() => {
    throw new Error("Storage getter denied");
  });
  try {
    vi.mocked(fetch).mockResolvedValueOnce(
      session({ authenticated: true, antiforgeryToken: "owned" }),
    );
    const { result } = renderHook(() => useSession());
    await waitFor(() => {
      expect(result.current.state).toEqual({ kind: "authenticated", token: "owned", epoch: 1 });
    });
    vi.mocked(fetch).mockResolvedValueOnce(
      session({ authenticated: false, antiforgeryToken: null }),
    );
    act(() => {
      result.current.refreshAfterTermination(1);
    });
    await waitFor(() => {
      expect(result.current.state).toMatchObject({ kind: "anonymous", epoch: 2 });
    });
  } finally {
    storage.mockRestore();
  }
});
