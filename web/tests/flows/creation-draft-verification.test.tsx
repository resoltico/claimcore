import {
  installationActions,
  requesterLinks,
  activeRequester,
} from "../support/human-requester.fixtures";
import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { RequesterContext, type ActiveRequester } from "../../src/hooks/useRequesterContext";
import { service } from "../../src/api/service";
import { TestProviders } from "../support/presentation-test-support";

it("consumes a fresh successful requester observation once and never reuses its offer after a later refusal", () => {
  let requester: ActiveRequester = {
    ...activeRequester,
    actions: installationActions(["case.open"]),
    links: requesterLinks(["CASES", "PEOPLE", "OPERATIONS"], ["case.open"]),
  };
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  const view = renderHook(
    () =>
      useOperation({
        subject: "CASE",
        kind: "case.open",
        current: null,
        credential: "token",
        onLockChange: vi.fn(),
      }),
    {
      wrapper: ({ children }: { children: ReactNode }) => (
        <TestProviders>
          <RequesterContext value={requester}>{children}</RequesterContext>
        </TestProviders>
      ),
    },
  );
  expect(view.result.current.disclosureCurrent).toBe(true);
  act(() => {
    view.result.current.invalidateDisclosure();
  });
  expect(view.result.current.disclosureCurrent).toBe(false);
  view.rerender();
  expect(view.result.current.disclosureCurrent).toBe(false);
  requester = { ...requester };
  view.rerender();
  expect(view.result.current.disclosureCurrent).toBe(true);
  act(() => {
    view.result.current.invalidateDisclosure();
  });
  view.rerender();
  expect(view.result.current.disclosureCurrent).toBe(false);
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
