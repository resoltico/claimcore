import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useSession } from "../../src/session/useSession";
import { productMetadata } from "../../src/protocol/productMetadata";
import { endpointResponse } from "../support/transport-response.fixtures";
import { consumerContractIdentity } from "@contracts/client";

const sessionReply = (antiforgeryToken: string, contractIdentity = consumerContractIdentity) =>
  endpointResponse("session", {
    outcome: {
      kind: "SUCCEEDED",
      view: { regions: [] },
      data: { authenticated: true, antiforgeryToken, contractIdentity },
    },
  });
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("admits matching public session metadata without protected definition access", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(sessionReply("synthetic-token"));
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("authenticated");
  });
  expect(globalThis.fetch).toHaveBeenCalledOnce();
  expect(vi.mocked(globalThis.fetch).mock.calls[0]?.[0]).toBe("/api/session");
  expect(Object.keys(productMetadata).sort()).toEqual([
    "catalog",
    "contractIdentity",
    "productVersion",
  ]);
  expect(productMetadata.productVersion).not.toBe("");
});

it("refuses a host fingerprint mismatch without claiming an invalid actor session", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    sessionReply("synthetic-token", "0".repeat(64)),
  );
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("failure");
  });
  expect(result.current.state).toMatchObject({
    message: { kind: "local", reason: "definitionMismatch" },
  });
});

it("keeps newer session admission when an older fingerprint reply arrives", async () => {
  let complete: (value: Response) => void = () => undefined;
  const first = new Promise<Response>((resolve) => {
    complete = resolve;
  });
  vi.mocked(globalThis.fetch)
    .mockReturnValueOnce(first)
    .mockResolvedValueOnce(sessionReply("current-token"));
  const { result } = renderHook(() => useSession());
  await act(async () => {
    await result.current.refresh();
  });
  complete(sessionReply("old-token", "0".repeat(64)));
  await waitFor(() => {
    expect(result.current.state).toMatchObject({ kind: "authenticated", token: "current-token" });
  });
});
