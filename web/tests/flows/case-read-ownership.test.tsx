import {
  caseSubject,
  caseDocumentInput,
  registrationInputs,
} from "../support/case-document.fixtures";
import { prefixedInputs } from "../support/submission-inputs";
import { operationAffordance } from "../support/affordances.fixtures";

import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { TestProviders } from "../support/presentation-test-support";
import { current } from "../support/operation-form.fixtures";

import { fields } from "../support/service-ui.fixtures";

afterEach(() => vi.unstubAllGlobals());
const observation = (name: string, revision: number) =>
  caseDocumentInput({
    subject: { ...caseSubject, caseId: current.subject.caseId, revision },
    actions: [
      operationAffordance(
        "case.amendRegistration",
        {
          kind: "CASE",
          target: { kind: "ID", caseId: current.subject.caseId },
          expectedRevision: revision,
        },
        { ...prefixedInputs("field:", { ...fields.registration, claimantName: name }) },
        [...registrationInputs],
        {},
      ),
    ],
    history: "FULL",
  });
const response = (name: string, revision: number) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: observation(name, revision) },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );

it("rejects an obsolete case read after A to B to A and never rebases authored revision", async () => {
  const old = Promise.withResolvers<Response>();
  const fresh = Promise.withResolvers<Response>();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockReturnValueOnce(old.promise)
    .mockReturnValueOnce(fresh.promise);
  vi.stubGlobal("fetch", fetch);
  const { result, rerender } = renderHook(
    ({ credential }) =>
      useOperation({
        subject: "CASE",
        kind: "case.amendRegistration",
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
  expect(
    result.current.currentCase?.actions.find((action) => action.task === "case.amendRegistration")
      ?.seeds["field:claimantName"],
  ).toBe("CURRENT_A");
  expect(result.current.currentCase?.subject.revision).toBe(9);
  expect(result.current.bindingSource).toMatchObject({ kind: "CASE", expectedRevision: 4 });
  expect(fetch.mock.calls.every(([path]) => path === "/api/case.get")).toBe(true);
});
