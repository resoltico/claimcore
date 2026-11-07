import type { PresentedValue } from "@contracts/client";

/** Canonical clipboard text uses supplied values and never the viewer's display locale. */
export const canonicalPresented = (value: PresentedValue): string => {
  switch (value.kind) {
    case "MONEY":
    case "MONEY_TOTAL":
      return `${value.amount} ${value.currency}`;
    case "ACTOR":
      return value.value.actorId;
    case "STATE":
    case "CODE":
      return value.token;
    case "CODES":
      return value.tokens.join(", ");
    case "MESSAGE":
      return value.messageId;
    case "ABSENT":
      return "null";
    default:
      return String(value.value);
  }
};
