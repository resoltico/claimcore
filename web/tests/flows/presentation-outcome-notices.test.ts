import { it, expect } from "vitest";
import { resultNotice } from "../../src/api/service";
import { createPresenter } from "../../src/presentation/presenter";
import { isResponse } from "../../src/protocol/client";
import type { Fault } from "@contracts/client";
it("diagnostics remain typed and never expose server prose", () => {
  const fault: Fault = { code: "CORE_STORE_UNAVAILABLE", action: "RETRY_SAFE" };
  expect(
    isResponse("case.get", {
      outcome: { kind: "FAILED", data: { ...fault, message: "PRIVATE_PROVIDER_CANARY" } },
    }),
  ).toBe(false);
  const notice = resultNotice({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "FAILED", data: fault } },
  });
  const en = createPresenter({
    language: "en",
    displayLocale: "en-GB",
    mode: "system" as const,
    accent: "green" as const,
  }).notice(notice);
  const ar = createPresenter({
    language: "ar",
    displayLocale: "en-GB",
    mode: "system" as const,
    accent: "green" as const,
  }).notice(notice);
  expect(en).not.toBe(ar);
  expect(en + ar).not.toContain("PRIVATE_PROVIDER_CANARY");
});

it("admits and renders the payment-group capacity issue with its server-declared bound", () => {
  const reply: unknown = {
    operationId: "10000000-0000-7000-8000-000000000001",
    outcome: {
      kind: "REJECTED",
      data: {
        code: "INVALID_INPUT",
        action: "CORRECT_INPUT",
        issues: [{ code: "PAYMENT_COUNT_RANGE", field: "payment", maximumPayments: 50 }],
      },
    },
  };
  if (!isResponse("operation.execute", reply)) {
    throw new Error("The count issue must be admitted by the client contract");
  }
  const notice = resultNotice({ kind: "outcome", status: 200, value: reply });
  if (
    notice.kind !== "diagnostic" ||
    notice.diagnostic.code !== "INVALID_INPUT" ||
    !("issues" in notice.diagnostic)
  ) {
    throw new Error("Expected the admitted input issue diagnostic");
  }
  const text = createPresenter({
    language: "en",
    displayLocale: "en-GB",
    mode: "system",
    accent: "green",
  }).notice({ kind: "issues", issues: notice.diagnostic.issues });
  expect(text).toContain("at most 50 payment entries");
  expect(text).toContain("Remove an entry only if it was recorded in error.");
  expect(text).not.toContain("PAYMENT_COUNT_RANGE");
  expect(text).not.toContain("undefined");
});
