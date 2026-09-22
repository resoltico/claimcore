import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, expect, it, vi } from "vitest";
import { useDefinition } from "../src/hooks/useDefinition";
import { definition, response } from "./v2-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("accepts the matching generated definition fingerprint", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("definition", "DESCRIBED", definition),
  );
  const { result } = renderHook(() => useDefinition(1));
  await waitFor(() => expect(result.current.definition).toEqual(definition));
  expect(result.current.message).toBeNull();
});

it("clears claimant definition state when the server rejects or mismatches assets", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        kind: "HOST_FAILURE",
        code: "WEB_BUSY",
        status: 429,
        diagnostic: { id: "WEB_HOST_BUSY", parameters: {} },
        message: "Busy",
        executionPhase: null,
      }),
      { status: 429, headers: { "content-type": "application/json" } },
    ),
  );
  const { result, rerender } = renderHook(({ epoch }) => useDefinition(epoch), {
    initialProps: { epoch: 1 },
  });
  await waitFor(() => expect(result.current.message).toContain("WEB_BUSY"));
  fetch.mockResolvedValueOnce(
    response("definition", "DESCRIBED", { ...definition, webFingerprint: "0".repeat(64) }),
  );
  rerender({ epoch: 2 });
  await waitFor(() => expect(result.current.message).toContain("does not match"));
  expect(result.current.definition).toBeNull();
});

it("discards an aborted definition response when session epoch changes", async () => {
  let completeFirst: ((value: Response) => void) | undefined;
  const first = new Promise<Response>((resolve) => {
    completeFirst = resolve;
  });
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(first);
  fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
  const { result, rerender } = renderHook(({ epoch }) => useDefinition(epoch), {
    initialProps: { epoch: 1 },
  });
  rerender({ epoch: 2 });
  completeFirst?.(
    response("definition", "DESCRIBED", { ...definition, webFingerprint: "0".repeat(64) }),
  );
  await waitFor(() => expect(result.current.definition).toEqual(definition));
  expect(result.current.message).toBeNull();
});
