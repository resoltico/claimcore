// @vitest-environment node
import { expect, it, vi } from "vitest";
import type { Page } from "@playwright/test";
import { noHorizontalOverflow } from "../../e2e/layout-support";

it("accepts the existing one-pixel document reflow tolerance", async () => {
  const page = {
    evaluate: vi.fn<Page["evaluate"]>().mockResolvedValue({ clientWidth: 320, scrollWidth: 321 }),
  };
  await expect(noHorizontalOverflow(page, "zoom")).resolves.toBeUndefined();
});
it("reports overflow without serializing DOM contents", async () => {
  const page = {
    evaluate: vi
      .fn<Page["evaluate"]>()
      .mockResolvedValue({ clientWidth: 320, scrollWidth: 400, privateValue: "PRIVATE_SENTINEL" }),
  };
  await expect(noHorizontalOverflow(page, "narrow")).rejects.toEqual(
    new Error("E2E_LAYOUT_NARROW_OVERFLOW"),
  );
});
