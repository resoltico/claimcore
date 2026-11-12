import { it, expect } from "vitest";
import { resultNotice } from "../../src/api/service";
import { createPresenter } from "../../src/presentation/presenter";
it("local transport cancellation is translated distinctly from missing information", () => {
  const notice = resultNotice({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "cancelled" },
    dispatched: true,
  });
  expect(notice).toEqual({ kind: "local", reason: "cancelled" });
  if (notice.kind !== "local") {
    throw new Error("Expected local cancellation notice");
  }
  for (const language of ["en", "lv", "ar"] as const) {
    const p = createPresenter({
      language,
      displayLocale: "en-GB",
      mode: "system" as const,
      accent: "green" as const,
    });
    expect(p.notice(notice)).not.toBe(p.notice({ kind: "local", reason: "incomplete" }));
  }
});
