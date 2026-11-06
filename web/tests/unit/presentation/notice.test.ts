import { expect, it } from "vitest";
import { renderNotice } from "../../../src/presentation/notice";
import { defaults } from "../../../src/presentation/preferences";
it("renders the core-declared required role instead of a capability token", () => {
  const value = renderNotice(defaults, {
    kind: "diagnostic",
    diagnostic: { code: "NOT_AUTHORIZED", action: "ASK_FOR_ACCESS", required: "RECORD_PAYMENT" },
  });
  expect(value).toBe("You need the Payment recorder role to make this change. Ask an owner.");
  expect(value).not.toContain("RECORD_PAYMENT");
});
it("renders alternative roles from the generated core policy", () => {
  const value = renderNotice(defaults, {
    kind: "diagnostic",
    diagnostic: { code: "NOT_AUTHORIZED", action: "ASK_FOR_ACCESS", required: "REGISTER" },
  });
  expect(value).toContain("Case registrar or Case handler");
});
