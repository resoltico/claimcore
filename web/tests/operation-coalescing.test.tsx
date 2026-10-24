import { caseOffers } from "./case-advice.fixtures";
import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { useOperation } from "../src/hooks/operation/useOperation";
import { fields, response, runtime } from "./service-ui.fixtures";

const current: CurrentCase = {
  caseReference: fields.caseReference,
  revision: 1,
  advice: { acts: caseOffers(["CLOSE"]), history: ["SUMMARY", "FULL"] },

  state: { tag: "LIVE", fields, disposition: "ACTIVE" },
};

it("coalesces duplicate preview activation while retention is pending", async () => {
  let finish: ((value: Response) => void) | undefined;
  const pending = new Promise<Response>((resolve) => {
    finish = resolve;
  });
  const fetch = vi.fn(() => pending);
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() =>
    useOperation({
      token: "token",
      current,
      kind: "CLOSE",
      scope: "CASE",
      onLockChange: vi.fn(),
    }),
  );
  let first: Promise<void> = Promise.resolve();
  act(() => {
    first = result.current.preview();
    void result.current.preview();
  });
  expect(fetch).toHaveBeenCalledOnce();
  await act(async () => {
    finish?.(
      response("operation.preview", "REVIEWED", {
        review: {
          before: {
            caseReference: current.caseReference,
            revision: 1,
            state: { tag: "LIVE", disposition: "ACTIVE", fields },
          },
          after: {
            caseReference: current.caseReference,
            revision: 2,
            state: { tag: "LIVE", disposition: "ACTIVE", fields },
          },
          changes: [],
          context: runtime,
        },
      }),
    );
    await first;
  });
  expect(result.current.phase).toBe("REVIEWING");
});
