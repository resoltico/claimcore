import { describe, expect, it } from "vitest";
import type { Receipt } from "../src/api/v2";
import { acceptedReceipt, isLocked, prepared } from "../src/views/operation/editorSupport";
import { fields, operationId, preparation } from "./v2-ui.fixtures";
import { review } from "./v2-foundation.fixtures";

const receipt: Receipt = {
  operationId,
  snapshot: { fields, revision: "2" },
  recordedAt: "2026-09-09T00:00:00.0000000+00:00",
  recordedBy: "synthetic",
  replayed: false,
  command: "CLOSE",
};

const expectPreparedSelection = (): void => {
  expect(
    prepared({
      endpoint: "command.prepare",
      outcome: { tag: "PREPARED", data: { details: preparation, review } },
    }),
  ).toEqual({ details: preparation, review });
  expect(
    prepared({
      endpoint: "command.prepare",
      outcome: { tag: "CANCELLED_BEFORE_ADMISSION", data: { operationId } },
    }),
  ).toBeNull();
};

const expectAcceptedSelection = (): void => {
  expect(
    acceptedReceipt({
      endpoint: "command.execute",
      outcome: { tag: "OBSERVED_ACCEPTED", data: { receipt } },
    }),
  ).toEqual(receipt);
  expect(
    acceptedReceipt({
      endpoint: "command.execute",
      outcome: {
        tag: "COMPLETED",
        data: {
          preparation: preparation.summary,
          attemptId: operationId,
          execution: { tag: "ACCEPTED", receipt },
          settlement: "CONFIRMED",
        },
      },
    }),
  ).toEqual(receipt);
  expect(
    acceptedReceipt({
      endpoint: "command.execute",
      outcome: { tag: "CANCELLED_BEFORE_ATTEMPT", data: { preparation: preparation.summary } },
    }),
  ).toBeNull();
};

describe("operation editor support decoding", () => {
  it("classifies every delivery lock state", () => {
    expect(isLocked("PREPARING")).toBe(true);
    expect(isLocked("PREPARATION_UNKNOWN")).toBe(true);
    expect(isLocked("SUBMITTING")).toBe(true);
    expect(isLocked("OUTCOME_UNKNOWN")).toBe(true);
    expect(isLocked("EDITING")).toBe(false);
  });

  it("selects only prepared and accepted typed outcome variants", () => {
    expectPreparedSelection();
    expectAcceptedSelection();
  });
});
