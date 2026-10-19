import { caseOffers } from "./case-advice.fixtures";
import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { useOperationEditor } from "../src/hooks/operation/useOperationEditor";
import { definition, fields, response, runtime } from "./service-ui.fixtures";

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
    useOperationEditor({
      token: "token",
      definition,
      current,
      initialCommand: "CLOSE",
      onClose: vi.fn(),
      onCommitted: vi.fn(),
      onMutationLockChange: vi.fn(),
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
      response("case.preview", "REVIEWED", {
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
  expect(result.current.state.delivery).toBe("REVIEWING");
});
