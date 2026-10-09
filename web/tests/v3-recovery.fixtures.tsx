import { vi } from "vitest";
import type { RecoveryActions } from "../src/views/recovery/RecoveryState";
import { fireEvent } from "./presentation-test-support";
import type { Rejection } from "../src/api/v3";
import { fields, operationId, preparation, recoveryPage, response } from "./v3-ui.fixtures";

export const preview = {
  artifactKind: "ENVELOPE",
  sourceSha256: "c".repeat(64),
  decodedEffect: {
    operationId,
    caseReference: "CASE-1",
    command: "CLOSE",
    expectedRevision: "1",
    authoredValues: [],
    canonicalCommandFormat: 3,
    requestSha256: "b".repeat(64),
  },
  existingPreparation: null,
};

export const list = (items: unknown[] = [preparation.summary]) =>
  response("recovery.list", "SUCCEEDED", recoveryPage(items));

export const receipt = {
  operationId,
  snapshot: { fields, revision: "1" },
  recordedAt: "2026-09-09T00:00:00.0000000+00:00",
  recordedBy: "synthetic",
  replayed: false,
  command: "CLOSE",
};

export const inspection = (tag = "NOT_FOUND") =>
  response("recovery.inspect", "SUCCEEDED", {
    tag: "FOUND",
    value: {
      tag: "RETAINED",
      value: {
        preparation,
        observation:
          tag === "FOUND"
            ? { tag: "FOUND", value: receipt }
            : { tag: "NOT_FOUND", identity: operationId },
      },
    },
  });

export const accepted = () =>
  response("recovery.resolve", "COMPLETED", {
    preparation: preparation.summary,
    attemptId: operationId,
    execution: {
      tag: "ACCEPTED",
      receipt,
    },
    settlement: "CONFIRMED",
  });

export const rejected = () =>
  response("recovery.resolve", "COMPLETED", {
    preparation: preparation.summary,
    attemptId: operationId,
    execution: {
      tag: "REJECTED",
      operationId,
      rejection: {
        code: "VERSION_CONFLICT",
        message: "Synthetic version conflict.",
        diagnostic: { id: "CASE_REVISION_CONFLICT", parameters: {} },
        field: null,
        actualRevision: "2",
        recommendedAction: "READ_CURRENT",
      } satisfies Rejection,
    },
    settlement: "CONFIRMED",
  });

export const selectFile = (input: Element, file: File): void => {
  Object.defineProperty(input, "files", {
    configurable: true,
    value: { 0: file, length: 1, item: (index: number) => (index === 0 ? file : null) },
  });
  fireEvent.change(input);
};

export const importFile = (container: HTMLElement, index: number, type: string): File => {
  const input = container.querySelectorAll('input[type="file"]')[index];
  if (input === undefined) {
    throw new Error("Recovery import control was not rendered.");
  }
  return new File(["{}"], "recovery.json", { type });
};

export const mockRecoveryActions = (): RecoveryActions => ({
  inspectId: vi.fn(),
  inspect: vi.fn(),
  loadAttempts: vi.fn(),
  choose: vi.fn(),
  act: vi.fn(),
  exportItem: vi.fn(),
  exportIdentity: vi.fn(),
  clearMessage: vi.fn(),
  preview: vi.fn(),
  retain: vi.fn(),
});
