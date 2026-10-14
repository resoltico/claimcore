import { useState } from "react";
import type { Review } from "../../api/service";
/** Confirmation belongs to this exact review instance and request identity. */
export const useReviewConsent = (review: Review | null, identity: string | null) => {
  const [consent, setConsent] = useState<{ review: Review; identity: string } | null>(null);
  return {
    confirmed:
      review !== null &&
      identity !== null &&
      consent?.review === review &&
      consent.identity === identity,
    setConfirmed: (value: boolean) => {
      setConsent(value && review !== null && identity !== null ? { review, identity } : null);
    },
  };
};
