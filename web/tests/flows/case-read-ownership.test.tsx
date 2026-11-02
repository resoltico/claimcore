import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { TestProviders } from "../support/presentation-test-support";
import { current } from "../support/operation-form.fixtures";
import { caseViewInput } from "../support/inputs";

afterEach(() => vi.unstubAllGlobals());
const observation = (name: string, revision: number) =>
  caseViewInput({
    ...current,
    revision,
    state: {
      ...current.state,
      fields: {
        ...current.state.fields,
        registration: { ...current.state.fields.registration, claimantName: name },
      },
    },
  });
const response = (name: string, revision: number) =>
  new Response(
    JSON.stringify({
      outcome: { kind: "SUCCEEDED", data: observation(name, revision) },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );

it("rejects an obsolete case read after A to B to A and never rebases authored revision", async () => {
  const old = Promise.withResolvers<Response>();
  const fresh = Promise.withResolvers<Response>();
  const fetch = vi.fn().mockReturnValueOnce(old.promise).mockReturnValueOnce(fresh.promise);
  vi.stubGlobal("fetch", fetch);
  const { result, rerender } = renderHook(
    ({ credential }) =>
      useOperation({
        subject: "CASE",
        kind: "AMEND_REGISTRATION",
        current,
        credential,
        onLockChange: vi.fn(),
      }),
    { wrapper: TestProviders, initialProps: { credential: "A" } },
  );
  rerender({ credential: "B" });
  expect(result.current.disclosureCurrent).toBe(false);
  act(() => {
    result.current.revalidateDisclosure();
  });
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(1);
  });
  rerender({ credential: "A" });
  expect(result.current.disclosureCurrent).toBe(false);
  act(() => {
    result.current.revalidateDisclosure();
  });
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  await act(async () => {
    old.resolve(response("OBSOLETE_B", 8));
    await old.promise;
  });
  expect(result.current.currentCase).toBeNull();
  expect(result.current.revalidating).toBe(true);
  await act(async () => {
    fresh.resolve(response("CURRENT_A", 9));
    await fresh.promise;
  });
  await waitFor(() => {
    expect(result.current.disclosureCurrent).toBe(true);
  });
  expect(result.current.currentCase?.state.fields.registration.claimantName).toBe("CURRENT_A");
  expect(result.current.currentCase?.revision).toBe(9);
  expect(result.current.values["expectedRevision"]).toBe("4");
  expect(fetch.mock.calls.every(([path]) => path === "/api/case.get")).toBe(true);
});
