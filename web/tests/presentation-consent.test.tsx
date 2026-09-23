import { act, renderHook } from "@testing-library/react";
import { expect, it } from "vitest";
import { usePreparedConsent } from "../src/hooks/operation/usePreparedConsent";
import type { PreparationDetails } from "../src/api/v2";
import { preparation } from "./v2-ui.fixtures";

it("binds consent to one reviewed preparation and exact identity rather than an editor-wide boolean", () => {
  const { result, rerender } = renderHook<
    ReturnType<typeof usePreparedConsent>,
    { subject: PreparationDetails | null }
  >(({ subject }: { subject: PreparationDetails | null }) => usePreparedConsent(subject), {
    initialProps: { subject: preparation },
  });
  expect(result.current.confirmed).toBe(false);
  act(() => result.current.setConfirmed(true));
  rerender({ subject: preparation });
  expect(result.current.confirmed).toBe(true);
  rerender({ subject: { ...preparation } });
  expect(result.current.confirmed).toBe(false);
  act(() => result.current.setConfirmed(true));
  expect(result.current.confirmed).toBe(true);
  act(() => result.current.setConfirmed(false));
  expect(result.current.confirmed).toBe(false);
  rerender({ subject: null });
  act(() => result.current.setConfirmed(true));
  expect(result.current.confirmed).toBe(false);
  rerender({
    subject: { ...preparation, summary: { ...preparation.summary, requestSha256: null } },
  });
  act(() => result.current.setConfirmed(true));
  expect(result.current.confirmed).toBe(false);
});
