import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { CurrentCase } from "../src/api/v2";
import { useOperationEditor } from "../src/hooks/operation/useOperationEditor";
import { definition, fields, preparation, response } from "./v2-ui.fixtures";

const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

it("coalesces duplicate preparation activation while retention is pending", async () => {
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
    first = result.current.prepare();
    void result.current.prepare();
  });
  expect(fetch).toHaveBeenCalledOnce();
  await act(async () => {
    finish?.(
      response("command.prepare", "PREPARED", {
        details: preparation,
        review: {
          before: current.case,
          proposed: current.case,
          changes: [],
          context: definition.runtime,
          advisory: true,
        },
      }),
    );
    await first;
  });
  expect(result.current.state.delivery).toBe("REVIEWING");
});
