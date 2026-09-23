import { useState } from "react";
import type { PreparationDetails } from "../../api/v2";

type Consent = { subject: PreparationDetails; identity: string };
/** Consent belongs to this review instance and exact identity, never to a language or a later draft. */
export const usePreparedConsent = (preparation: PreparationDetails | null) => {
  const [consent, setConsent] = useState<Consent | null>(null);
  const digest = preparation?.summary.requestSha256;
  const identity =
    preparation !== null && typeof digest === "string"
      ? `${preparation.summary.operationId}/${digest}`
      : null;
  return {
    confirmed:
      identity !== null && consent?.subject === preparation && consent.identity === identity,
    setConfirmed: (value: boolean): void => {
      setConsent(
        value && preparation !== null && identity !== null
          ? { subject: preparation, identity }
          : null,
      );
    },
  };
};
