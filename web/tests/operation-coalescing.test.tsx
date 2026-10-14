import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { useOperationEditor } from "../src/hooks/operation/useOperationEditor";
import { definition, fields, digest, response, runtime } from "./service-ui.fixtures";

const current: CurrentCase = {
  state: "LIVE",
  disposition: "ACTIVE",
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

it("coalesces duplicate digest activation while retention is pending", async () => {
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
      response("command.preview", "REVIEWED", {
        requestSha256: digest,
        review: {
          before: current.case,
          proposed: current.case,
          changes: [],
          context: runtime,
          advisory: true,
        },
      }),
    );
    await first;
  });
  expect(result.current.state.delivery).toBe("REVIEWING");
});
