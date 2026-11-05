import { messageShapes } from "@contracts/messages";
import type { PresentedValue, ReviewPresentation } from "@contracts/client";

const shapes: Readonly<Record<string, Readonly<Record<string, unknown>>>> = messageShapes;
const plainMessage = (key: string) =>
  Object.hasOwn(shapes, key) && Object.keys(shapes[key]!).length === 0;
const valuePresentable = (value: PresentedValue) => {
  switch (value.kind) {
    case "ABSENT":
      return plainMessage(value.reasonId);
    case "MESSAGE":
      return plainMessage(value.messageId);
    default:
      return true;
  }
};

/** Renderability is an interface boundary, independent of the core's authoritative value delta. */
export const isReviewPresentationComplete = (value: ReviewPresentation): boolean =>
  value.changes.every(
    (entry) =>
      plainMessage(entry.labelId) &&
      valuePresentable(entry.before) &&
      valuePresentable(entry.after),
  ) && value.unchanged.every((entry) => plainMessage(entry.labelId));
