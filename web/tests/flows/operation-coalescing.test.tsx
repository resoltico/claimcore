import { caseDocumentInput } from "../support/case-document.fixtures";
import { recoveryOptions } from "../support/recovery.fixtures";

import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { previewReply } from "../support/presentation-state.fixtures";

const current: CurrentCase = caseDocumentInput({
  tasks: ["CASE:CLOSE"],
  caseId: "20000000-0000-4000-8000-000000000001",
  revision: 1,
  history: "FULL",
});
const closePreview = () => previewReply();
it("coalesces duplicate preview activation while retention is pending", async () => {
  let finish: ((value: Response) => void) | undefined;
  const pending = new Promise<Response>((resolve) => {
    finish = resolve;
  });
  const fetch = vi.fn(() => pending);
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() =>
    useOperation({
      ...recoveryOptions,
      credential: "token",
      current,
      kind: "CLOSE",
      subject: "CASE",
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
    finish?.(closePreview());
    await first;
  });
  expect(result.current.notice).toBeNull();
  expect(result.current.phase).toBe("REVIEWING");
});
