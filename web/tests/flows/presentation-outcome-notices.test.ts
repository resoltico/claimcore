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
  const en = createPresenter({ language: "en", displayLocale: "en-GB" }).notice(notice);
  const ar = createPresenter({ language: "ar", displayLocale: "en-GB" }).notice(notice);
  expect(en).not.toBe(ar);
  expect(en + ar).not.toContain("PRIVATE_PROVIDER_CANARY");
});
