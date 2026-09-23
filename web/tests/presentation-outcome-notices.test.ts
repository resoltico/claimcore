import { expect, it } from "vitest";
import type { EndpointOutcome } from "../src/api/v2";
import type { Fault, RecoveryRejection } from "../src/generated/convergence/web-v2.types";
import { resultNotice } from "../src/api/v2";
import { createPresenter } from "../src/presentation/presenter";
import { operationId, preparation } from "./v2-ui.fixtures";

const fault: Fault = {
  code: "STORE_UNAVAILABLE",
  message: "PRIVATE_PROVIDER_CANARY",
  diagnostic: { id: "CORE_STORE_UNAVAILABLE", parameters: {} },
  recommendedAction: "RETRY_SAFE",
};
const rejection: RecoveryRejection = {
  code: "PREPARATION_DISMISSED",
  message: "PRIVATE_ENGLISH_CANARY",
  diagnostic: { id: "RECOVERY_PREPARATION_DISMISSED", parameters: {} },
  recommendedAction: "READ_CURRENT",
};

it("preserves diagnostics from direct read refusals and nested recovery execution faults without server prose", () => {
  const responses: EndpointOutcome[] = [
    { endpoint: "recovery.list", outcome: { tag: "FAILED", data: fault } },
    { endpoint: "recovery.inspect", outcome: { tag: "REJECTED", data: rejection } },
    { endpoint: "command.prepare", outcome: { tag: "FAILED", data: { operationId, fault } } },
    {
      endpoint: "recovery.resolve",
      outcome: {
        tag: "COMPLETED",
        data: {
          preparation: preparation.summary,
          attemptId: operationId,
          settlement: "CONFIRMED",
          execution: { tag: "FAILED_BEFORE_COMMIT", operationId, fault },
        },
      },
    },
  ];
  for (const value of responses) {
    const notice = resultNotice({ kind: "outcome", status: 200, value });
    expect(notice.kind).toBe("diagnostic");
    const en = createPresenter({ language: "en", displayLocale: "en-GB" }).notice(notice);
    const ar = createPresenter({ language: "ar", displayLocale: "lv-LV" }).notice(notice);
    expect(en).not.toBe(ar);
    expect(en + ar).not.toContain("PRIVATE_");
  }
});

it("keeps cancelled or non-diagnostic outcomes distinct from a translated core refusal", () => {
  for (const value of [
    { endpoint: "recovery.list", outcome: { tag: "CANCELLED", data: null } },
    {
      endpoint: "recovery.inspect",
      outcome: { tag: "SUCCEEDED", data: { tag: "NOT_FOUND", identity: operationId } },
    },
  ] satisfies EndpointOutcome[]) {
    expect(resultNotice({ kind: "outcome", status: 200, value })).toEqual({
      kind: "local",
      reason: "incomplete",
    });
  }
});
