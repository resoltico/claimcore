import { act, renderHook } from "@testing-library/react";
import { expect, it } from "vitest";
import { useReviewConsent } from "../src/hooks/operation/useReviewConsent";
import type { Review } from "../src/api/types";
import { review } from "./service-ui.fixtures";
it("confirmation belongs to one review and exact serialized request", () => {
  const { result, rerender } = renderHook<
    ReturnType<typeof useReviewConsent>,
    { subject: Review | null; identity: string | null }
  >(({ subject, identity }) => useReviewConsent(subject, identity), {
    initialProps: { subject: review, identity: "request" },
  });
  expect(result.current.confirmed).toBe(false);
  act(() => {
    result.current.setConfirmed(true);
  });
  rerender({ subject: review, identity: "request" });
  expect(result.current.confirmed).toBe(true);
  rerender({ subject: { ...review }, identity: "request" });
  expect(result.current.confirmed).toBe(false);
  act(() => {
    result.current.setConfirmed(true);
  });
  rerender({ subject: review, identity: "different" });
  expect(result.current.confirmed).toBe(false);
  rerender({ subject: null, identity: null });
  act(() => {
    result.current.setConfirmed(true);
  });
  expect(result.current.confirmed).toBe(false);
});
