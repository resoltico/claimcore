import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useSession } from "../src/hooks/useSession";

const session = (data: unknown) =>
  new Response(JSON.stringify({ endpoint: "session", outcome: { tag: "SNAPSHOT", data } }), {
    headers: { "content-type": "application/json" },
  });

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("fails a malformed authenticated session and leaves invalid mutation calls inert", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: null }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => expect(result.current.state.kind).toBe("failure"));
  await result.current.login("ignored");
  await result.current.logout();
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("surfaces invalid snapshots after login and logout responses", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: false, antiforgeryToken: "anonymous" }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => expect(result.current.state.kind).toBe("anonymous"));
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: null }));
  await result.current.login("credential");
  await waitFor(() => expect(result.current.state.kind).toBe("failure"));
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "token" }));
  await result.current.refresh();
  await waitFor(() => expect(result.current.state.kind).toBe("authenticated"));
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: null }));
  await result.current.logout();
  await waitFor(() => expect(result.current.state.kind).toBe("failure"));
});

it("keeps the anonymous token after a rejected credential so login can be retried", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: false, antiforgeryToken: "anonymous" }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => expect(result.current.state.kind).toBe("anonymous"));
  fetch.mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        kind: "HOST_FAILURE",
        code: "WEB_LOGIN_REJECTED",
        status: 401,
        diagnostic: { id: "WEB_HOST_LOGIN_REJECTED", parameters: {} },
        message: "Credential was rejected.",
        executionPhase: "NOT_STARTED",
      }),
      { status: 401, headers: { "content-type": "application/json" } },
    ),
  );
  await result.current.login("invalid");
  await waitFor(() =>
    expect(result.current.state).toMatchObject({
      kind: "anonymous",
      token: "anonymous",
      message: "WEB_LOGIN_REJECTED: Credential was rejected.",
    }),
  );
  fetch.mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        endpoint: "session.login",
        outcome: { tag: "SNAPSHOT", data: { authenticated: true, antiforgeryToken: "new" } },
      }),
      { headers: { "content-type": "application/json" } },
    ),
  );
  await result.current.login("valid");
  await waitFor(() =>
    expect(result.current.state).toMatchObject({ kind: "authenticated", token: "new" }),
  );
});

it("shows logout transport failure as retriable and clears it after a session refresh", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(session({ authenticated: true, antiforgeryToken: "token" }));
  const { result } = renderHook(() => useSession());
  await waitFor(() => expect(result.current.state.kind).toBe("authenticated"));
  fetch.mockRejectedValueOnce(new Error("Synthetic delivery loss"));
  await result.current.logout();
  await waitFor(() => expect(result.current.state).toMatchObject({ kind: "failure" }));
  fetch.mockResolvedValueOnce(session({ authenticated: false, antiforgeryToken: "fresh" }));
  await result.current.refresh();
  await waitFor(() =>
    expect(result.current.state).toMatchObject({
      kind: "anonymous",
      token: "fresh",
      message: null,
    }),
  );
});
