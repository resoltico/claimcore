import { it, expect } from "vitest";
import { resultNotice } from "../src/api/service";
import { createPresenter } from "../src/presentation/presenter";
import type { Fault } from "../src/generated/contracts/types";
it("diagnostics remain typed and never expose server prose", () => {
  const fault: Fault = {
    code: "STORE_UNAVAILABLE",
    diagnostic: { id: "CORE_STORE_UNAVAILABLE", parameters: {} },
    message: "PRIVATE_PROVIDER_CANARY",
    recommendedAction: "RETRY_SAFE",
  };
  const notice = resultNotice({
    kind: "outcome",
    status: 200,
    value: { endpoint: "case.get", outcome: { tag: "FAILED", data: fault } },
  });
  const en = createPresenter({ language: "en", displayLocale: "en-GB" }).notice(notice);
  const ar = createPresenter({ language: "ar", displayLocale: "en-GB" }).notice(notice);
  expect(en).not.toBe(ar);
  expect(en + ar).not.toContain("PRIVATE_PROVIDER_CANARY");
});
