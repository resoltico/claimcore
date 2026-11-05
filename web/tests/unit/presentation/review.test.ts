import { expect, it } from "vitest";
import type { ReviewPresentation } from "@contracts/client";
import { isReviewPresentationComplete } from "../../../src/presentation/review";

const known: ReviewPresentation = {
  changes: [
    {
      key: "money",
      labelId: "field.payableAmount.label",
      before: { kind: "ABSENT", reasonId: "label.notRecorded" },
      after: { kind: "MESSAGE", messageId: "lifecycle.ERASED.meaning" },
    },
  ],
  unchanged: [{ key: "claimant", labelId: "field.claimantName.label" }],
};
it("admits registered argument-free labels and explicit value messages without comparing values", () => {
  expect(isReviewPresentationComplete(known)).toBe(true);
  expect(isReviewPresentationComplete({ changes: [], unchanged: [] })).toBe(true);
});
it.each(["field.missing.label", "diagnostic.TOO_LONG", "ui.signOut"])(
  "rejects unrenderable label or value message %s",
  (key) => {
    const change = known.changes[0]!;
    expect(isReviewPresentationComplete({ ...known, changes: [{ ...change, labelId: key }] })).toBe(
      false,
    );
    expect(
      isReviewPresentationComplete({ ...known, unchanged: [{ key: "unknown", labelId: key }] }),
    ).toBe(false);
    expect(
      isReviewPresentationComplete({
        ...known,
        changes: [{ ...change, before: { kind: "ABSENT", reasonId: key } }],
      }),
    ).toBe(false);
    expect(
      isReviewPresentationComplete({
        ...known,
        changes: [{ ...change, after: { kind: "MESSAGE", messageId: key } }],
      }),
    ).toBe(false);
  },
);
