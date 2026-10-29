import type { Locator, Page } from "@playwright/test";

/** Drive the same segmented calendar control as a person, without editing hidden wire inputs. */
export const fillControl = async (control: Locator, value: string): Promise<void> => {
  await control.waitFor({ state: "visible" });
  const segments = control.getByRole("spinbutton", { includeHidden: true });
  if ((await segments.count()) === 0) {
    await control.fill(value);
    if ((await control.getAttribute("role")) === "combobox") {
      await control.press("Tab");
    }
    return;
  }
  const parts = /^(\d{4})-(\d{2})-(\d{2})$/u.exec(value);
  for (const [type, text] of [
    ["year", parts?.[1]],
    ["month", parts?.[2]],
    ["day", parts?.[3]],
  ] as const) {
    const segment = control.locator(`[data-type="${type}"]`);
    await segment.focus();
    if (text === undefined) {
      for (let index = 0; index < 4; index += 1) {
        await segment.press("Delete");
      }
    } else {
      await segment.pressSequentially(text);
    }
  }
};
export const fillNamedField = async (page: Page, name: string, value: string): Promise<void> => {
  await page.getByLabel(name, { exact: true }).first().waitFor({ state: "visible" });
  const group = page.getByRole("group", { name, exact: true, includeHidden: true });
  const combo = page.getByRole("combobox", { name, exact: true, includeHidden: true });
  if ((await group.count()) === 1) {
    await fillControl(group, value);
    return;
  }
  if ((await combo.count()) === 1) {
    await fillControl(combo, value);
    return;
  }
  await fillControl(page.getByLabel(name, { exact: true }), value);
};
export const fillBusinessField = (page: Page, name: string, value: string): Promise<void> =>
  fillControl(page.locator(`[data-field-name="${name}"]:not(.field-row)`), value);
