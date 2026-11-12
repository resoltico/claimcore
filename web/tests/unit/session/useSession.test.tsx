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
  ["REJECTED", { code: "RESOURCE_UNAVAILABLE", action: "ASK_FOR_ACCESS" }],
  ["FAILED", { code: "CORE_STORE_UNAVAILABLE", action: "RETRY_SAFE" }],
] as const;

it.each(unsuccessful)("surfaces a valid %s session bootstrap outcome", async (tag, data) => {
  vi.mocked(fetch).mockResolvedValueOnce(
    endpointResponse("session", { outcome: { kind: tag, data } }),
  );
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state).toMatchObject({
      kind: "failure",
      message: { kind: "diagnostic", diagnostic: { code: data.code } },
    });
  });
});

it.each(unsuccessful)("surfaces a valid %s logout without claiming success", async (tag, data) => {
  vi.mocked(fetch).mockResolvedValueOnce(
    session({ authenticated: true, antiforgeryToken: "token" }),
  );
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("authenticated");
  });
  const previousEpoch = result.current.state.epoch;
  vi.mocked(fetch).mockResolvedValueOnce(
    endpointResponse("session.logout", { outcome: { kind: tag, data } }),
  );
  await act(async () => {
    await result.current.logout();
  });
  expect(result.current.state).toMatchObject({
    kind: "failure",
    epoch: previousEpoch + 1,
    message: { kind: "diagnostic", diagnostic: { code: data.code } },
  });
  expect("token" in result.current.state).toBe(false);
});

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

it("surfaces invalid snapshots after session refresh and logout responses", async () => {
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
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: null }));
  await result.current.logout();
  await waitFor(() => {
    expect(result.current.state.kind).toBe("failure");
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
