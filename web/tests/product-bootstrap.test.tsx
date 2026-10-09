import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useSession } from "../src/hooks/useSession";
import { productMetadata } from "../src/generated/contracts/web-v3.product-metadata";
import { response } from "./v3-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("admits matching public session metadata without protected definition access [CC-WEB-001]", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("session", "SNAPSHOT", { authenticated: true, antiforgeryToken: "synthetic-token" }),
  );
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("authenticated");
  });
  expect(globalThis.fetch).toHaveBeenCalledOnce();
  expect(vi.mocked(globalThis.fetch).mock.calls[0]?.[0]).toBe("/api/v3/session");
  expect(Object.keys(productMetadata).sort()).toEqual([
    "definition",
    "productVersion",
    "semanticFingerprint",
    "webFingerprint",
  ]);
  expect(productMetadata.definition.fields).toHaveLength(13);
  expect(productMetadata.productVersion).not.toBe("");
});

it("refuses a host fingerprint mismatch without claiming an invalid actor session [CC-WEB-001]", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("session", "SNAPSHOT", {
      authenticated: true,
      antiforgeryToken: "synthetic-token",
      webFingerprint: "0".repeat(64),
    }),
  );
  const { result } = renderHook(() => useSession());
  await waitFor(() => {
    expect(result.current.state.kind).toBe("failure");
  });
  expect(result.current.state).toMatchObject({
    message: { kind: "local", reason: "definitionMismatch" },
  });
});

it("keeps newer session admission when an older fingerprint reply arrives [CC-WEB-001]", async () => {
  let complete: (value: Response) => void = () => undefined;
  const first = new Promise<Response>((resolve) => {
    complete = resolve;
  });
  vi.mocked(globalThis.fetch)
    .mockReturnValueOnce(first)
    .mockResolvedValueOnce(
      response("session", "SNAPSHOT", { authenticated: true, antiforgeryToken: "current-token" }),
    );
  const { result } = renderHook(() => useSession());
  await act(async () => {
    await result.current.refresh();
  });
  complete(
    response("session", "SNAPSHOT", {
      authenticated: true,
      antiforgeryToken: "old-token",
      webFingerprint: "0".repeat(64),
    }),
  );
  await waitFor(() => {
    expect(result.current.state).toMatchObject({ kind: "authenticated", token: "current-token" });
  });
});
