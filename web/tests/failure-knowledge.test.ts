import { it, expect } from "vitest";
import { resultNotice } from "../src/api/service";
import { createPresenter } from "../src/presentation/presenter";
it("cancellation is translated and distinct from missing information", () => {
  const notice = resultNotice({
    kind: "outcome",
    status: 200,
    value: { endpoint: "command.execute", outcome: { tag: "CANCELLED", data: null } },
  });
  expect(notice).toEqual({ kind: "local", reason: "cancelled" });
  for (const language of ["en", "lv", "ar"] as const) {
    const p = createPresenter({ language, displayLocale: "en-GB" });
    expect(p.notice(notice)).not.toBe(p.notice({ kind: "local", reason: "incomplete" }));
  }
});
