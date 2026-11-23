import { expect, type Page } from "@playwright/test";

export const expectNoInlineStyles = async (page: Page): Promise<void> => {
  // Report tag names only. Runtime markup can contain private form values.
  const elements = await page
    .locator("style, [style]")
    .evaluateAll((nodes) => nodes.map((node) => node.localName));
  expect(elements, "The workspace must use external stylesheets").toEqual([]);
};
