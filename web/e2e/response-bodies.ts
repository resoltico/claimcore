import type { Page } from "@playwright/test";
const responseBytes = 16 * 1024 * 1024;

const configured = new WeakMap<Page, Promise<void>>();

const configure = async (page: Page): Promise<void> => {
  if (page.context().browser()?.browserType().name() !== "chromium") {
    return;
  }
  // The native page/context lifetime closes this session and its retained bodies.
  const session = await page.context().newCDPSession(page);
  await session.send("Network.configureDurableMessages", {
    maxTotalBufferSize: 2 * responseBytes,
    maxResourceBufferSize: responseBytes,
  });
};

/** Preserve the original streamed reply for the existing native HTTP-body assertions. */
export const retainResponseBodies = (page: Page): Promise<void> => {
  const existing = configured.get(page);
  if (existing !== undefined) {
    return existing;
  }
  const pending = configure(page);
  configured.set(page, pending);
  return pending;
};
